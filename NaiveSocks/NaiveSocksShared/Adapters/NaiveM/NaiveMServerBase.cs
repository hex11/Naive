using System;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Collections.Generic;
using System.Linq;

namespace NaiveSocks
{
    public class NaiveMServerBase : InAdapter
    {
        internal HashSet<NaiveMChannels> nmsList = new HashSet<NaiveMChannels>();
        Dictionary<string, ImuxSession> imuxSessions = new Dictionary<string, ImuxSession>();

        public int timeout { get; set; } = 120;

        public bool fastopen { get; set; } = true;

        public class Settings
        {
            [NotConf]
            public byte[] realKey { get; set; }
            [NotConf]
            public Func<string, INetwork> networkProvider { get; set; }
            public int imux_max { get; set; } = -1;
            public AdapterRef @out { get; set; }

            public virtual INetwork GetNetwork(string str)
            {
                return networkProvider?.Invoke(str);
            }
        }

        public async Task HandleRequestAsync(HttpConnection p, Settings settings, string token)
        {
            try {
                if (token == null)
                    throw new ArgumentNullException(nameof(token));

                var realKey = settings.realKey;
                NaiveProtocol.Request req;
                try {
                    var bytes = Convert.FromBase64String(token);
                    if (realKey != null) {
                        bytes = NaiveProtocol.EncryptOrDecryptBytes(false, realKey, bytes);
                    }
                    req = NaiveProtocol.Request.Parse(bytes);
                } catch (Exception e) {
                    Logger.exception(e, Logging.Level.Warning, "parsing token");
                    return;
                }

                try {
                    p.Handled = true;

                    var r = await GetMsgStream(p, settings, req);
                    if (r == null) return;
                    var (msgStream, imux) = r;

                    var nms = new NaiveMChannels(new NaiveMultiplexing(msgStream)) {
                        Adapter = this,
                        InConnectionFastCallback = fastopen,
                        Logger = this.Logger
                    };
                    nms.ConnectionHandler = (settings.@out != null) ? settings.@out.Adapter : @out.Adapter;
                    nms.NetworkProvider = settings.GetNetwork;
                    lock (nmsList)
                        nmsList.Add(nms);
                    try {
                        if (req.extraStrings.Length > 1) {
                            nms.PerChannelEncryption = req.extraStrings[1];
                            if (nms.PerChannelEncryption.IsNullOrEmpty() == false)
                                nms.PerChannelEncryptionKey = realKey;
                        }
                        await nms.Start();
                    } finally {
                        lock (nmsList)
                            nmsList.Remove(nms);
                        if (imux != null) {
                            lock (imuxSessions)
                                imuxSessions.Remove(imux.SessionId);
                            imux.WhenEnd.SetResult(null);
                        }
                    }
                } catch (Exception e) {
                    Logger.exception(e, Logging.Level.Error, "NaiveMHandler From: " + p.myStream + " Url: " + p.Url);
                }
            } finally {
                if (p.ConnectionState == HttpConnection.States.Processing) {
                    p.Handled = false;
                }
            }
        }

        private async Task<Tuple<IMsgStream, ImuxSession>> GetMsgStream(HttpConnection p, Settings settings, NaiveProtocol.Request req)
        {
            var realKey = settings.realKey;

            const string XumPrefix = "chs2:";
            bool isImux = req.additionalString.StartsWith(XumPrefix);
            ImuxSession imux = null;
            string encryptType = "";
            if (req.extraStrings.Length > 0) {
                encryptType = req.extraStrings[0];
            }
            if (!isImux && req.additionalString != "channels") {
                Logger.warning($"{p.remoteEP}: protocol not supported");
                return null;
            }
            IMsgStream msgStream = null;
            if (isImux) {
                var arr = NaiveUtils.DeserializeArray(req.additionalString.Substring(XumPrefix.Length));
                var sessionId = arr[0];
                int wsCount = Int32.Parse(arr[1]), wssoCount = 0, httpCount = 0;
                var connId = Int32.Parse(arr[2]);
                if (arr.Count > 3) {
                    wssoCount = Int32.Parse(arr[3]);
                    httpCount = Int32.Parse(arr[4]);
                }
                var connCount = wsCount + wssoCount + httpCount;
                int imuxMax = settings.imux_max;
                if (imuxMax < 0)
                    imuxMax = 16;
                if (connCount > imuxMax) {
                    Logger.warning($"{p.remoteEP}: IMUX count requesting ({connCount}) > imux_max ({imuxMax})");
                    return null;
                }
                IMsgStream wsOrHttp;
                if (connId < connCount - httpCount) {
                    wsOrHttp = await HandleWebsocket(p, realKey, encryptType);
                } else {
                    p.setStatusCode("200 OK");
                    p.setHeader(HttpHeaders.KEY_Transfer_Encoding, HttpHeaders.VALUE_Transfer_Encoding_chunked);
                    await p.EndResponseAsync();
                    var baseStream = MyStream.FromStream(p.SwitchProtocol());
                    var msf = new HttpChunkedEncodingMsgStream(baseStream);
                    NaiveProtocol.ApplyEncryption(msf, realKey, encryptType);
                    wsOrHttp = msf;
                }
                lock (imuxSessions) {
                    if (imuxSessions.TryGetValue(sessionId, out imux) == false) {
                        imux = new ImuxSession(sessionId, connCount) {
                            WsCount = wsCount,
                            WssoCount = wssoCount,
                            HttpCount = httpCount
                        };
                        imuxSessions.Add(sessionId, imux);
                        NaiveUtils.SetTimeout(10 * 1000, () => {
                            if (imux.ConnectedCount != imux.Count) {
                                Logger.warning($"IMUX (id={imux.SessionId}, count={imux.ConnectedCount}/{imux.Count}) timed out");
                                imux.WhenEnd.SetResult(null);
                            }
                        });
                    }
                    if (imux.HandleConnection(wsOrHttp, connId)) {
                        // all connections are connected to the imux session, return the msg stream.
                        msgStream = imux.MuxStream;
                    }
                }

                if (msgStream == null) {
                    // wait for the end of the imux session, prevent it from being disconncected.
                    await imux.WhenEnd.Task;
                    return null;
                }
            } else {
                msgStream = await HandleWebsocket(p, realKey, encryptType);
            }
            return Tuple.Create(msgStream, imux);
        }

        private async Task<WebSocketServer> HandleWebsocket(HttpConnection p, byte[] realKey, string encType)
        {
            var ws = new WebSocketServer(p);
            ws.AddToManaged(timeout / 2, timeout);
            NaiveProtocol.ApplyEncryption(ws, realKey, encType); // check encryption parameters before http response
            if ((await ws.HandleRequestAsync(false)).IsConnected == false)
                throw new Exception("websocket handshake failed.");
            return ws;
        }

        class ImuxSession
        {
            public ImuxSession(string sid, int count)
            {
                SessionId = sid;
                Connections = new IMsgStream[count];
            }

            public string SessionId;
            public IMsgStream[] Connections;
            public int Count => Connections.Length;
            public int ConnectedCount;
            public InverseMuxStream MuxStream;

            public int WssoCount, WsCount, HttpCount;

            public TaskCompletionSource<object> WhenEnd = new TaskCompletionSource<object>();

            public bool HandleConnection(IMsgStream msgStream, int id)
            {
                lock (Connections) {
                    if (this.Connections[id] != null)
                        throw new Exception($"imux sid {SessionId} id {id} already exists.");
                    Connections[id] = msgStream;
                    ConnectedCount++;

                    if (ConnectedCount == Count) {
                        if (WsCount == Count)
                            MuxStream = new InverseMuxStream(this.Connections);
                        else
                            MuxStream = new InverseMuxStream(
                                recvStreams: Connections.Take(WssoCount + WsCount),
                                sendStreams: Connections.Skip(WssoCount)
                                );
                        return true;
                    }
                    return false;
                }
            }
        }
    }
}
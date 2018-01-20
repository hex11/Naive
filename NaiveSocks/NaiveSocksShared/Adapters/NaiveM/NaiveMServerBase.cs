using System;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Collections.Generic;
using System.Linq;

namespace NaiveSocks
{
    public partial class NaiveProtocol
    {
        public class NaiveMServerBase : InAdapter
        {
            internal List<NaiveMChannels> nmsList = new List<NaiveMChannels>();
            Dictionary<string, ImuxSession> imuxSessions = new Dictionary<string, ImuxSession>();

            protected virtual INetwork GetNetwork(string name)
            {
                return null;
            }

            public int imux_max { get; set; } = 16;

            public int timeout { get; set; } = 30;

            public async Task HandleRequestAsync(HttpConnection p, byte[] realKey)
            {
                try {
                    var token = p.ParseUrlQstr()["token"];
                    if (token == null)
                        return;
                    var bytes = Convert.FromBase64String(token);
                    if (realKey != null) {
                        bytes = EncryptOrDecryptBytes(false, realKey, bytes);
                    }
                    var req = Request.Parse(bytes);
                    const string XumPrefix = "chs2:";
                    bool isImux = req.additionalString.StartsWith(XumPrefix);
                    ImuxSession imux = null;
                    string encryptType = "";
                    if (req.extraStrings.Length > 0) {
                        encryptType = req.extraStrings[0];
                    }
                    if (isImux || req.additionalString == "channels") {
                        IMsgStream msgStream;
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
                            if (connCount > imux_max) {
                                Logging.warning($"{this}: {p.remoteEP}: IMUX count requesting ({connCount}) > imux_max ({imux_max})");
                                return;
                            }
                            IMsgStream wsOrHttp;
                            if (connId < connCount - httpCount) {
                                wsOrHttp = await HandleWebsocket(p, realKey, encryptType);
                            } else {
                                p.setStatusCode("200 OK");
                                p.setHeader(HttpHeaders.KEY_Transfer_Encoding, HttpHeaders.VALUE_Transfer_Encoding_chunked);
                                await p.EndResponseAsync();
                                var baseStream = MyStream.FromStream(p.SwitchProtocol());
                                var msf = new MsgStreamFilter(new HttpChunkedEncodingMsgStream(baseStream));
                                msf.AddWriteFilter(Filterable.GetAesStreamFilter(true, realKey));
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
                                            Logging.warning($"IMUX (id={imux.SessionId}, count={imux.ConnectedCount}/{imux.Count}) timed out");
                                            imux.WhenEnd.SetResult(null);
                                        }
                                    });
                                }
                                if (imux.HandleConnection(wsOrHttp, connId)) {
                                    msgStream = imux.MuxStream;
                                    goto NO_AWAIT;
                                }
                            }
                            await imux.WhenEnd.Task;
                            return;
                            NO_AWAIT:;
                        } else {
                            msgStream = await HandleWebsocket(p, realKey, encryptType);
                        }
                        var nms = new NaiveMChannels(new NaiveMultiplexing(msgStream)) {
                            InAdapter = this
                        };
                        nms.GetNetwork = GetNetwork;
                        lock (nmsList)
                            nmsList.Add(nms);
                        try {
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
                    }
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "NaiveMHandler Url: " + p.Url);
                } finally {
                    if (p.ConnectionState == HttpConnection.States.Processing) {
                        p.Handled = false;
                    }
                }
            }

            private async Task<WebSocketServer> HandleWebsocket(HttpConnection p, byte[] realKey, string encType)
            {
                var ws = new WebSocketServer(p);
                if ((await ws.HandleRequestAsync(false)).IsConnected == false)
                    throw new Exception("websocket handshake failed.");
                ws.AddToManaged(timeout / 2, timeout);
                NaiveProtocol.ApplyEncryption(ws, realKey, encType);
                //ws.ApplyAesStreamFilter(realKey);
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
                        return checkCount();
                    }
                }

                bool checkCount()
                {
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

            //public class ServerConnection // TODO
            //{
            //    private readonly Server _inAdapter;
            //    private readonly HttpConnection _p;
            //    private Request _request;
            //    private WebSocket ws;
            //    private readonly EPPair _eppair;
            //    private int _req = 0;

            //    public ServerConnection(Server inAdapter, HttpConnection p, Request request)
            //    {
            //        _eppair = EPPair.FromSocket(p.tcpClient.Client);
            //        _inAdapter = inAdapter;
            //        _p = p;
            //        _request = request;
            //    }
            //}


            public override void Start()
            {
            }

            public override void Stop()
            {
            }
        }
    }
}
using System;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Collections.Generic;

namespace NaiveSocks
{
    public partial class NaiveProtocol
    {
        public class NaiveMServerBase : InAdapter
        {
            internal List<NaiveMSocks> nmsList = new List<NaiveMSocks>();
            Dictionary<string, ImuxSession> atoDict = new Dictionary<string, ImuxSession>();

            protected virtual INetwork GetNetwork(string name)
            {
                return null;
            }

            public int imux_max { get; set; } = 16;

            public async Task HandleRequestAsync(HttpConnection p, byte[] realKey)
            {
                try {
                    var token = p.ParseUrlQstr()["token"];
                    if (token == null)
                        return;
                    var bytes = Convert.FromBase64String(token);
                    if (realKey != null)
                        bytes = EncryptOrDecryptBytes(false, realKey, bytes);
                    var req = Request.Parse(bytes);
                    const string XumPrefix = "chs2:";
                    bool isXum = req.additionalString.StartsWith(XumPrefix);
                    ImuxSession imux = null;
                    if (isXum || req.additionalString == "channels") {
                        var ws = new WebSocketServer(p);
                        if ((await ws.HandleRequestAsync(false)).IsConnected == false)
                            return;
                        ws.AddToManaged();
                        ws.ApplyAesStreamFilter(realKey);
                        IMsgStream msgStream;
                        if (isXum) {
                            var arr = NaiveUtils.DeserializeArray(req.additionalString.Substring(XumPrefix.Length));
                            var sessionId = arr[0];
                            var connCount = Int32.Parse(arr[1]);
                            var connId = Int32.Parse(arr[2]);
                            if (connCount > imux_max) {
                                Logging.warning($"{this}: {p.remoteEP}: IMUX count requesting ({connCount}) > imux_max ({imux_max})");
                                return;
                            }
                            lock (atoDict) {
                                if (atoDict.TryGetValue(sessionId, out imux) == false) {
                                    imux = new ImuxSession(sessionId, connCount);
                                    atoDict.Add(sessionId, imux);
                                    NaiveUtils.RunAsyncTask(async () => {
                                        await Task.Delay(10 * 1000);
                                        if (imux.ConnectedCount != imux.Count) {
                                            Logging.warning($"IMUX (id={imux.SessionId}, count={imux.ConnectedCount}/{imux.Count}) timed out");
                                            imux.WhenComplete.SetResult(null);
                                        }
                                    });
                                }
                                if (imux.SetWebsocket(ws, connId)) {
                                    msgStream = imux.MuxStream;
                                    goto IMUX_OK;
                                }
                            }
                            await imux.WhenComplete.Task;
                            return;
                            IMUX_OK:;
                        } else {
                            msgStream = ws;
                        }
                        var nms = new NaiveMSocks(new NaiveMultiplexing(msgStream)) {
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
                                lock (atoDict)
                                    atoDict.Remove(imux.SessionId);
                                imux.WhenComplete.SetResult(null);
                            }
                        }
                    }
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error);
                } finally {
                    if (p.ConnectionState == HttpConnection.States.Processing) {
                        p.Handled = false;
                    }
                }
            }

            class ImuxSession
            {
                public ImuxSession(string sid, int count)
                {
                    SessionId = sid;
                    WebSockets = new WebSocketServer[count];
                }

                public string SessionId;
                public WebSocketServer[] WebSockets;
                public int Count => WebSockets.Length;
                public int ConnectedCount;
                public InverseMuxStream MuxStream;

                public TaskCompletionSource<object> WhenComplete = new TaskCompletionSource<object>();

                public bool SetWebsocket(WebSocketServer wss, int id)
                {
                    lock (WebSockets) {
                        if (this.WebSockets[id] != null)
                            throw new Exception();
                        WebSockets[id] = wss;
                        ConnectedCount++;
                        return checkCount();
                    }
                }

                bool checkCount()
                {
                    if (ConnectedCount == Count) {
                        MuxStream = new InverseMuxStream(this.WebSockets);
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
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
            Dictionary<string, XumSession> atoDict = new Dictionary<string, XumSession>();

            protected virtual INetwork GetNetwork(string name)
            {
                return null;
            }

            public int xum_max { get; set; } = 16;

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
                    XumSession chs2 = null;
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
                            if (connCount > xum_max) {
                                Logging.warning($"{this}: {p.remoteEP}: xum count requesting ({connCount}) > xum_max ({xum_max})");
                                return;
                            }
                            lock (atoDict) {
                                if (atoDict.TryGetValue(sessionId, out chs2) == false) {
                                    chs2 = new XumSession(sessionId, connCount);
                                    atoDict.Add(sessionId, chs2);
                                    NaiveUtils.RunAsyncTask(async () => {
                                        await Task.Delay(10 * 1000);
                                        if (chs2.ConnectedCount != chs2.Count) {
                                            Logging.warning($"Xum (id={chs2.SessionId}, count={chs2.ConnectedCount}/{chs2.Count}) timed out");
                                            chs2.WhenComplete.SetResult(null);
                                        }
                                    });
                                }
                                msgStream = chs2.SetWebsocket(ws, connId);
                            }
                            if (msgStream == null) {
                                await chs2.WhenComplete.Task;
                                return;
                            }
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
                            if (chs2 != null) {
                                lock (atoDict)
                                    atoDict.Remove(chs2.SessionId);
                                chs2.WhenComplete.SetResult(null);
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

            class XumSession
            {
                public XumSession(string sid, int count)
                {
                    SessionId = sid;
                    WebSockets = new WebSocketServer[count];
                }

                public string SessionId;
                public WebSocketServer[] WebSockets;
                public int Count => WebSockets.Length;
                public int ConnectedCount;

                public TaskCompletionSource<object> WhenComplete = new TaskCompletionSource<object>();

                public IMsgStream SetWebsocket(WebSocketServer wss, int id)
                {
                    lock (WebSockets) {
                        if (this.WebSockets[id] != null)
                            throw new Exception();
                        WebSockets[id] = wss;
                        ConnectedCount++;
                        if (ConnectedCount == Count) {
                            var ato = new AllToOne(this.WebSockets);
                            return ato;
                        }
                    }
                    return null;
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
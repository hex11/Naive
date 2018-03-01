using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    public class WebSocketMsg
    {
        public WebSocket server;
        public int opcode;
        public object data;
    }

    public class WebSocketServer : WebSocket
    {
        private readonly HttpConnection p;
        private Stream stream => BaseStream;
        private EPPair epPair;


        public WebSocketServer(HttpConnection p) : base(p.baseStream, false)
        {
            this.p = p;
            this.epPair = new EPPair(p.localEP, p.remoteEP);
        }

        public override string ToString()
        {
            return (epPair.LocalEP == null) ? $"{{WsSvr on {BaseStream}}}"
                                            : $"{{WsSvr on {epPair}}}";
        }

        public void HandleRequest() => HandleRequest(true);

        public void HandleRequest(bool enterRecvLoop)
        {
            if (p.Method == "GET"
                && p.GetReqHeader("Upgrade") == "websocket"
                && p.GetReqHeaderSplits("Connection").Contains("Upgrade")) {
                p.Handled = true;
                var wskey = p.GetReqHeader("Sec-WebSocket-Key");
                if (wskey == null) {
                    p.ResponseStatusCode = "400 Bad Request";
                    p.keepAlive = false;
                    p.EndResponse();
                    return;
                }
                if (p.GetReqHeader("Sec-WebSocket-Version") != "13") {
                    p.ResponseStatusCode = "400 Bad Request";
                    p.setHeader("Sec-WebSocket-Version", "13");
                    p.EndResponse();
                    return;
                }

                p.ResponseStatusCode = "101 Switching Protocols";
                p.setHeader("Upgrade", "websocket");
                p.setHeader("Connection", "Upgrade");
                p.setHeader("Sec-WebSocket-Accept", GetWebsocketAcceptKey(wskey));
                p.keepAlive = false;
                p.EndResponse();

                BaseStream = p.SwitchProtocol();
                ConnectionState = States.Open;
                if (enterRecvLoop)
                    recvLoop();
            }
        }

        public Task<WsHandleRequestResult> HandleRequestAsync() => HandleRequestAsync(true);

        public async Task<WsHandleRequestResult> HandleRequestAsync(bool enterRecvLoop)
        {
            List<string> connectionSplits = p.GetReqHeaderSplits("Connection");
            if (p.Method == "GET"
                && p.GetReqHeader("Upgrade") == "websocket"
                && (connectionSplits.Contains("Upgrade") || connectionSplits.Contains("upgrade"))) {
                p.Handled = true;
                if (p.GetReqHeader("Sec-WebSocket-Version") != "13") {
                    p.ResponseStatusCode = "400 Bad Request";
                    p.setHeader("Sec-WebSocket-Version", "13");
                    return new WsHandleRequestResult(WsHandleRequestResult.Results.BadVersion);
                }
                var wskey = p.GetReqHeader("Sec-WebSocket-Key");
                if (wskey == null) {
                    p.ResponseStatusCode = "400 Bad Request";
                    p.keepAlive = false;
                    return new WsHandleRequestResult(WsHandleRequestResult.Results.BadKey);
                }

                p.ResponseStatusCode = "101 Switching Protocols";
                p.setHeader("Upgrade", "websocket");
                p.setHeader("Connection", "Upgrade");
                p.setHeader("Sec-WebSocket-Accept", GetWebsocketAcceptKey(wskey));
                p.keepAlive = false;
                await p.EndResponseAsync().CAF();

                BaseStream = p.SwitchProtocol();
                ConnectionState = States.Open;
                if (enterRecvLoop)
                    await recvLoopAsync().CAF();
                return new WsHandleRequestResult(WsHandleRequestResult.Results.Connected);
            }
            return new WsHandleRequestResult(WsHandleRequestResult.Results.NonWebsocket);
        }
    }

    public class WsHandleRequestResult
    {
        public enum Results
        {
            Connected,
            NonWebsocket,
            BadKey,
            BadVersion
        }

        public Results Result { get; }

        public bool IsWebsocketRequest => Result != Results.NonWebsocket;
        public bool IsConnected => Result == Results.Connected;

        public WsHandleRequestResult(Results result)
        {
            this.Result = result;
        }
    }

    public class WebSocketListener : NaiveHttpServerAsync
    {
        public string Path = "/";
        public IPEndPoint Local;

        public Func<WebSocketServer, Task> Accepted;

        public Task Start()
        {
            AddListener(Local);
            return Run();
        }

        public override async Task HandleRequestAsync(HttpConnection p)
        {
            if (p.Url_path == Path) {
                using (var ws = new WebSocketServer(p)) {
                    await ws.HandleRequestAsync(false).CAF();
                    if (p.Handled)
                        await Accepted(ws).CAF();
                }
            }
        }
    }
}

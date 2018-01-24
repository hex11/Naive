using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    public class WebSocketClient : WebSocket
    {
        public string Path { get; }
        public string Host { get; set; }

        public EPPair epPair;

        public WebSocketClient(Stream baseStream, string path) : base(baseStream, true)
        {
            Path = path;
        }

        public override string ToString()
        {
            return (epPair.LocalEP == null) ? $"{{WebSocketClient on {BaseStream}}}"
                                            : $"{{WebSocketClient on {epPair}}}";
        }

        public static Task<WebSocketClient> ConnectToAsync(string host, int port, string path) => ConnectToAsync(new AddrPort(host, port), path);

        public static async Task<WebSocketClient> ConnectToAsync(AddrPort dest, string path)
        {
            Socket socket = await NaiveUtils.ConnectTCPAsync(dest, 15 * 1000);
            try {
                var socketStream = NaiveSocks.MyStream.FromSocket(socket);
                var ws = new WebSocketClient(NaiveSocks.MyStream.ToStream(socketStream), path);
                ws.Host = dest.Host;
                return ws;
            } catch (Exception) {
                socket.Dispose();
                throw;
            }
        }

        public static WebSocketClient ConnectTo(string host, int port, string path)
        {
            return ConnectToAsync(host, port, path).RunSync();
        }

        public void Start() => Start(true);
        public void Start(bool enterRecvLoop)
        {
            var sw = new StreamWriter(BaseStream, Encoding.ASCII);
            var sr = new StreamReader(BaseStream, Encoding.ASCII);
            var wskey = WebSocket.GenerateSecWebSocketKey();
            var headers = new Dictionary<string, string> {
                ["Upgrade"] = "websocket",
                ["Connection"] = "Upgrade",
                ["Sec-WebSocket-Version"] = "13",
                ["Sec-WebSocket-Key"] = wskey
            };
            if (Host != null)
                headers.Add("Host", Host);
            HttpClient.WriteHttpRequestHeader(sw, "GET", Path, headers);
            sw.Flush();
            BaseStream.Flush();
            var response = HttpClient.ReadHttpResponseHeader(sr);
            var statusCode = response.StatusCode.Split(' ')[0];
            if (statusCode == "101"
                && response.TestHeader("Connection", "Upgrade")
                && response.TestHeader("Upgrade", "websocket")
                && response.TestHeader("Sec-WebSocket-Accept", WebSocket.GetWebsocketAcceptKey(wskey))
            ) {
                ConnectionState = States.Open;
                if (enterRecvLoop)
                    recvLoop();
            } else {
                throw new Exception($"websocket handshake failed ({response.StatusCode})");
            }
        }

        public Task HandshakeAsync(bool enterRecvLoop)
            => HandshakeAsync(enterRecvLoop, null);
        public async Task HandshakeAsync(bool enterRecvLoop, Dictionary<string, string> addHeaders)
        {
            var sw = new StreamWriter(BaseStream, NaiveUtils.UTF8Encoding, 1440, true);
            var wskey = Guid.NewGuid().ToString("D");
            var headers = new Dictionary<string, string> {
                ["Upgrade"] = "websocket",
                ["Connection"] = "Upgrade",
                ["Sec-WebSocket-Version"] = "13",
                ["Sec-WebSocket-Key"] = wskey
            };
            if (Host != null)
                headers.Add("Host", Host);
            if (addHeaders != null) {
                foreach (var item in addHeaders) {
                    headers[item.Key] = item.Value;
                }
            }
            await HttpClient.WriteHttpRequestHeaderAsync(sw, "GET", Path, headers);
            await sw.FlushAsync();
            var responseString = await NaiveUtils.ReadStringUntil(BaseStream, NaiveUtils.DoubleCRLFBytes);
            HttpResponse response;
            try {
                response = await HttpClient.ReadHttpResponseHeaderAsync(new StringReader(responseString));
            } catch (Exception e) {
                throw new Exception("error parsing response:\n" + responseString, e);
            }
            var statusCode = response.StatusCode.Split(' ')[0];
            if (statusCode == "101"
                && response.TestHeader("Connection", "Upgrade")
                && response.TestHeader("Upgrade", "websocket")
                && response.TestHeader("Sec-WebSocket-Accept", WebSocketServer.GetWebsocketAcceptKey(wskey))
            ) {
                ConnectionState = States.Open;
                if (enterRecvLoop)
                    await recvLoopAsync();
            } else {
                throw new Exception($"websocket handshake failed ({response.StatusCode})");
            }
        }
    }
}

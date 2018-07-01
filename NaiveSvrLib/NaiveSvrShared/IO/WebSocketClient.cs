using NaiveSocks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
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

        public WebSocketClient(Stream baseStream, string path) : base(baseStream.ToMyStream(), true)
        {
            Path = path;
        }

        public WebSocketClient(MyStream baseStream, string path) : base(baseStream, true)
        {
            Path = path;
        }

        public override string ToString()
        {
            return (epPair.LocalEP == null) ? $"{{WsCli on {BaseStream}}}"
                                            : $"{{WsCli on {epPair}}}";
        }

        public static Task<WebSocketClient> ConnectToAsync(string host, int port, string path) => ConnectToAsync(new AddrPort(host, port), path);

        public static Task<WebSocketClient> ConnectToAsync(AddrPort dest, string path)
        {
            return ConnectToAsync(dest, path, 15 * 1000);
        }

        public static Task<WebSocketClient> ConnectToAsync(AddrPort dest, string path, int timeout)
            => ConnectToAsync(dest, path, timeout, CancellationToken.None);

        public static async Task<WebSocketClient> ConnectToAsync(AddrPort dest, string path,
                                                                int timeout, CancellationToken ct)
        {
            Socket socket = await NaiveUtils.ConnectTcpAsync(dest, timeout, async x => x, ct);
            try {
                var socketStream = MyStream.FromSocket(socket);
                var ws = new WebSocketClient(MyStream.ToStream(socketStream), path);
                ws.Host = dest.Host;
                return ws;
            } catch (Exception) {
                socket.Dispose();
                throw;
            }
        }

        public static Task<WebSocketClient> ConnectToTlsAsync(AddrPort dest, string path, int timeout)
            => ConnectToTlsAsync(dest, path, timeout, CancellationToken.None);

        public static async Task<WebSocketClient> ConnectToTlsAsync(AddrPort dest, string path,
                                                                    int timeout, CancellationToken ct)
        {
            var stream = await NaiveUtils.ConnectTlsAsync(dest, timeout,
                System.Security.Authentication.SslProtocols.Tls11
                | System.Security.Authentication.SslProtocols.Tls12,
                ct);
            try {
                var ws = new WebSocketClient(stream, path);
                ws.Host = dest.Host;
                return ws;
            } catch (Exception) {
                stream.Dispose();
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
            var stream = this.BaseStream.ToStream();
            var sw = new StreamWriter(stream, Encoding.ASCII);
            var sr = new StreamReader(stream, Encoding.ASCII);
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
            stream.Flush();
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
            var strw = new StringWriter(new StringBuilder(1024));
            HttpClient.WriteHttpRequestHeader(strw, "GET", Path, headers);
            await BaseStream.WriteAsync(NaiveUtils.GetUTF8Bytes(strw.ToString()));
            var responseString = await NaiveUtils.ReadStringUntil(BaseStream.ToStream(), NaiveUtils.DoubleCRLFBytes);
            HttpResponse response;
            try {
                response = HttpClient.ReadHttpResponseHeader(new StringReader(responseString));
            } catch (Exception e) {
                throw new Exception("error parsing response:\n" + responseString, e);
            }
            var statusCode = response.StatusCode.Split(' ')[0];
            if (statusCode == "101"
                && response.TestHeader("Connection", "Upgrade")
                && response.TestHeader("Upgrade", "websocket")
                && response.TestHeader("Sec-WebSocket-Accept", GetWebsocketAcceptKey(wskey))
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

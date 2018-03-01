﻿using NaiveSocks;
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

        public static Task<WebSocketClient> ConnectToAsync(AddrPort dest, string path)
        {
            return ConnectToAsync(dest, path, 15 * 1000);
        }

        public static async Task<WebSocketClient> ConnectToAsync(AddrPort dest, string path, int timeout)
        {
            Socket socket = await NaiveUtils.ConnectTcpAsync(dest, timeout);
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

        public static async Task<WebSocketClient> ConnectToTlsAsync(AddrPort dest, string path, int timeout)
        {
            Socket socket = await NaiveUtils.ConnectTcpAsync(dest, timeout);
            try {
                var tls = new SslStream(new NetworkStream(socket));
                await tls.AuthenticateAsClientAsync(dest.Host, null, System.Security.Authentication.SslProtocols.Tls12, false);
                var ws = new WebSocketClient(tls, path);
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

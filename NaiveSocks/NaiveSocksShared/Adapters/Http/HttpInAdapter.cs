﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Linq;

namespace NaiveSocks
{
    public class HttpInAdapter : InAdapterWithListenField, IHttpRequestAsyncHandler
    {
        public AdapterRef webout { get; set; }

        public bool verbose { get; set; }
#if DEBUG
        = true;
#endif

        public override string ToString()
        {
            return $"{{HttpIn listen={listen}}}";
        }

        HttpServer httpServer;

        protected override void Init()
        {
            base.Init();
            httpServer = new HttpServer(this);
            httpServer.AddListener(listen);
        }

        public override void Start()
        {
            base.Start();
            httpServer.Run();
        }

        public override void Stop()
        {
            base.Stop();
            httpServer.Stop();
        }

        public Task HandleRequestAsync(HttpConnection p)
        {
            return httpServer.HandleRequestAsync(p);
        }

        private class HttpConn : HttpConnection
        {
            public HttpConn(Stream stream, EPPair ePPair, NaiveHttpServer server) : base(stream, ePPair, server)
            {
            }
        }

        private class HttpServer : NaiveHttpServerAsync
        {
            public HttpServer(HttpInAdapter adapter)
            {
                this.adapter = adapter;
            }

            HttpInAdapter adapter { get; }
            bool verbose => adapter.verbose;

            public override async Task HandleRequestAsync(HttpConnection p)
            {
                if (verbose)
                    Logging.info($"{adapter.QuotedName}: [{p.Id}({p.requestCount})] {p.Method} {p.Url}");
                if (p.Method == "CONNECT") {
                    p.EnableKeepAlive = false;
                    var dest = AddrPort.Parse(p.Url);
                    var stream = p.SwitchProtocol();
                    var mystream = getStream(p);
                    string str = "(tunnel) remote=" + p.remoteEP;
                    var inc = InConnection.Create(adapter, dest, async (r) => {
                        if (r.Ok) {
                            await mystream.WriteAsync(NaiveUtils.UTF8Encoding.GetBytes(
                                "HTTP/1.1 200 Connection established\r\nProxy-Agent: NaiveSocks\r\n\r\n"));
                            return mystream;
                        } else {
                            mystream.Close().Forget();
                            return null;
                        }
                    }, () => str);
                    try {
                        await adapter.HandleIncommingConnection(inc);
                    } finally {
                        MyStream.CloseWithTimeout(mystream);
                    }
                } else if (p.Url.StartsWith("http://") || p.Url.StartsWith("https://")) {
                    await handleHttp(p);
                } else {
                    if (adapter.webout != null) {
                        if (!(adapter.webout.Adapter is IHttpRequestAsyncHandler ihrah)) {
                            Logging.warning($"{adapter.QuotedName}: value of 'webout' ({adapter.webout.Adapter}) is not a http handler.");
                            return;
                        }
                        await ihrah.HandleRequestAsync(p);
                    } else {
                        Logging.info($"{adapter.QuotedName}: unhandled web Request: {p.Method} {p.Url}");
                    }
                }
            }

            static IMyStream getStream(HttpConnection p) => p.myStream;

            private async Task handleHttp(HttpConnection p)
            {
                p.EnableKeepAlive = false;
                AddrPort parseUrl(string url, out string path, out bool ishttps)
                {
                    int hostStart = url.IndexOf("://") + 3;
                    var scheme = url.Substring(0, hostStart - 3);
                    switch (scheme.ToLower()) {
                    case "http":
                        ishttps = false;
                        break;
                    case "https":
                        ishttps = true;
                        break;
                    default:
                        throw new Exception($"unknown scheme '{scheme}'");
                    }
                    int realUrlStart = url.IndexOf('/', hostStart);
                    if (realUrlStart == -1) {
                        // such url: http://example.com
                        realUrlStart = 7;
                        url += "/";
                    }
                    var host = url.Substring(hostStart, realUrlStart - hostStart);
                    path = url.Substring(realUrlStart);
                    if (host.Contains(":") && !host.Contains("[")) {
                        return AddrPort.Parse(host);
                    } else {
                        return new AddrPort(host, ishttps ? 443 : 80);
                    }
                }
                bool isUpgrade(Dictionary<string, string> headers) => headers.ContainsKey("Upgrade")
                            || (headers.ContainsKey("Connection")
                                && headers["Connection"]?.Split(',').Select(x => x.Trim()).Contains("Upgrade") == true);
                AddrPort dest = parseUrl(p.Url, out var realurl, out var isHttps);
                connnectDest:
                var tcsGetResult = new TaskCompletionSource<ConnectResult>();
                var tcsProcessing = new TaskCompletionSource<VoidType>();
                try {
                    var inc = InConnection.Create(adapter, dest, dataStream: null, getInfoStr: "(http) remote=" + p.remoteEP);
                    inc.Url = p.Url;
                    adapter.Controller.Connect(inc, adapter.@out.Adapter,
                        (result) => {
                            tcsGetResult.SetResult(result);
                            return tcsProcessing.Task;
                        }).Forget();
                    var r = await tcsGetResult.Task;
                    if (!r.Ok) {
                        throw new Exception($"ConnectResult: {r.Result} ({r.FailedReason})");
                    }
                    var destStream = r.Stream;
                    TlsStream tlsStream = null;
                    if (isHttps) {
                        destStream = tlsStream = new TlsStream(destStream);
                        await tlsStream.AuthAsClient(dest.Host);
                    }
                    var whenCanReadResponse = r.WhenCanRead;
                    var destCommonStream = MyStream.ToStream(destStream);
                    try {
                        var sb = new StringBuilder(p.RawRequest.Length);
                        var newHeaders = new Dictionary<string, string>(p.RequestHeaders.Count);
                        Task WriteRequest(IMyStream stream)
                        {
                            sb.Clear();
                            var tw = new StringWriter(sb);
                            HttpClient.WriteHttpRequestHeader(tw, new HttpRequest {
                                Method = p.Method,
                                Path = realurl,
                                Headers = newHeaders
                            });
                            return stream.WriteAsync(NaiveUtils.GetUTF8Bytes(tw.ToString()));
                        }

                        bool keepAlive = true;
                        void ProcessHeaders()
                        {
                            foreach (var kv in p.RequestHeaders) {
                                var value = kv.Value;
                                if (string.Equals(kv.Key, "Proxy-Connection", StringComparison.OrdinalIgnoreCase)) {
                                    newHeaders["Connection"] = value;
                                    if (value == "close" || value == "Close" /* <- IE */ )
                                        //       ^ Browsers excpet IE
                                        keepAlive = false;
                                    continue;
                                }
                                newHeaders[kv.Key] = value;
                            }
                            if (isUpgrade(newHeaders)) {
                                keepAlive = false;
                            }
                        }

                        ProcessHeaders();
                        await WriteRequest(destStream);
                        p.SwitchProtocol();
                        if (keepAlive == false) {
                            p.SwitchProtocol();
                            var clientStream = getStream(p);
                            await MyStream.Relay(destStream, clientStream, whenCanReadResponse);
                        } else {
                            var clientStream = getStream(p);
                            var copyingResponse = NaiveUtils.RunAsyncTask(async () => {
                                await whenCanReadResponse;
                                await MyStream.StreamCopy(destStream, clientStream, 32 * 1024, true);
                            });
                            while (true) { // keep-alive loop
                                if (p.inputDataStream != null) {
                                    await Utils.StreamCopyAsync(p.inputDataStream, destCommonStream);
                                    if (verbose)
                                        Logging.info($"{adapter.QuotedName} {p}: copying input data {p.inputDataStream.Length} bytes.");
                                }
                                if (!keepAlive) {
                                    Logging.warning($"{adapter.QuotedName} {p}: keep-alvie changed to false. ({dest})");
                                    var copyingResponse2 = NaiveUtils.RunAsyncTask(async () => {
                                        await copyingResponse;
                                        await clientStream.Shutdown(SocketShutdown.Send);
                                    });
                                    var copingRequese = NaiveUtils.RunAsyncTask(async () => {
                                        await MyStream.StreamCopy(clientStream, destStream);
                                        await destStream.Shutdown(SocketShutdown.Send);
                                    });
                                    await Task.WhenAll(copyingResponse2, copingRequese);
                                    if (verbose)
                                        Logging.info($"{adapter.QuotedName} {p} completed: no keep-alive.");
                                    break;
                                }
                                var recvNext = p._ReceiveNextRequest();
                                Task completed = await Task.WhenAny(recvNext, copyingResponse);
                                if (completed == copyingResponse) {
                                    if (verbose)
                                        Logging.info($"{adapter.QuotedName} {p} completed: copyingResponse.");
                                    await completed;
                                    await clientStream.Close();
                                    return;
                                } else {
                                    try {
                                        if (recvNext.Result == false) {
                                            return;
                                        }
                                    } catch (IOException e) when (e.InnerException is SocketException se) {
                                        if (verbose)
                                            Logging.warning($"{adapter.QuotedName} {p}: receiving request error {se.SocketErrorCode}");
                                        return;
                                    }
                                }
                                if (verbose)
                                    Logging.info($"{adapter.QuotedName} {p}: {p.Method} {p.Url}");
                                var newDest = parseUrl(p.Url, out realurl, out var newIsHttps);
                                if (newDest != dest || newIsHttps != isHttps) {
                                    string proto(bool x) => x ? "https" : "http";
                                    if (verbose)
                                        Logging.warning($"{adapter.QuotedName} {p}: dest changed." +
                                            $" ({proto(isHttps)}){dest} -> ({proto(newIsHttps)}){newDest}");
                                    await destStream.Shutdown(SocketShutdown.Send);
                                    await copyingResponse;
                                    dest = newDest;
                                    isHttps = newIsHttps;
                                    goto connnectDest; // It works!
                                }
                                ProcessHeaders();
                                await WriteRequest(destStream);
                            }
                        }
                    } finally {
                        MyStream.CloseWithTimeout(destStream);
                    }
                } finally {
                    tcsProcessing.TrySetResult(0);
                }
            }
        }
    }
}

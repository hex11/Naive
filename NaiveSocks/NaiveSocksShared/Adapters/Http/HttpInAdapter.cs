using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Linq;
using Nett;

namespace NaiveSocks
{
    public class HttpInAdapter : InAdapterWithListenField, IHttpRequestAsyncHandler, IConnectionHandler
    {
        public AdapterRef[] webouts { get; set; }

        public Dictionary<string, AdapterRef[]> hosts { get; set; }

        public bool logging { get; set; }
        public bool verbose { get; set; }
#if DEBUG
        = true;
#endif

        public override string ToString()
        {
            return $"{{HttpIn listen={listen}}}";
        }

        HttpServer httpServer;

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            if (toml.TryGetValue<AdapterRef>("webout", out var ada)) {
                var newarr = new AdapterRef[1 + (webouts?.Length ?? 0)];
                newarr[0] = ada;
                if (webouts?.Length > 0) {
                    Array.Copy(webouts, 0, newarr, 1, webouts.Length);
                }
                webouts = newarr;
            }
        }

        protected override void Init()
        {
            base.Init();
            httpServer = new HttpServer(this);
            if (listen != null)
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

        public async Task HandleConnection(InConnection connection)
        {
            await connection.SetConnectResult(ConnectResults.Conneceted);
            var httpConn = WebBaseAdapter.CreateHttpConnectionFromMyStream(connection.DataStream, httpServer);
            await httpConn.Process();
        }

        private class HttpServer : NaiveHttpServerAsync
        {
            public HttpServer(HttpInAdapter adapter)
            {
                this.adapter = adapter;
            }

            HttpInAdapter adapter { get; }
            Logger Logger => adapter.Logger;
            bool verbose => adapter.verbose;
            bool logging => adapter.logging | verbose;

            public override Task HandleRequestAsync(HttpConnection p)
            {
                if (logging)
                    Logger.info($"[{p.Id}({p.requestCount})] {p.Method} {p.Url}");
                if (p.Method == "CONNECT") {
                    if (adapter.@out == null) {
                        adapter.Logger.info($"unhandled tunnel request (no 'out'): {p.Method} {p.Url}");
                        return AsyncHelper.CompletedTask;
                    }
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
                    return adapter.HandleIncommingConnection(inc);
                } else if (p.Url.StartsWith("http://") || p.Url.StartsWith("https://")) {
                    if (adapter.@out == null) {
                        Logger.info($"unhandled proxy request (no 'out'): {p.Method} {p.Url}");
                        return AsyncHelper.CompletedTask;
                    }
                    return handleHttp(p);
                } else {
                    return HandleWeb(p);
                }
            }

            async Task HandleWeb(HttpConnection p)
            {
                var host = p.Host;
                var hosts = adapter.hosts;
                if (host != null && hosts != null) {
                    if (hosts.TryGetValue(host, out var outs)) {
                        if (await HandleByAdapters(p, outs))
                            return;
                    }
                }
                var webouts = adapter.webouts;
                if (webouts == null) {
                    Logger.info($"unhandled web request (no 'webouts'/'hosts'): {p.Method} {p.Url}");
                    return;
                }
                await HandleByAdapters(p, webouts);
            }

            async Task<bool> HandleByAdapters(HttpConnection p, AdapterRef[] adapters)
            {
                foreach (var adaRef in adapters) {
                    if (!(adaRef.Adapter is IHttpRequestAsyncHandler ihrah)) {
                        Logger.warning($"adapter ({adaRef}) is not a http handler.");
                    } else {
                        await ihrah.HandleRequestAsync(p);
                        if (p.Handled)
                            return true;
                    }
                }
                return false;
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
                                        Logger.info($"{p}: copying input data {p.inputDataStream.Length} bytes.");
                                }
                                if (!keepAlive) {
                                    Logger.warning($"{p}: keep-alvie changed to false. ({dest})");
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
                                        Logger.info($"{p} completed: no keep-alive.");
                                    break;
                                }
                                var recvNext = p._ReceiveNextRequest();
                                Task completed = await Task.WhenAny(recvNext, copyingResponse);
                                if (completed == copyingResponse) {
                                    if (verbose)
                                        Logger.info($"{p} completed: copyingResponse.");
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
                                            Logger.warning($"{p}: receiving request error {se.SocketErrorCode}");
                                        return;
                                    }
                                }
                                if (verbose)
                                    Logger.info($"{p}: {p.Method} {p.Url}");
                                var newDest = parseUrl(p.Url, out realurl, out var newIsHttps);
                                if (newDest != dest || newIsHttps != isHttps) {
                                    string proto(bool x) => x ? "https" : "http";
                                    if (verbose)
                                        Logger.warning($"{p}: dest changed." +
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
                        MyStream.CloseWithTimeout(destStream).Forget();
                    }
                } finally {
                    tcsProcessing.TrySetResult(0);
                }
            }
        }
    }
}

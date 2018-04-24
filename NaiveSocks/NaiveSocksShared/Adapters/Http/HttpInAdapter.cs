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
    public class HttpInAdapter : InAdapterWithListenField, IHttpRequestAsyncHandler, IConnectionHandler, ICanReload
    {
        public struct WebRoute
        {
            public StringOrArray host { get; set; }

            public string location { get; set; }

            public AdapterRefOrArray to { get; set; }

            public bool chroot { get; set; }
        }

        public Dictionary<string, AdapterRef[]> hosts { get; set; }

        public WebRoute[] webroutes { get; set; }

        public AdapterRef[] webouts { get; set; }

        public bool logging { get; set; }
        public bool verbose { get; set; }
#if DEBUG
        = true;
#endif

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
            if (webroutes != null) {
                var troutes = toml.Get<TomlTableArray>(nameof(webroutes)).Items;
                for (int i = 0; i < webroutes.Length; i++) {
                    var r = webroutes[i];
                    if (troutes[i].TryGetValue<string>("location_chroot", out var v)) {
                        r.location = v;
                        r.chroot = true;
                    }
                    webroutes[i] = r;
                }
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

        HttpInAdapter newInstance;

        public bool Reloading(object oldInstance)
        {
            if (oldInstance is HttpInAdapter old) {
                old.newInstance = this;
                old.Stop();
            }
            return false;
        }

        public async Task HandleConnection(InConnection connection)
        {
            var stream = await connection.HandleAndGetStream(this);
            var httpConn = WebBaseAdapter.CreateHttpConnectionFromMyStream(stream, httpServer);
            await httpConn.Process();
        }

        static byte[] ConnectedResponse = NaiveUtils.UTF8Encoding.GetBytes(
                            "HTTP/1.1 200 Connection established\r\nProxy-Agent: NaiveSocks\r\n\r\n");

        public Task HandleRequestAsync(HttpConnection p)
        {
            if (newInstance != null) {
                return newInstance.HandleRequestAsync(p);
            }
            if (logging)
                Logger.info($"[{p.Id}({p.requestCount})] {p.Method} {p.Url}");
            if (p.Method == "CONNECT") {
                if (@out == null) {
                    Logger.info($"unhandled tunnel request (no 'out'): {p.Method} {p.Url}");
                    return AsyncHelper.CompletedTask;
                }
                p.EnableKeepAlive = false;
                var dest = AddrPort.Parse(p.Url);
                var stream = p.SwitchProtocol();
                var mystream = getStream(p);
                string str = "(tunnel) " + p.epPair.ToString();
                var inc = InConnection.Create(this, dest, async (r) => {
                    if (r.Ok) {
                        await mystream.WriteAsync(ConnectedResponse);
                        return mystream;
                    } else {
                        mystream.Close().Forget();
                        return null;
                    }
                }, () => str);
                return HandleIncommingConnection(inc);
            } else if (p.Url.StartsWith("http://") || p.Url.StartsWith("https://")) {
                if (@out == null) {
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
            if (host != null && hosts != null) {
                if (hosts.TryGetValue(host, out var outs)) {
                    if (await HandleByAdapters(p, outs))
                        return;
                }
            }
            if (webroutes != null) {
                foreach (var r in webroutes) {
                    if ((r.host.IsNull || (p.Host != null && r.host.IsOrContains(p.Host)))
                            && (r.location == null || IsInLocation(p.Url_path, r.location))) {
                        var oldPath = p.Url_path;
                        if (r.chroot && r.location != null)
                            p.Url_path = p.Url_path.Substring(r.location.Length);
                        if (p.Url_path.Length == 0)
                            p.Url_path = "/";
                        try {
                            foreach (var item in r.to) {
                                if (await HandleByAdapter(p, item))
                                    return;
                            }
                        } finally {
                            p.Url_path = oldPath;
                        }
                    }
                }
            }
            if (webouts == null) {
                Logger.info($"unhandled web request (no 'webouts'/'hosts'?): {p.Method} {p.Url}");
                return;
            }
            await HandleByAdapters(p, webouts);
        }

        bool IsInLocation(string path, string location)
        {
            if (path.StartsWith(location))
                if (path.Length == location.Length || path[location.Length] == '/')
                    return true;
            return false;
        }

        async Task<bool> HandleByAdapters(HttpConnection p, AdapterRef[] adapters)
        {
            foreach (var adaRef in adapters) {
                if (await HandleByAdapter(p, adaRef))
                    return true;
            }
            return false;
        }

        async Task<bool> HandleByAdapter(HttpConnection p, AdapterRef adaRef)
        {
            if (!(adaRef.Adapter is IHttpRequestAsyncHandler ihrah)) {
                Logger.warning($"adapter ({adaRef}) is not a http handler.");
            } else {
                await ihrah.HandleRequestAsync(p);
                if (p.Handled)
                    return true;
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
                string host;
                int realUrlStart = url.IndexOf('/', hostStart);
                if (realUrlStart == -1) {
                    // such url: http://example.com
                    host = url.Substring(hostStart);
                    path = "/";
                } else {
                    host = url.Substring(hostStart, realUrlStart - hostStart);
                    path = url.Substring(realUrlStart);
                }
                if (host.Contains(":") && !host.Contains("[")) {
                    return AddrPort.Parse(host);
                } else {
                    return new AddrPort(host, ishttps ? 443 : 80);
                }
            }

            bool isUpgrade(Dictionary<string, string> headers) => headers.ContainsKey("Upgrade")
                        || (headers.ContainsKey("Connection")
                            && headers["Connection"]?.Split(',').Select(x => x.Trim()).Contains("Upgrade") == true);

            string protoStr(bool x) => x ? "strange-https" : "http";

            AddrPort dest = parseUrl(p.Url, out var realurl, out var isHttps);
            connnectDest:
            var tcsGetResult = new TaskCompletionSource<ConnectResult>();
            var tcsProcessing = new TaskCompletionSource<VoidType>();
            try {
                var inc = InConnection.Create(this, dest, dataStream: null, getInfoStr: $"({protoStr(isHttps)}) " + p.epPair.ToString());
                inc.Url = p.Url;
                Controller.Connect(inc, @out.Adapter,
                    (result) => {
                        tcsGetResult.SetResult(result);
                        return tcsProcessing.Task;
                    }).Forget();
                var r = await tcsGetResult.Task;
                if (!r.Ok) {
                    throw new Exception($"ConnectResult: {r.Result} ({r.FailedReason})");
                }
                var thisCounterRW = inc.BytesCountersRW;
                thisCounterRW.R.Add(p.RawRequestBytesLength);
                var destStream = r.Stream;
                var destCounterRW = r.Adapter?.GetAdapter().BytesCountersRW ?? new BytesCountersRW() {
                    R = null,
                    W = MyStream.GlobalWriteCounter
                };
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
                            if (kv.Key.StartsWith("Proxy", StringComparison.OrdinalIgnoreCase)) {
                                Logger.warning($"Unknown 'Proxy*' header ({kv.Key}: {kv.Value})");
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
                    var clientStream = getStream(p);
                    if (keepAlive == false) {
                        await MyStream.Relay(destStream, clientStream, whenCanReadResponse);
                        return;
                    }

                    var copyingResponse = NaiveUtils.RunAsyncTask(async () => {
                        await whenCanReadResponse;
                        await new MyStream.Copier(destStream, clientStream) {
                            CounterR = destCounterRW.R,
                            CounterW = thisCounterRW.W
                        }.Copy();
                        // NO need...
                        // TODO: check response boundary
                    });

                    while (true) { // keep-alive loop
                        if (p.inputDataStream != null) {
                            await new MyStream.Copier(p.inputDataStream.ToMyStream(), destStream) {
                                CounterW = destCounterRW.W,
                                CounterR = thisCounterRW.R
                            }.Copy();
                            if (verbose)
                                Logger.info($"{p}: copied input data {p.inputDataStream.Length} bytes.");
                        }
                        if (!keepAlive) {
                            Logger.warning($"{p}: keep-alvie changed to false. ({dest})");
                            var copyingResponse2 = NaiveUtils.RunAsyncTask(async () => {
                                await copyingResponse;
                                await clientStream.Shutdown(SocketShutdown.Send);
                            });
                            var copingRequest = NaiveUtils.RunAsyncTask(async () => {
                                await new MyStream.Copier(clientStream, destStream) {
                                    CounterW = destCounterRW.W,
                                    CounterR = thisCounterRW.R
                                }.CopyAndShutdown();
                            });
                            await Task.WhenAll(copyingResponse2, copingRequest);
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
                        }
                        try {
                            if (recvNext.Result == false) {
                                return;
                            }
                        } catch (IOException e) when (e.InnerException is SocketException se) {
                            if (verbose)
                                Logger.warning($"{p}: receiving request error {se.SocketErrorCode}");
                            return;
                        }
                        if (verbose)
                            Logger.info($"{p}: {p.Method} {p.Url}");
                        var newDest = parseUrl(p.Url, out realurl, out var newIsHttps);
                        if (newDest != dest || newIsHttps != isHttps) {
                            if (verbose)
                                Logger.warning($"{p}: dest changed." +
                                    $" ({protoStr(isHttps)}){dest} -> ({protoStr(newIsHttps)}){newDest}");
                            await destStream.Shutdown(SocketShutdown.Send);
                            await copyingResponse;
                            dest = newDest;
                            isHttps = newIsHttps;
                            goto connnectDest; // It works!
                        }
                        thisCounterRW.R.Add(p.RawRequestBytesLength);
                        ProcessHeaders();
                        await WriteRequest(destStream);
                    }
                } finally {
                    MyStream.CloseWithTimeout(destStream).Forget();
                }
            } finally {
                tcsProcessing.TrySetResult(0);
            }
        }

        private class HttpServer : NaiveHttpServerAsync
        {
            public HttpServer(HttpInAdapter adapter)
            {
                this.adapter = adapter;
            }

            HttpInAdapter adapter { get; }

            public override Task HandleRequestAsync(HttpConnection p)
            {
                return adapter.HandleRequestAsync(p);
            }
        }
    }
}

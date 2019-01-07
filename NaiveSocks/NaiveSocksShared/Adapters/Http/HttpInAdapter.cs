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
    public class HttpInAdapter : InAdapterWithListener, IHttpRequestAsyncHandler, IConnectionHandler, ICanReload
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

        protected override void OnInit()
        {
            base.OnInit();
            httpServer = new HttpServer(this);
        }

        HttpInAdapter newInstance;

        public bool Reloading(object oldInstance)
        {
            if (oldInstance is HttpInAdapter old) {
                old.newInstance = this;
                old.OnStop();
            }
            return false;
        }

        public async Task HandleConnection(InConnection connection)
        {
            var stream = await connection.HandleAndGetStream(this);
            var httpConn = WebBaseAdapter.CreateHttpConnectionFromMyStream(stream, httpServer);
            await httpConn.Process();
        }

        public override void OnNewConnection(TcpClient client)
        {
            var stream = GetMyStreamFromSocket(client.Client);
            var httpConn = WebBaseAdapter.CreateHttpConnectionFromMyStream(stream, httpServer);
            httpConn.Process().Forget();
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
                int pathStart = url.IndexOf('/', hostStart);
                if (pathStart == -1) {
                    // such url: http://example.com
                    host = url.Substring(hostStart);
                    path = "/";
                } else {
                    host = url.Substring(hostStart, pathStart - hostStart);
                    path = url.Substring(pathStart);
                }
                if (host.IndexOf('@') != -1)
                    throw new ArgumentException("host should not contain '@'.");
                if (host.Contains(":") && !host.Contains("[")) {
                    return AddrPort.Parse(host);
                } else {
                    return new AddrPort(host, ishttps ? 443 : 80);
                }
            }

            bool isUpgrade(HttpHeaderCollection headers) => headers["Upgrade"] != null
                        || (headers["Connection"] != null
                            && headers["Connection"]?.Split(',').Select(x => x.Trim()).Contains("Upgrade") == true);

            int totalReqs = 0;

            AddrPort dest = parseUrl(p.Url, out var realurl, out var isHttps);
        connnectDest:
            int destReqs = 0;
            string p_HttpVersion = null;
            string state = null;
            Controller.ConnectResponse? ccr = null;
            try {
                var req = ConnectRequest.Create(this, dest,
                    () => {
                        var sb = new StringBuilder();
                        sb.Append("(").Append(p.HttpVersion ?? p_HttpVersion);
                        if (isHttps)
                            sb.Append(" tls");
                        sb.Append(" ").Append(destReqs).Append("/").Append(totalReqs);
                        if (state != null)
                            sb.Append(" ").Append(state);
                        sb.Append(") ").Append(p.epPair.ToString());
                        return sb.ToString();
                    });
                req.Url = p.Url;
                ConnectResult r;
                try {
                    ccr = await Controller.Connect(req, @out.Adapter);
                    r = ccr.Value.Result;
                } catch (Exception e) {
                    Logger.exception(e, Logging.Level.Warning, "Controller.Connect exception");
                    r = new ConnectResult(null, e.Message);
                }
                if (!r.Ok) {
                    p.setStatusCode("502 Bad Gateway");
                    await p.writeAsync("<h1>502 Bad Gateway</h1>" + HttpUtil.HtmlEncode(r.FailedReason));
                    p.keepAlive = false;
                    // TODO: Keepalive
                    await p.EndResponseAsync();
                    return;
                }
                var thisCounterRW = req.BytesCountersRW;
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
                    var newHeaders = new HttpHeaderCollection(p.RequestHeaders.Count);
                    async Task WriteRequest()
                    {
                        sb.Clear();
                        var tw = new StringWriter(sb);
                        HttpClient.WriteHttpRequestHeader(tw,
                            p.Method,
                            realurl,
                            newHeaders
                        );
                        var buf = NaiveUtils.GetUTF8Bytes_AllocFromPool(tw.ToString());
                        destCounterRW.W.Add(buf.Len);
                        await destStream.WriteAsync(buf);
                        BufferPool.GlobalPut(buf.Bytes);
                    }

                    bool keepAlive = p.IsHttp1_1;
                    void ProcessHeaders()
                    {
                        totalReqs++;
                        destReqs++;
                        p_HttpVersion = p.HttpVersion; // p.HttpVersion will be cleaned on next request receving
                        newHeaders.Clear();
                        string connection_value = null;
                        foreach (var kv in p.RequestHeaders) {
                            var value = kv.Value;
                            if (string.Equals(kv.Key, "Proxy-Connection", StringComparison.OrdinalIgnoreCase)) {
                                connection_value = value; // set after the loop to avoid duplicated Connection headers
                                if (string.Equals(value, "close", StringComparison.OrdinalIgnoreCase))
                                    keepAlive = false;
                                continue;
                            }
                            if (kv.Key.StartsWith("Proxy", StringComparison.OrdinalIgnoreCase)) {
                                Logger.warning($"Unknown 'Proxy*' header ({kv.Key}: {kv.Value})");
                                continue;
                            }
                            newHeaders.Add(new HttpHeader(kv.Key, value));
                        }
                        if (connection_value != null) {
                            newHeaders["Connection"] = connection_value;
                        }
                        if (isUpgrade(newHeaders)) {
                            keepAlive = false;
                        }
                    }

                    ProcessHeaders();
                    await WriteRequest();
                    p.SwitchProtocol();
                    var clientStream = getStream(p);
                    if (keepAlive == false) {
                        state = "nonKeepAlive";
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
                            state = "wasKeepAlive";
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
                                    $" {(isHttps ? "(https) " : null)}{dest} -> {(newIsHttps ? "(https) " : null)}{newDest}");
                            await destStream.Shutdown(SocketShutdown.Send);
                            await copyingResponse;
                            dest = newDest;
                            isHttps = newIsHttps;
                            goto connnectDest; // It works!
                        }
                        thisCounterRW.R.Add(p.RawRequestBytesLength);
                        ProcessHeaders();
                        await WriteRequest();
                    }
                } finally {
                    MyStream.CloseWithTimeout(destStream).Forget();
                }
            } catch (Exception e) {
                ccr?.OnConnectionException(e);
            } finally {
                ccr?.OnConnectionEnd();
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

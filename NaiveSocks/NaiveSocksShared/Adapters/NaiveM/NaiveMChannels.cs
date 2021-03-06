﻿using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;

namespace NaiveSocks
{
    public class NaiveMChannels : RrChannels<NaiveProtocol.Request, NaiveProtocol.Reply>
    {
        public IAdapter Adapter;

        public IAdapter ConnectionHandler;
        public Func<InConnection, Task> OnIncomming;

        public Logger Logger;
        public bool LogDest;

        public Func<string, INetwork> NetworkProvider;

        public bool InConnectionFastCallback;

        public string PerChannelEncryption;
        public byte[] PerChannelEncryptionKey;

        Task mainTask;

        List<WeakReference<INetwork>> joinedNetworks = new List<WeakReference<INetwork>>();

        static NaiveMChannels()
        {
            MsgRequestConverter = x => {
                var r = NaiveProtocol.Request.Parse(x.Data.GetBytes());
                x.TryRecycle();
                return r;
            };
            RequestMsgConverter = x => x.ToBytes();
            MsgReplyConverter = x => {
                var r = NaiveProtocol.Reply.Parse(x.Data.GetBytes());
                x.TryRecycle();
                return r;
            };
            ReplyMsgConverter = x => x.ToBytes();
        }

        public NaiveMChannels(NaiveMultiplexing channels) : base(channels)
        {
            this.OnLocalChannelCreated += TryApplyPerChannelEncryption;
            this.OnRemoteChannelCreated += TryApplyPerChannelEncryption;
        }

        private void TryApplyPerChannelEncryption(Channel x)
        {
            if (PerChannelEncryption.IsNullOrEmpty() == false)
                NaiveProtocol.ApplyEncryption(x.DataFilter, PerChannelEncryptionKey, PerChannelEncryption);
        }

        public class ConnectingSettings
        {
            public AddrPort Host { get; set; }
            public string Path { get; set; }
            public byte[] Key { get; set; }
            public string KeyString { set => Key = NaiveProtocol.GetRealKeyFromString(value, 32); }
            public string Encryption { get; set; }
            public string EncryptionPerChannel { get; set; }
            public bool TlsEnabled { get; set; }

            public Dictionary<string, string> Headers { get; set; }
            public string UrlFormat { get; set; } = "{0}?token={1}";

            public int ImuxWsConnections { get; set; } = 1;
            public int ImuxHttpConnections { get; set; } = 0;
            public int ImuxWsSendOnlyConnections { get; set; } = 0;
            public int ImuxConnectionsDelay { get; set; } = 0;
            public int Timeout { get; internal set; }
        }

        public static Task<NaiveMChannels> ConnectTo(AddrPort host, string path, string key)
            => ConnectTo(new ConnectingSettings {
                Host = host,
                Path = path,
                Key = NaiveProtocol.GetRealKeyFromString(key, 32),
            });

        public static Task<NaiveMChannels> ConnectTo(AddrPort host, string path, byte[] key)
            => ConnectTo(new ConnectingSettings {
                Host = host,
                Path = path,
                Key = key
            });

        public static Task<NaiveMChannels> ConnectTo(ConnectingSettings settings)
            => ConnectTo(settings, CancellationToken.None);

        public static async Task<NaiveMChannels> ConnectTo(ConnectingSettings settings, CancellationToken ct)
        {
            const string ImuxPrefix = "chs2:";
            var key = settings.Key;
            if (settings.Headers?.ContainsKey("Host") == false) {
                settings.Headers["Host"] = (settings.Host.Port == 80) ? settings.Host.Host : settings.Host.ToString();
            }
            IMsgStream msgStream;
            // [ websocket send-only (wsso) | websocket (ws) | http ]
            int wssoCount = settings.ImuxWsSendOnlyConnections; // sending only (index starting from 0)
            int wsCount = settings.ImuxWsConnections; // sending and recving (index following by wssoCount)
            int httpCount = settings.ImuxHttpConnections; // recving only
            int count = wssoCount + wsCount + httpCount;
            if (count > 1) {
                string sid = Guid.NewGuid().ToString("N").Substring(0, 8);
                int httpStart = count - httpCount;
                var myCts = new CancellationTokenSource();
                var fail = new TaskCompletionSource<int>();
                bool canceled = false;
                var streams = new IMsgStream[count];
                var tasks = Enumerable.Range(0, count).Select(x => NaiveUtils.RunAsyncTask(async () => {
                    if (settings.ImuxConnectionsDelay > 0)
                        await Task.Delay(settings.ImuxConnectionsDelay * x);
                    if (myCts.Token.IsCancellationRequested)
                        return;
                    string parameter = NaiveUtils.SerializeArray(sid, wsCount.ToString(), x.ToString(),
                                                    wssoCount.ToString(), httpCount.ToString());
                    IMsgStream stream;
                    try {
                        stream = await Connect(ImuxPrefix + parameter, x >= httpStart, settings, myCts.Token);
                    } catch (Exception e) {
                        Logging.debug($"{sid}-{x}: {e.Message}");
                        fail.TrySetException(e);
                        return;
                    }
                    lock (streams) {
                        if (!canceled) {
                            streams[x] = stream;
                            return;
                        }
                    }
                    stream.Close(CloseOpt.Close).Forget();
                })).ToArray();
                try {
                    using (ct.Register((x) => ((TaskCompletionSource<int>)x).TrySetCanceled(), fail, false)) {
                        var task = await Task.WhenAny(Task.WhenAll(tasks), fail.Task);
                        await task;
                    }
                } catch (Exception) {
                    myCts.Cancel();
                    lock (streams) {
                        canceled = true;
                        foreach (var item in streams) {
                            try {
                                item?.Close(CloseOpt.Close).Forget();
                            } catch (Exception e) {
                                Logging.warning("NaiveMChs.ConnectTo(): error cleaning msgtream: " + e.Message);
                            }
                        }
                    }
                    throw;
                }
                if (httpCount == 0 && wssoCount == 0) {
                    msgStream = new InverseMuxStream(streams);
                } else {
                    foreach (WebSocket ws in streams.Take(wssoCount)) {
                        // While it should never receive sth from the server,
                        // call ReadAsync() once to start handle ping/close frames.
                        ws.ReadAsync().Forget();
                    }
                    msgStream = new InverseMuxStream(
                        sendStreams: streams.Take(wssoCount + wsCount),
                        recvStreams: streams.Skip(wssoCount)
                    );
                }
            } else {
                msgStream = await Connect("channels", false, settings, ct);
            }
            var ncs = new NaiveMChannels(new NaiveMultiplexing(msgStream));
            if (settings.EncryptionPerChannel.IsNullOrEmpty() == false) {
                ncs.PerChannelEncryption = settings.EncryptionPerChannel;
                ncs.PerChannelEncryptionKey = settings.Key;
            }
            return ncs;
        }

        private static Task<IMsgStream> Connect(string addStr, bool isHttp, ConnectingSettings settings, CancellationToken ct)
        {
            var req = new NaiveProtocol.Request(AddrPort.Empty) {
                additionalString = addStr
            };
            if (settings.Encryption != null) {
                req.extraStrings = new[] { settings.Encryption, settings.EncryptionPerChannel };
            }
            var reqbytes = req.ToBytes();
            reqbytes = NaiveProtocol.EncryptOrDecryptBytes(true, settings.Key, reqbytes);
            var reqPath = string.Format(settings.UrlFormat, settings.Path, HttpUtil.UrlEncode(Convert.ToBase64String(reqbytes)));
            if (isHttp) {
                return ConnectHttpChunked(settings, settings.Key, reqPath, settings.Encryption, ct);
            } else {
                return ConnectWebSocket(settings, settings.Key, reqPath, settings.Encryption, ct);
            }
        }

        private static async Task<IMsgStream> ConnectWebSocket(ConnectingSettings settings,
            byte[] key, string reqPath, string encType, CancellationToken ct)
        {
            int timeoutms = settings.Timeout * 1000;
            var ws = settings.TlsEnabled
                ? await WebSocketClient.ConnectToTlsAsync(settings.Host, reqPath, timeoutms, ct)
                : await WebSocketClient.ConnectToAsync(settings.Host, reqPath, timeoutms, ct);
            ws.AddToManaged(settings.Timeout / 2, settings.Timeout);
            if (await ws.HandshakeAsync(false, settings.Headers).WithTimeout(timeoutms)) {
                ws.Close();
                throw new TimeoutException("websocket handshake timed out.");
            }
            NaiveProtocol.ApplyEncryption(ws, key, encType);
            return ws;
        }

        private static async Task<IMsgStream> ConnectHttpChunked(ConnectingSettings settings, byte[] key, string reqPath, string encType, CancellationToken ct)
        {
            int timeoutms = settings.Timeout * 1000;
            var stream = settings.TlsEnabled
                ? MyStream.FromStream(await NaiveUtils.ConnectTlsAsync(settings.Host, timeoutms, NaiveUtils.TlsProtocols, ct))
                : MyStream.FromSocket(await NaiveUtils.ConnectTcpAsync(settings.Host, timeoutms, async x => x, ct));
            try {
                async Task request()
                {
                    using (ct.Register(x => {
                        ((IMyStream)x).Close().Forget();
                    }, stream, false)) {
                        var stream2 = MyStream.ToStream(stream);
                        var httpClient = new HttpClient(stream2);
                        var response = await httpClient.Request(new HttpRequest() {
                            Method = "GET",
                            Path = reqPath,
                            Headers = settings.Headers
                        });
                        if (response.StatusCode != "200")
                            throw new Exception($"remote response: '{response.StatusCode} {response.ReasonPhrase}'");
                        if (!response.TestHeader(HttpHeaders.KEY_Transfer_Encoding, HttpHeaders.VALUE_Transfer_Encoding_chunked))
                            throw new Exception("test header failed: Transfer-Encoding != chunked");
                    }
                }
                if (await request().WithTimeout(timeoutms)) {
                    throw new TimeoutException("HTTP requesting timed out.");
                }
                var msf = new HttpChunkedEncodingMsgStream(stream);
                NaiveProtocol.ApplyEncryption(msf, key, encType);
                return msf;
            } catch (Exception) {
                MyStream.CloseWithTimeout(stream).Forget();
                throw;
            }
        }

        public async Task HandleInConnection(NaiveSocks.InConnectionTcp inConnection)
        {
            var r = await Connect(inConnection);
            if (r.Ok) {
                await inConnection.HandleAndPutStream(Adapter, r.Stream, r.WhenCanRead);
            } else {
                await inConnection.HandleAndGetStream(r);
            }
        }

        public async Task<ConnectResult> Connect(ConnectArgument arg)
        {
            var beginTime = DateTime.Now;
            var req = new NaiveProtocol.Request(arg.Dest);
            var result = await Request(req).CAF();
            try {
                async Task<NaiveProtocol.Reply> readReply()
                {
                    var response = await result.GetReply(keepOpen: true).CAF();
                    Logger?.log($"#{result.Channel.Parent.Id}ch{result.Channel.Id}" +
                        $" req={(LogDest ? arg.Dest.ToString() : "***")}" +
                        $" reply={response.status}" +
                            (response.additionalString.IsNullOrEmpty()
                                ? "" : $" ({response.additionalString})") +
                        $" in {(DateTime.Now - beginTime).TotalMilliseconds:0} ms",
                        level: (response.status == 0) ? Logging.Level.Info : Logging.Level.Warning);
                    return response;
                }
                var readReplyTask = readReply();
                if (!InConnectionFastCallback) {
                    var reply = await readReplyTask.CAF();
                    if (reply.status != 0) {
                        result.Channel.Dispose();
                        return new ConnectResult(Adapter, ConnectResultEnum.Failed);
                    }
                }
                return new ConnectResult(Adapter, ConnectResultEnum.OK, new MsgStreamToMyStream(result.Channel)) { WhenCanRead = readReplyTask };
            } catch (Exception) {
                result.Dispose();
                throw;
            }
        }

        public async Task Start()
        {
            Requested = _Requested;
            await (mainTask = base.Start()).CAF();
        }

        private async Task _Requested(ReceivedRequest request)
        {
            var req = request.Value;
            if (req.additionalString.IsNullOrEmpty() || req.additionalString == "connect") {
                if (Adapter == null && ConnectionHandler == null) {
                    await request.Reply(new NaiveProtocol.Reply(AddrPort.Empty, 255, "noinadapter")).CAF();
                    return;
                }
                var inc = new InConnection(this, req, request.Channel);
                if (OnIncomming != null) {
                    await OnIncomming(inc);
                } else if (ConnectionHandler != null) {
                    await Adapter.Controller.HandleInConnection(inc, ConnectionHandler as IConnectionHandler);
                } else {
                    await Adapter.Controller.HandleInConnection(inc).CAF();
                }
            } else if (req.additionalString == "speedtest") {
                await HandleSpeedTest(request.Channel);
            } else if (req.additionalString.StartsWith("dns:")) {
                await HandleDns(request.Channel, req);
            } else if (req.additionalString == "network") {
                await HandleNetwork(request);
            } else {
                Logger?.warning($"unknown cmd: '{req.additionalString}' from {request.Channel}.");
                await request.Reply(new NaiveProtocol.Reply(AddrPort.Empty, 255, "notsupport"));
            }
        }

        private async Task HandleNetwork(ReceivedRequest req)
        {
            var channel = req.Channel;
            await req.Reply(new NaiveProtocol.Reply(AddrPort.Empty, 0, "ok"));
            while (true) {
                string subcommand = await channel.RecvString();
                if (subcommand == null)
                    return;
                var a = NaiveUtils.DeserializeArray(subcommand);
                var m = a[0];
                if (m == "join") {
                    foreach (var item in from x in a.Skip(1) select x.Split('@')) {
                        var nn = item.Length > 1 ? item[1] : "default";
                        var n = NetworkProvider(nn);
                        if (n == null) {
                            await channel.SendString(NaiveUtils.SerializeArray("error", $"network '{nn}' not found"));
                            return;
                        }
                        if (joinedNetworks.Any(x => {
                            if (!x.TryGetTarget(out var tar)) return false;
                            return tar == n;
                        })) continue;
                        n.AddClient(new NClient((from x in item[0].Split(',') where !string.IsNullOrWhiteSpace(x) select x.Trim()).ToArray()) {
                            WhenDisconnected = mainTask,
                            HandleConnection = async (inc) => {
                                await HandleInConnection(inc);
                            }
                        });
                        joinedNetworks.Add(new WeakReference<INetwork>(n));
                    }
                    await channel.SendString(NaiveUtils.SerializeArray("ok"));
                } else {
                    await channel.SendString(NaiveUtils.SerializeArray("notsupport"));
                }
            }
        }

        private async Task HandleDns(Channel ch, NaiveProtocol.Request req)
        {
            var name = req.additionalString.Substring("dns:".Length);
            IPAddress[] addrs = null;
            try {
                var r = await Adapter.Controller.ResolveName(Adapter, AdapterRef.FromAdapter(ConnectionHandler), new DnsRequest(name, DnsRequestType.AnAAAA));
                addrs = r.Addresses;
            } catch (Exception e) {
                Logger?.exception(e, Logging.Level.Warning, $"handling dns request '{name}' from '{ch}'");
                await ch.SendMsg(new NaiveProtocol.Reply(AddrPort.Empty, 1, "dns_failed").ToBytes());
                return;
            }
            await ch.SendMsg(new NaiveProtocol.Reply(AddrPort.Empty, 0, "dns_ok:" + string.Join<IPAddress>("|", addrs)).ToBytes());
        }

        private async Task HandleSpeedTest(Channel ch)
        {
            await ch.SendMsg(new NaiveProtocol.Reply(AddrPort.Empty, 0, "speedtest_ok").ToBytes()).CAF();
            while (true) {
                var cmd = await ch.RecvString().CAF();
                if (cmd == null)
                    return;
                if (cmd == "download") {
                    var buf = new byte[32 * 1024];
                    new Random().NextBytes(buf);
                    try {
                        int sync = 0;
                        while (true) {
                            if (sync >= 128) { await Task.Yield(); sync = 0; }
                            var task = ch.SendMsg(buf).CAF();
                            if (task.GetAwaiter().IsCompleted) sync++; else sync = 0;
                            await task;
                        }
                    } catch (Exception) {
                    }
                } else if (cmd == "upload") {
                    long downloadedBytes = 0;
                    long lastReportMs = 0;
                    Stopwatch sw = Stopwatch.StartNew();
                    int sync = 0;
                    while (true) {
                        if (sync >= 128) { await Task.Yield(); sync = 0; }
                        var msg = await ch.RecvMsgR(null).SyncCounter(ref sync);
                        if (msg.IsEOF)
                            break;
                        var len = msg.Data.tlen;
                        msg.TryRecycle();
                        downloadedBytes += len;
                        var ms = sw.ElapsedMilliseconds;
                        if (ms - lastReportMs >= 1000) {
                            await ch.SendString(ms + "|" + downloadedBytes);
                        }
                        lastReportMs = ms;
                    }
                    return;
                } else if (cmd == "ping") {
                    await ch.SendString("pong").CAF();
                }
            }
        }

        public async Task SpeedTest(Action<string> log)
        {
            Stopwatch sw = Stopwatch.StartNew();
            var resMsg = await Request(new NaiveProtocol.Request(AddrPort.Empty, "speedtest")).CAF();
            var response = await resMsg.GetReply(keepOpen: true);
            using (var ch = resMsg.Channel) {
                if (response.status == 0 && response.additionalString == "speedtest_ok") {
                    log($"Speedtest session created. ({sw.ElapsedMilliseconds:N0} ms)");

                    log("PING TEST...");
                    const int pingCount = 3;
                    long pingSum = 0;
                    for (int i = 0; i < pingCount; i++) {
                        sw.Restart();
                        await ch.SendString("ping");
                        if ((await ch.RecvString()) != "pong")
                            throw new Exception("Unexpected reply. Expected 'pong'.");
                        var ping = sw.ElapsedMilliseconds;
                        pingSum += ping;
                        log($" Ping {i + 1}/{pingCount}: {ping:N0} ms");
                    }
                    long pingAvg = pingSum / pingCount;

                    long toKiBps(long deltaBytes, long deltaMs) => deltaBytes / 1024 * 1000 / deltaMs;
                    float toMbps(long deltaBytes, long deltaMs) => (float)deltaBytes * 8 / (1024 * 1024) * 1000 / deltaMs;

                    log("DOWNLOAD TEST...");
                    await ch.SendString("download");
                    long downloadedBytes = -1;
                    long lastReportBytes = 0;
                    long lastReportMs = 0;
                    BufferPool.GlobalPut(BufferPool.GlobalGet(64 * 1024));
                    while (true) {
                        var msg = await ch.RecvMsgR(null);
                        if (msg.IsEOF)
                            break;
                        if (downloadedBytes == -1) {
                            msg.TryRecycle();
                            log(" Started download.");
                            downloadedBytes = 0;
                            sw.Restart();
                            continue;
                        }
                        downloadedBytes += msg.Data.tlen;
                        msg.TryRecycle();
                        var curMs = sw.ElapsedMilliseconds;
                        if (curMs - lastReportMs >= 1000) {
                            var deltaBytes = downloadedBytes - lastReportBytes;
                            lastReportBytes = downloadedBytes;
                            var deltaMs = curMs - lastReportMs;
                            log($" {lastReportMs:N0} ms - {curMs:N0} ms speed: {toKiBps(deltaBytes, deltaMs):N0} KiB/s, {toMbps(deltaBytes, deltaMs):N2} Mbps");
                            lastReportMs = curMs;
                        }
                        if (sw.ElapsedMilliseconds >= 10000) {
                            ch.CloseIfOpen();
                            break;
                        }
                    }
                    var totalMs = Math.Max(1, sw.ElapsedMilliseconds);
                    var avgKiBps = toKiBps(downloadedBytes, totalMs);
                    log($" Done. Downloaded {downloadedBytes / 1024:N0} KiB in {totalMs:N0} ms." +
                        $" Avg speed: {avgKiBps:N0} KiB/s, {toMbps(downloadedBytes, totalMs):N2} Mbps.");

                    var avgMiBps = avgKiBps / 1024.0;
                    log($"SUMMARY:\n Ping: {pingAvg} ms\n Download: {avgMiBps:N2} MiB/s");
                } else {
                    throw new Exception("unknown response from remote");
                }
            }
        }

        public async Task<DnsResponse> DnsQuery(DnsRequest req)
        {
            if (IPAddress.TryParse(req.Name, out var addr))
                return new DnsResponse(Adapter, addr);
            var resMsg = await Request(new NaiveProtocol.Request(AddrPort.Empty, "dns:" + req.Name) {
                extraStrings = new string[] {
                    ((int)req.Type).ToString()
                }
            });
            var response = await resMsg.GetReply(keepOpen: false);
            if (response.status == 0 && response.additionalString?.StartsWith("dns_ok:") == true) {
                var str = response.additionalString.Substring(7);
                var addrs = from x in str.Split('|') select IPAddress.Parse(x);
                // TODO: try receive TTL from the server
                return new DnsResponse(Adapter, addrs.ToArray());
            } else if (response.additionalString == "dns_failed") {
                throw new Exception("remote returned an error");
            } else {
                throw new Exception("unknown response from remote");
            }
        }

        public async Task JoinNetworks(string[] names)
        {
            using (var resMsg = await Request(new NaiveProtocol.Request(AddrPort.Empty, "network"))) {
                await resMsg.Channel.SendString(NaiveUtils.SerializeArray(new[] { "join" }.Union(names)));
                if ((await resMsg.GetReply(true)).additionalString != "ok") {
                    throw new Exception("remote does not support networks");
                }
                var r = NaiveUtils.DeserializeArray(await resMsg.Channel.RecvString() ?? throw new Exception("unexpected EOF"));
                if (r[0] != "ok") {
                    throw new Exception("remote error: " + string.Join(" | ", r));
                }
            }
        }

        private Task pingTask;
        private int pingTaskVersion = 0;

        public bool PingRunning
        {
            get => pingTask?.IsCompleted == false;
            set {
                if (PingRunning == value)
                    return;
                pingTaskVersion++;
                if (value) {
                    pingTask = _PingTask((t) => Logger?.info($"{BaseChannels}: {t}"), CancellationToken.None, pingTaskVersion);
                } else {
                    pingTask = null;
                }
            }
        }

        public Task<int> Ping()
        {
            return _PingTask(null, CancellationToken.None, -1, true);
        }

        public Task<int> Ping(Action<string> logged, bool once = false, CancellationToken ct = default(CancellationToken))
        {
            return _PingTask(logged, ct, -1, once);
        }

        private async Task<int> _PingTask(Action<string> logged, CancellationToken ct, int id, bool once = false)
        {
            void log(string text)
            {
                logged?.Invoke(text);
            }
            //log("ping starting...");
            Channel ch = null;
            Stopwatch sw = new Stopwatch();
            try {
                sw.Restart();
                var req = await Request(new NaiveProtocol.Request(AddrPort.Empty, "speedtest")).CAF();
                ch = req.Channel;
                var response = await req.GetReply(keepOpen: true).CAF();
                if (response.additionalString != "speedtest_ok") throw new Exception("wrong response");
                if (once) {
                    var ms = sw.ElapsedMilliseconds;
                    log($"RTT: {ms} ms");
                    ch.Dispose();
                    return (int)ms;
                }
            } catch (Exception e) {
                log("ping starting error: " + e.Message);
                ch?.Dispose();
                throw;
            }
            try {
                using (ch) {
                    log($"ping ready. on {ch}");
                    while (true) {
                        ct.ThrowIfCancellationRequested();
                        if (id >= 0 && pingTaskVersion != id) {
                            log("ping end.");
                            return (int)sw.ElapsedMilliseconds;
                        }
                        var intervalLimiter = Task.Delay(1000);
                        sw.Restart();
                        await ch.SendString("ping").CAF();
                        var reply = await ch.RecvString().CAF();
                        sw.Stop();
                        if (reply == null)
                            throw new Exception("recv EOF");
                        if (reply != "pong")
                            throw new Exception("wrong response (expected 'pong')");
                        log($"RTT: {sw.ElapsedMilliseconds} ms");
                        ct.ThrowIfCancellationRequested();
                        await intervalLimiter.CAF();
                    }
                }
            } catch (Exception e) {
                log("ping end: " + e.Message);
                throw;
            }
        }

        public class InConnection : NaiveSocks.InConnectionTcp
        {
            public NaiveMChannels Ncs { get; }
            public Channel Channel { get; }
            Stopwatch sw;
            bool lz4;

            public InConnection(NaiveMChannels ncs, NaiveProtocol.Request req, Channel channel) : base(ncs.Adapter)
            {
                Ncs = ncs;
                Channel = channel;
                this.Dest = req.dest;
                this.lz4 = req.extraStrings.Contains("lz4");
                sw = Stopwatch.StartNew();
            }

            protected override async Task OnConnectionResult(ConnectResultBase resultBase)
            {
                var result = resultBase as ConnectResult;
                var addstr = result.FailedReason;
                var time = sw.ElapsedMilliseconds + " ms";
                if (string.IsNullOrEmpty(addstr))
                    addstr = time;
                else
                    addstr = addstr + " [" + time + "]";
                var response = new NaiveProtocol.Reply() {
                    remoteEP = AddrPort.Parse(result.destEP?.ToString() ?? "0:0"),
                    status = (byte)(result.Ok ? 0 : 1),
                    additionalString = addstr
                };
                if (lz4)
                    Channel.DataFilter.ApplyFilterFromFilterCreator(LZ4pn.LZ4Filter.GetFilter);
                await Channel.SendMsg(new Msg(new BytesView(response.ToBytes()))).CAF();
                if (result.Ok) {
                    DataStream = new MsgStreamToMyStream(Channel);
                } else {
                    Channel.Close(new CloseOpt(CloseType.Close)).Forget();
                }
                await base.OnConnectionResult(resultBase);
            }
        }
    }

    public class MsgStreamFilter : FilterBase, IMsgStream
    {
        public MsgStreamFilter(IMsgStream baseStream)
        {
            BaseStream = baseStream;
        }

        public IMsgStream BaseStream { get; }

        public MsgStreamStatus State => BaseStream.State;

        public Task Close(CloseOpt closeOpt)
        {
            return BaseStream.Close(closeOpt);
        }

        public async Task<Msg> RecvMsg(BytesView buf)
        {
            var msg = await BaseStream.RecvMsg(buf);
            if (!msg.IsEOF && msg.Data.tlen > 0)
                OnRead(msg.Data);
            return msg;
        }

        public Task SendMsg(Msg msg)
        {
            if (!msg.IsEOF && msg.Data.tlen > 0)
                OnWrite(msg.Data);
            return BaseStream.SendMsg(msg);
        }
    }

    public class HttpChunkedEncodingMsgStream : FilterBase, IMsgStream
    {
        public HttpChunkedEncodingMsgStream(IMyStream baseStream)
        {
            this.BaseStream = baseStream;
        }

        public IMyStream BaseStream { get; }
        private Stream asStream;

        public MsgStreamStatus State => (MsgStreamStatus)BaseStream.State;

        bool isClosed;

        public Task Close(CloseOpt closeOpt)
        {
            if (closeOpt.CloseType == CloseType.Shutdown) {
                return BaseStream.Shutdown(closeOpt.ShutdownType);
            } else {
                isClosed = true;
                return BaseStream.Close();
            }
        }

        byte[] _crlfBuffer = new byte[2];

        public async Task<Msg> RecvMsg(BytesView buf)
        {
            if (asStream == null)
                asStream = BaseStream.ToStream();
            ReRecv:
            var lengthStr = await NaiveUtils.ReadStringUntil(asStream, NaiveUtils.CRLFBytes, maxLength: 32, withPattern: false);
            latestRecv = WebSocket.CurrentTimeRough;
            if (lengthStr == "01") {
                await NaiveUtils.ReadBytesUntil(asStream, NaiveUtils.CRLFBytes, maxLength: 32, withPattern: false);
                goto ReRecv;
            }
            var chunkSize = Convert.ToInt32(lengthStr, 16);
            if (chunkSize == 0) {
                return Msg.EOF;
            }
            var buffer = new byte[chunkSize];
            var pos = 0;
            do {
                int read;
                pos += read = await BaseStream.ReadAsync(new BytesSegment(buffer, pos, chunkSize - pos)).CAF();
                if (read == 0)
                    throw new DisconnectedException("unexpected EOF while reading chunked http request content.");
            } while (pos < chunkSize);
            pos = 0;
            do { // read CRLF
                int read;
                pos += read = await BaseStream.ReadAsync(new BytesSegment(_crlfBuffer, pos, 2 - pos));
                if (read == 0)
                    throw new DisconnectedException("unexpected EOF while reading chunked http request content.");
            } while (pos < 2);
            if (_crlfBuffer[0] != '\r' && _crlfBuffer[1] != '\n') {
                throw new Exception($"not a CRLF after chunk! {_crlfBuffer[0]} {_crlfBuffer[1]}");
            }
            var bv = new BytesView(buffer);
            OnRead(bv);
            return new Msg(bv);
        }

        private readonly object _lockLatestSendTask = new object();
        private Task _latestSendTask;

        public Task SendMsg(Msg msg)
        {
            lock (_lockLatestSendTask) {
                if (_latestSendTask?.IsCompleted == false) {
                    return _latestSendTask = _SendMsgQueued(_latestSendTask, msg);
                } else {
                    return _latestSendTask = _SendMsg(msg);
                }
            }
        }

        private async Task _SendMsgQueued(Task taskToWait, Msg msg)
        {
            try {
                await taskToWait;
            } catch (Exception) { }
            await _SendMsg(msg);
        }

        private Task _SendMsg(Msg msg)
        {
            //Logging.debug("send: " + msg.Data.tlen);
            latestSend = WebSocket.CurrentTimeRough;
            OnWrite(msg.Data);
            var tlen = msg.Data.tlen;
            var chunkHeader = getChunkSizeBytes(tlen);
            if (BaseStream is IMyStreamMultiBuffer bvs && tlen > 128) {
                var bv = new BytesView(chunkHeader) { nextNode = msg.Data };
                bv.lastNode.nextNode = new BytesView(NaiveUtils.CRLFBytes);
                return bvs.WriteMultipleAsyncR(bv).ToTask();
            } else {
                var bufferSize = chunkHeader.Length + tlen + 2;
                var buffer = new byte[bufferSize];
                var cur = 0;
                WriteToBuffer(buffer, ref cur, chunkHeader);
                foreach (var item in msg.Data) {
                    if (item.len > 0) {
                        WriteToBuffer(buffer, ref cur, item.bytes, item.offset, item.len);
                    }
                }
                WriteToBuffer(buffer, ref cur, NaiveUtils.CRLFBytes, 0, 2);
                return BaseStream.WriteAsync(buffer);
            }
        }

        void WriteToBuffer(byte[] buffer, ref int cur, byte[] src)
        {
            WriteToBuffer(buffer, ref cur, src, 0, src.Length);
        }

        void WriteToBuffer(byte[] buffer, ref int cur, byte[] src, int offset, int count)
        {
            NaiveUtils.CopyBytes(src, offset, buffer, cur, count);
            cur += count;
        }

        private static byte[] getChunkSizeBytes(int size)
        {
            string chunkSize = Convert.ToString(size, 16) + "\r\n";
            return Encoding.UTF8.GetBytes(chunkSize);
        }

        int latestSend = WebSocket.CurrentTimeRough;
        int latestRecv = WebSocket.CurrentTimeRough;

        public void AddManagedTask(bool isSending, int timeout)
        {
            WebSocket.AddManagementTask(() => {
                if (isClosed || State == MsgStreamStatus.Close)
                    return true;
                if (isSending) {
                    if (WebSocket.CurrentTime - latestSend > timeout) {
                        SendKeepaliveMessage();
                    }
                } else {
                    if (WebSocket.CurrentTime - latestRecv > timeout) {
                        Task.Run(() => {
                            Close(CloseOpt.Close);
                        });
                    }
                }
                return false;
            });
        }

        private void SendKeepaliveMessage()
        {
            lock (_lockLatestSendTask) {
                var tmp = _latestSendTask;
                _latestSendTask = NaiveUtils.RunAsyncTask(async () => {
                    try {
                        if (tmp != null)
                            await tmp;
                        await BaseStream.WriteAsync(NaiveUtils.GetUTF8Bytes("01\r\n" + (char)NaiveUtils.Random.Next('A', 'z') + "\r\n"));
                        latestSend = WebSocket.CurrentTimeRough;
                    } catch (Exception) {
                        if (State != MsgStreamStatus.Close)
                            await Close(CloseOpt.Close);
                    }
                });
            }
        }
    }
}
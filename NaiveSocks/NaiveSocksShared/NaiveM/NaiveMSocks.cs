using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Diagnostics;
using System.Collections.Generic;

namespace NaiveSocks
{
    public class NaiveMSocks : RrChannels<NaiveProtocol.Request, NaiveProtocol.Reply>
    {
        public IInAdapter InAdapter;

        public IConnectionHandler OutAdapter;

        public Action<string> Logged;

        public Func<string, INetwork> GetNetwork;

        public Func<NaiveSocks.InConnection, Task> HandleRemoteInConnection;

        public bool InConnectionFastCallback;

        Task mainTask;

        List<WeakReference<INetwork>> joinedNetworks = new List<WeakReference<INetwork>>();

        public NaiveMSocks(NaiveMultiplexing channels) : base(channels)
        {
            MsgRequestConverter = x => NaiveProtocol.Request.Parse(x.Data.GetBytes());
            RequestMsgConverter = x => x.ToBytes();
            MsgReplyConverter = x => NaiveProtocol.Reply.Parse(x.Data.GetBytes());
            ReplyMsgConverter = x => x.ToBytes();
        }

        public class ConnectingSettings
        {
            public AddrPort Host { get; set; }
            public string Path { get; set; }
            public byte[] Key { get; set; }
            public string KeyString { set => Key = NaiveProtocol.GetRealKeyFromString(value); }

            public Dictionary<string, string> Headers { get; set; }
            public string UrlFormat { get; set; } = "{0}?token={1}";

            public int ImuxHttpConnections { get; set; } = 0;
            public int ImuxConnections { get; set; } = 1;
            public int ImuxConnectionsDelay { get; set; } = 0;
            public int Timeout { get; internal set; }
        }

        public static Task<NaiveMSocks> ConnectTo(AddrPort host, string path, string key)
            => ConnectTo(new ConnectingSettings {
                Host = host,
                Path = path,
                Key = NaiveProtocol.GetRealKeyFromString(key),
            });

        public static Task<NaiveMSocks> ConnectTo(AddrPort host, string path, byte[] key)
            => ConnectTo(new ConnectingSettings {
                Host = host,
                Path = path,
                Key = key
            });

        public static async Task<NaiveMSocks> ConnectTo(ConnectingSettings settings)
        {
            const string ImuxPrefix = "chs2:";
            var key = settings.Key;
            if (settings.Headers?.ContainsKey("Host") == false) {
                settings.Headers["Host"] = (settings.Host.Port == 80) ? settings.Host.Host : settings.Host.ToString();
            }
            async Task<IMsgStream> connect(string addStr, bool isHttp = false)
            {
                var req = new NaiveProtocol.Request(new AddrPort("", 0)) {
                    additionalString = addStr
                };
                var reqbytes = req.ToBytes();
                reqbytes = NaiveProtocol.EncryptOrDecryptBytes(true, key, reqbytes);
                var reqPath = string.Format(settings.UrlFormat, settings.Path, HttpUtil.UrlEncode(Convert.ToBase64String(reqbytes)));
                if (isHttp) {
                    var r = await ConnectHelper.Connect(null, settings.Host, 10);
                    r.ThrowIfFailed();
                    try {
                        var stream = r.Stream;
                        var stream2 = MyStream.ToStream(stream);
                        var httpClient = new HttpClient(stream2);
                        var response = await httpClient.Request(new HttpRequest() {
                            Method = "GET",
                            Path = settings.Path,
                            Headers = settings.Headers
                        });
                        if (response.StatusCode != "200")
                            throw new Exception($"remote response: '{response.StatusCode} {response.ReasonPhrase}'");
                        if (!response.TestHeader(HttpHeaders.KEY_Transfer_Encoding, HttpHeaders.VALUE_Transfer_Encoding_chunked))
                            throw new Exception("test header failed: Transfer-Encoding != chunked");
                        var chunkedStream = InputDataStream.FromStreamChunked(stream2);
                        throw new NotImplementedException();
                    } catch (Exception) {
                        MyStream.CloseWithTimeout(r.Stream);
                        throw;
                    }
                } else {
                    var ws = await WebSocketClient.ConnectToAsync(settings.Host, reqPath);
                    await ws.HandshakeAsync(false, settings.Headers);
                    ws.ManagedPingTimeout = settings.Timeout / 2;
                    ws.ManagedCloseTimeout = settings.Timeout;
                    ws.AddToManaged();
                    ws.ApplyAesStreamFilter(key);
                    return ws;
                }
            }
            IMsgStream msgStream;
            int count = settings.ImuxConnections;
            string sid = Guid.NewGuid().ToString("N").Substring(0, 8);
            if (count > 1) {
                string strCount = count.ToString();
                var tasks = Enumerable.Range(0, count).Select(x => NaiveUtils.RunAsyncTask(async () => {
                    if (settings.ImuxConnectionsDelay > 0)
                        await Task.Delay(settings.ImuxConnectionsDelay * x);
                    return await connect(ImuxPrefix + NaiveUtils.SerializeArray(sid, strCount, x.ToString()));
                })).ToArray();
                var streams = await Task.WhenAll(tasks);
                msgStream = new InverseMuxStream(streams);
            } else {
                msgStream = await connect("channels");
            }
            var ncs = new NaiveMSocks(new NaiveMultiplexing(msgStream));
            return ncs;
        }

        public async Task HandleInConnection(NaiveSocks.InConnection inConnection)
        {
            var beginTime = DateTime.Now;

            using (var req = await Request(new NaiveProtocol.Request(inConnection.Dest)).CAF()) {
                async Task<NaiveProtocol.Reply> readReply()
                {
                    var response = await req.GetReply(keepOpen: true).CAF();
                    Logged?.Invoke($"#{req.Channel.Parent.Id}ch{req.Channel.Id} req={inConnection.Dest} " +
                        $"reply={response.status}{(string.IsNullOrEmpty(response.additionalString) ? "" : $" ({response.additionalString})")} " +
                        $"in {(DateTime.Now - beginTime).TotalMilliseconds:0} ms");
                    return response;
                }
                var readReplyTask = readReply();
                if (!InConnectionFastCallback) {
                    var reply = await readReplyTask.CAF();
                    if (reply.status != 0) {
                        req.Channel.Dispose();
                        await inConnection.SetConnectResult(ConnectResults.Failed, null).CAF();
                        return;
                    }
                }
                //await inConnection.SetConnectResult(ConnectResults.Conneceted, null).CAF();
                //await MyStream.Relay(new MsgStreamToMyStream(req.Channel), inConnection.DataStream, readReplyTask).CAF();
                await inConnection.RelayWith(new MsgStreamToMyStream(req.Channel), readReplyTask);
            }
        }

        public async Task<ConnectResult> Connect(ConnectArgument arg)
        {
            var beginTime = DateTime.Now;

            var req = await Request(new NaiveProtocol.Request(arg.Dest)).CAF();
            try {
                async Task<NaiveProtocol.Reply> readReply()
                {
                    var response = await req.GetReply(keepOpen: true).CAF();
                    Logged?.Invoke($"#{req.Channel.Parent.Id}ch{req.Channel.Id} req={arg.Dest} " +
                        $"reply={response.status}{(string.IsNullOrEmpty(response.additionalString) ? "" : $" ({response.additionalString})")} " +
                        $"in {(DateTime.Now - beginTime).TotalMilliseconds:0} ms");
                    return response;
                }
                var readReplyTask = readReply();
                if (!InConnectionFastCallback) {
                    var reply = await readReplyTask.CAF();
                    if (reply.status != 0) {
                        req.Channel.Dispose();
                        return new ConnectResult(ConnectResults.Failed);
                    }
                }
                return new ConnectResult(ConnectResults.Conneceted, new MsgStreamToMyStream(req.Channel)) { WhenCanRead = readReplyTask };
            } catch (Exception) {
                req.Dispose();
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
            if (req.additionalString == "" || req.additionalString == "connect") {
                if (InAdapter == null & HandleRemoteInConnection == null) {
                    await request.Reply(new NaiveProtocol.Reply(AddrPort.Empty, 255, "noinadapter")).CAF();
                    return;
                }
                var inc = new InConnection(this, req, request.Channel);
                if (HandleRemoteInConnection != null) {
                    await HandleRemoteInConnection(inc).CAF();
                } else {
                    await InAdapter.Controller.HandleInConnection(inc).CAF();
                }
            } else if (req.additionalString == "speedtest") {
                await HandleSpeedTest(request.Channel);
            } else if (req.additionalString?.StartsWith("dns:") == true) {
                await HandleDns(request.Channel, req);
            } else if (req.additionalString?.StartsWith("network") == true) {
                await HandleNetwork(request);
            } else {
                Logging.warning($"{request.Channel} unknown cmd: {req.additionalString}");
                await request.Reply(new NaiveProtocol.Reply(AddrPort.Empty, 255, "notsupport"));
            }
        }

        private async Task HandleNetwork(ReceivedRequest req)
        {
            var channel = req.Channel;
            await req.Reply(new NaiveProtocol.Reply(AddrPort.Empty, 0, "ok"));
            while (true) {
                var a = NaiveUtils.DeserializeArray(await channel.RecvString());
                var m = a[0];
                if (m == "join") {
                    foreach (var item in from x in a.Skip(1) select x.Split('@')) {
                        var nn = item.Length > 1 ? item[1] : "default";
                        var n = GetNetwork(nn);
                        if (n == null) {
                            await channel.SendString(NaiveUtils.SerializeArray("error", $"network {nn} not found"));
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
                addrs = await Dns.GetHostAddressesAsync(name);
            } catch (Exception) {
                await ch.SendMsg(new NaiveProtocol.Reply(AddrPort.Empty, 1, "dns_failed").ToBytes());
                return;
            }
            await ch.SendMsg(new NaiveProtocol.Reply(AddrPort.Empty, 0, "dns_ok:" + string.Join<IPAddress>("|", addrs)).ToBytes());
        }

        private Encoding ASCII => Encoding.ASCII;

        private async Task HandleSpeedTest(Channel ch)
        {
            await ch.SendMsg(new NaiveProtocol.Reply(AddrPort.Empty, 0, "speedtest_ok").ToBytes()).CAF();
            while (true) {
                var cmd = await ch.RecvString().CAF();
                if (cmd == null)
                    return;
                if (cmd == "download") {
                    var buf = new byte[32 * 1024];
                    while (true) {
                        await ch.SendMsg(buf).CAF();
                    }
                } else if (cmd == "upload") {
                    while ((await ch.RecvMsg(null).CAF()).IsEOF == false) {
                    }
                    return;
                } else if (cmd == "ping") {
                    await ch.SendString("pong").CAF();
                }
            }
        }

        public async Task<IPAddress[]> DnsQuery(string name)
        {
            var resMsg = await Request(new NaiveProtocol.Request(AddrPort.Empty, "dns:" + name));
            var response = await resMsg.GetReply(keepOpen: false);
            if (response.status == 0 && response.additionalString?.StartsWith("dns_ok:") == true) {
                var addrs = from x in response.additionalString.Split('|') select IPAddress.Parse(x);
                return addrs.ToArray();
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
                    pingTask = _PingTask((t) => Logged?.Invoke($"{BaseChannels.ToString()}: {t}"), CancellationToken.None, pingTaskVersion);
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

        public class InConnection : NaiveSocks.InConnection
        {
            public NaiveMSocks Ncs { get; }
            public Channel Channel { get; }
            Stopwatch sw;

            public InConnection(NaiveMSocks ncs, NaiveProtocol.Request req, Channel channel) : base(ncs.InAdapter)
            {
                Ncs = ncs;
                Channel = channel;
                this.Dest = req.dest;
                sw = Stopwatch.StartNew();
            }

            protected override async Task OnConnectionResult(ConnectResult result)
            {
                var addstr = result.FailedReason;
                var time = sw.ElapsedMilliseconds + " ms";
                if (string.IsNullOrEmpty(addstr))
                    addstr = time;
                else
                    addstr = addstr + " [" + time + "]";
                var response = new NaiveProtocol.Reply() {
                    remoteEP = AddrPort.Parse(result.destEP.ToString()),
                    status = (byte)(result.Ok ? 0 : 1),
                    additionalString = addstr
                };
                await Channel.SendMsg(new Msg(new BytesView(response.ToBytes()))).CAF();
                if (result.Ok) {
                    this.DataStream = new MsgStreamToMyStream(Channel);
                } else {
                    Channel.Close(new CloseOpt(CloseType.Close)).Forget();
                }
            }
        }

    }
}
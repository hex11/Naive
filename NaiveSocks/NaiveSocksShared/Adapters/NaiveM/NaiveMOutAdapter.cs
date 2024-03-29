﻿using System.Threading.Tasks;
using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Nett;
using System.Threading;
using System.Text;

namespace NaiveSocks
{
    public class NaiveMOutAdapter : OutAdapter2, IInAdapter, ICanReload
    {
        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("server", server);
            if (tls)
                ctx.AddTag("TLS");
            ctx.AddField("enc", encryption);
        }

        public string uri { get; set; }
        public AddrPort server { get; set; } // default port is 80 or 443 (with tls)
        public string path { get; set; } = "/";
        public string key { get; set; }

        public bool connect_on_start { get; set; } = false;
        public int pool_min_free { get; set; } = 1;
        public int pool_concurrency { get; set; } = 32;
        public int pool_max { get; set; } = 0;
        public int pool_max_free { get; set; } = 8;
        public bool pool_prefer_connected { get; set; } = false;

        public int connect_delay { get; set; } = 1;
        public int connect_delay_multiplier { get; set; } = 2;
        public int connect_delay_max { get; set; } = 36;

        public Dictionary<string, string> headers { get; set; }

        public int imux_ws { get; set; } = 1;
        public int imux_http { get; set; } = 0;
        public int imux_wsso { get; set; } = 0;
        public int imux_delay { get; set; } = 20;

        public int timeout { get; set; } = 30;

        public static string DefaultEncryption { get; set; }

        public string encryption { get; set; } = DefaultEncryption; // [enc1][,enc2][//per_ch_enc1[,per_ch_enc2]]
        public string encryption_per_ch { get; set; } = null;

        public bool tls { get; set; }
        public bool tls_only { get; set; }

        int _multiplied_delay = 0;
        int _using_delay => Math.Max(Math.Max(_multiplied_delay, connect_delay), 0);

        public AdapterRef @out { get; set; }

        public string network { get; set; } // name1[,name2...][@networkA][ nameB1,nameB2[@networkB]...]

        private DateTime lastFailedNetworkJoin = DateTime.MinValue;

        public bool fastopen { get; set; } = true;

        public bool log_dest { get; set; } = true;

        private bool _ping_enabled;
        public bool ping_enabled
        {
            get => _ping_enabled;
            set {
                _ping_enabled = value;
                lock (ncsPool)
                    foreach (var item in ncsPool) {
                        if (item.nms != null)
                            item.nms.PingRunning = value;
                    }
            }
        }

        internal List<PoolItem> ncsPool = new List<PoolItem>();

        private NaiveMChannels.ConnectingSettings connectingSettings;

        private void UpdateConnectingSettings()
        {
            connectingSettings = new NaiveMChannels.ConnectingSettings {
                // Note that Headers may be modified in NaiveMChannels.ConnectTo()
                Headers = new Dictionary<string, string>(headers),
                Host = server,
                KeyString = key,
                Encryption = encryption,
                EncryptionPerChannel = encryption_per_ch,
                TlsEnabled = tls,
                Path = path,
                ImuxWsConnections = imux_ws,
                ImuxHttpConnections = imux_http,
                ImuxWsSendOnlyConnections = imux_wsso,
                ImuxConnectionsDelay = imux_delay,
                Timeout = timeout
            };
        }

        public NaiveMOutAdapter()
        {
            CheckDefaultEncryption();
        }

        private static void CheckDefaultEncryption()
        {
            if (DefaultEncryption != null)
                return;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                DefaultEncryption = NaiveProtocol.EncryptionAesOfb128;
            } else {
                DefaultEncryption = NaiveProtocol.EncryptionSpeck0;
            }
            DefaultEncryption = NaiveProtocol.HashCrc32c + "," + DefaultEncryption;
            Logging.info($"NaiveOutAdapter default encryption chosen: '{DefaultEncryption}'");
        }

        public bool Reloading(object oldInstance)
        {
            var old = oldInstance as NaiveMOutAdapter;
            if (old.server == this.server
                && old.path == this.path
                && old.key == this.key) {
                ncsPool = old.ncsPool;
                Logger.info($"reload with {ncsPool.Count} old connections.");
            }
            return false;
        }

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            if (key == null) {
                Logger.error("'key' is not specified.");
                return;
            }
            if (toml.TryGetValue("imux", out var imux)) {
                if (imux is TomlArray ta) {
                    var imuxarr = ta.Get<int[]>();
                    imux_ws = imuxarr[0];
                    imux_http = imuxarr[1];
                    imux_wsso = imuxarr[2];
                } else if (imux is TomlValue tv) {
                    imux_ws = tv.Get<int>();
                } else {
                    throw new Exception("unexpected 'imux' value type: " + imux.GetType());
                }
            }
            uri = uri ?? toml.TryGetValue<string>("url", null);
            if (!uri.IsNullOrEmpty()) {
                var parsedUri = new Uri(uri);
                if (!parsedUri.IsAbsoluteUri)
                    throw new Exception("not an absolute URI!");
                switch (parsedUri.Scheme) {
                    case "http":
                    case "ws":
                    case "naivem":
                        break;
                    case "https":
                    case "wss":
                        tls = true;
                        break;
                    default:
                        throw new Exception($"Unknown URI scheme '{parsedUri.Scheme}'");
                }
                server = new AddrPort {
                    Host = parsedUri.Host,
                    Port = parsedUri.IsDefaultPort ? 0 : parsedUri.Port
                };
                path = parsedUri.AbsolutePath;
            }
            if (tls_only) {
                tls = true;
                encryption = NaiveProtocol.EncryptionNone;
            }
            server = server.WithDefaultPort(tls ? 443 : 80);
        }

        static readonly string[] UAs = new[] {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:102.0) Gecko/20100101 Firefox/102.0",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/117.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/117.0.0.0 Safari/537.36 Edg/117.0.2045.36",
        };

        protected override void OnInit()
        {
            base.OnInit();
            if (@out == null && network != null) {
                @out = Controller.AdapterRefFromName("direct");
            }
            if (headers == null)
                headers = new Dictionary<string, string>();
            if (headers.ContainsKey("User-Agent") == false) {
                headers["User-Agent"] = UAs[NaiveUtils.Random.Next(UAs.Length)];
            }
        }

        protected override void OnStart()
        {
            base.OnStart();
            UpdateConnectingSettings();
            if (connect_on_start || network != null) {
                CheckPoolWithDelay();
            }
        }

        protected override void OnStop()
        {
            base.OnStop();
            lock (ncsPool) {
                for (int i = ncsPool.Count - 1; i >= 0; i--) {
                    PoolItem item = ncsPool[i];
                    CloseAndRemoveItem_NoLock(item);
                }
            }
        }

        object timerLock = new object();
        Timer timer;
        int timerCurrentDueTime = -1;

        private void CheckPoolWithDelay()
        {
            if (timer == null) {
                timer = new Timer((x) => {
                    lock (timerLock) {
                        timerCurrentDueTime = -1;
                        if (!IsRunning)
                            return;
                    }
                    CheckPool();
                });
            }
            var ms = _using_delay * 1000;
            lock (timerLock) {
                if (timerCurrentDueTime == -1 || timerCurrentDueTime > ms) {
                    timerCurrentDueTime = ms;
                    timer.Change(ms, -1);
                }
            }
        }

        private void CheckPool()
        {
            lock (ncsPool) {
                List<PoolItem> toBeRemoved = null;
                if (pool_max > 0 && ncsPool.Count >= pool_max)
                    return;
                int avaliable = 0;
                int idle = 0;
                int max_idle = pool_max_free >= 0 ? Math.Max(pool_min_free, pool_max_free) : -1;
                foreach (var item in ncsPool) {
                    if (item.connectTask?.IsCompleted == true && item.nms == null) {
                        Logger.warning("found a strange pool item, removing. (mono bug?)");
                        (toBeRemoved ?? (toBeRemoved = new List<PoolItem>())).Add(item);
                        continue;
                    }
                    int localCh = item.ConnectionsCount;
                    int remoteCh = item?.nms?.BaseChannels.TotalRemoteChannels ?? 0;
                    if (localCh + remoteCh < pool_concurrency) {
                        avaliable++;
                    }
                    if (localCh == 0 && remoteCh == 0) {
                        idle++;
                        if (max_idle >= 0 && idle > max_idle) {
                            (toBeRemoved ?? (toBeRemoved = new List<PoolItem>())).Add(item);
                        }
                    }
                }
                if (toBeRemoved != null)
                    foreach (var item in toBeRemoved) {
                        CloseAndRemoveItem_NoLock(item);
                    }
                int countToCreate = pool_min_free - avaliable;
                if (countToCreate > 0) {
                    Logger.info($"creating {countToCreate} connections...");
                    for (int i = 0; i < countToCreate; i++) {
                        NewPoolItem();
                    }
                }
            }

            PoolItem NewPoolItem()
            {
                var pi = new PoolItem(this);
                pi.Connected += (x) => {
                    _multiplied_delay = connect_delay;
                    x.nms.PingRunning = ping_enabled;
                };
                pi.Error += (x, e) => {
                    _multiplied_delay = _using_delay;
                    _multiplied_delay *= connect_delay_multiplier;
                    _multiplied_delay = Math.Min(connect_delay_max, _multiplied_delay);
                };
                pi.Closed += (x) => {
                    RemovePoolItem(x);
                    CheckPoolWithDelay();
                };
                AddPoolItem(pi);
                Task.Run(pi.ConnectIfNot);
                return pi;
            }
        }

        private void CloseAndRemoveItem_NoLock(PoolItem item)
        {
            ncsPool.Remove(item);
            try {
                item.nms.BaseChannels.Close(true);
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "Error closing channels");
            }
        }

        private void RemovePoolItem(PoolItem x)
        {
            lock (ncsPool)
                ncsPool.Remove(x);
        }

        private void AddPoolItem(PoolItem pi)
        {
            lock (ncsPool)
                ncsPool.Add(pi);
        }

        private PoolItem GetPoolItem()
        {
            CheckPool();
            PoolItem result = null;
            lock (ncsPool) {
                foreach (var set in new[] { ncsPool.Where(x => x.IsConnected), ncsPool }) {
                    foreach (var item in set) {
                        if (result == null || result.ConnectionsCount > item.ConnectionsCount) {
                            result = item;
                        }
                    }
                    if (pool_prefer_connected && result != null)
                        break;
                }
            }
            return result;
        }

        public override Task<ConnectResult> ProtectedConnect(ConnectArgument arg)
        {
            var ncs = GetPoolItem();
            return ncs.Connect(arg);
        }

        public override Task HandleConnection(InConnection connection)
        {
            if (connection is InConnectionDns dns) return ResolveName(dns);
            return base.HandleConnection(connection);
        }

        public async Task ResolveName(InConnectionDns dns)
        {
            var ncs = GetPoolItem();
            await ncs.ConnectIfNot();
            var resp = await ncs.nms.DnsQuery(dns.DnsRequest);
            await dns.SetResult(resp);
        }

        public async Task SpeedTest(Action<string> log)
        {
            var ncs = GetPoolItem();
            await ncs.ConnectIfNot();
            log("Selected session: " + ncs.nms.BaseChannels);
            await ncs.nms.SpeedTest(log);
        }

        internal class PoolItem
        {
            private NaiveMOutAdapter adapter;

            Logger Logger => adapter.Logger;

            private object _lock => this;

            public NaiveMChannels nms;
            public Task connectTask;

            public int ConnectionsCount => nms?.BaseChannels?.TotalLocalChannels ?? 0;
            public bool IsConnected => nms != null;

            public event Action<PoolItem> Connected;
            public event Action<PoolItem, Exception> Error;
            public event Action<PoolItem> Closed;

            int state = 0;

            public PoolItem(NaiveMOutAdapter adapter)
            {
                this.adapter = adapter;
            }

            private async Task Connect()
            {
                try {
                    var beginTime = DateTime.Now;
                    state = 1;
                    var ct = new CancellationTokenSource(30 * 1000).Token;
                    var nms = await NaiveMChannels.ConnectTo(adapter.connectingSettings, ct);
                    state = 2;
                    nms.Adapter = adapter;
                    nms.Logger = Logger;
                    nms.LogDest = adapter.log_dest;
                    nms.OnIncomming = (inc) => {
                        Logger.info($"inbound {(inc as NaiveMChannels.InConnection)?.Channel}" +
                                        $" (dest={inc.Dest}) redirecting to 127.1");
                        adapter.CheckPool();
                        inc.Dest.Host = "127.0.0.1";
                        return adapter.Controller.HandleInConnection(inc);
                    };
                    nms.InConnectionFastCallback = adapter.fastopen;
                    Logger.info($"connected: {nms.BaseChannels} in {(DateTime.Now - beginTime).TotalMilliseconds:0} ms");
                    state = 3;
                    if (adapter.network != null && adapter.lastFailedNetworkJoin < DateTime.Now.AddMinutes(5))
                        NaiveUtils.RunAsyncTask(async () => {
                            try {
                                await nms.JoinNetworks(adapter.network.Split(' '));
                                Logger.info($"{nms.BaseChannels} joined network(s).");
                            } catch (Exception e) {
                                if (adapter.lastFailedNetworkJoin < DateTime.Now.AddMinutes(5))
                                    Logger.error($"{nms.BaseChannels}: joining network(s) error: {e.Message}");
                                if (!nms.BaseChannels.Closed)
                                    adapter.lastFailedNetworkJoin = DateTime.Now;
                            }
                        }).Forget();
                    this.nms = nms;
                    Connected?.Invoke(this);
                    state = 4;
                    NaiveUtils.RunAsyncTask(async () => {
                        try {
                            state = 5;
                            await nms.Start();
                        } catch (Exception e) {
                            Logger.exception(e, Logging.Level.Error, $"{nms.BaseChannels} error");
                            Error?.Invoke(this, e);
                        } finally {
                            state = 6;
                            Closed?.Invoke(this);
                            //connectTask = null;
                            nms = null;
                        }
                    }).Forget();
                } catch (Exception e) {
                    state = 7;
                    Error?.Invoke(this, e);
                    Closed?.Invoke(this);
                    //connectTask = null;
                    nms = null;
                    if (e.IsConnectionException()) {
                        Logger.error($"connecting failed: {e.Message}");
                    } else {
                        Logger.exception(e, Logging.Level.Error, "connecting failed");
                    }
                    throw;
                }
            }

            public async Task ConnectIfNot()
            {
                if (nms == null) {
                    if (connectTask == null) { // double-checked locking
                        lock (_lock) {
                            if (connectTask == null) {
                                connectTask = Connect();
                            }
                        }
                    }
                    await connectTask;
                }
            }

            public async Task<ConnectResult> Connect(ConnectArgument arg)
            {
                await ConnectIfNot();
                arg.CancellationToken.ThrowIfCancellationRequested();
                return await nms.Connect(arg);
            }
        }
    }
}

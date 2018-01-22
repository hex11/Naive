﻿using System.Threading.Tasks;
using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Nett;

namespace NaiveSocks
{
    public class NaiveMOutAdapter : OutAdapter2, IInAdapter, ICanReloadBetter, IDnsProvider
    {
        public override string ToString() => $"{{NaiveMOut server={server}}}";

        public AddrPort server { get; set; }
        public string path { get; set; } = "/";
        public string key { get; set; } = "hello, world";

        public bool connect_on_start { get; set; } = false;
        public int pool_min_free { get; set; } = 1;
        public int pool_concurrency { get; set; } = 32;
        public int pool_max { get; set; } = 5;

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

        public string encryption { get; set; } = DefaultEncryption;

        int _multiplied_delay = 0;
        int _using_delay => Math.Max(Math.Max(_multiplied_delay, connect_delay), 0);

        public AdapterRef @out { get; set; }

        public string network { get; set; } // name1[,name2...][@networkA][ nameB1,nameB2[@networkB]...]

        public bool fastopen { get; set; } = true;

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

        static NaiveMOutAdapter()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                DefaultEncryption = NaiveProtocol.EncryptionAesOfb128;
            } else {
                DefaultEncryption = NaiveProtocol.EncryptionSpeck0;
            }
            Logging.info($"NaiveOutAdapter default encryption chosen: '{DefaultEncryption}'");
        }

        public bool Reloading(object oldInstance)
        {
            var old = oldInstance as NaiveMOutAdapter;
            if (old.server == this.server
                && old.path == this.path
                && old.key == this.key) {
                ncsPool = old.ncsPool;
                Logging.info($"{this} reload with {ncsPool.Count} old connections.");
            }
            return false;
        }

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            if (toml.TryGetValue("imux", out var imux)) {
                if (imux is TomlArray ta) {
                    var imuxarr = ta.Get<int[]>();
                    imux_ws = imuxarr[0];
                    imux_http = imuxarr[1];
                    imux_wsso = imuxarr[2];
                } else if (imux is TomlValue tv) {
                    imux_ws = toml.Get<int>();
                } else {
                    throw new Exception("unexpected 'imux' value type: " + imux.GetType());
                }
            }
        }

        static readonly string[] UAs = new[] {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:57.0) Gecko/20100101 Firefox/57.0",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/61.0.3163.100 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/62.0.3165.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/63.0.3239.84 Safari/537.36"
        };

        protected override void Init()
        {
            base.Init();
            if (@out == null && network != null) {
                @out = Controller.AdapterRefFromName("direct");
            }
            if (headers == null)
                headers = new Dictionary<string, string>();
            if (headers.ContainsKey("User-Agent") == false) {
                headers["User-Agent"] = UAs[NaiveUtils.Random.Next(UAs.Length)];
            }
        }

        public override void Start()
        {
            base.Start();
            if (connect_on_start || network != null) {
                CheckPoolWithDelay();
            }
        }

        private void CheckPoolWithDelay()
        {
            var d = _using_delay;
            NaiveUtils.RunAsyncTask(async () => {
                await Task.Delay(d * 1000);
                if (!IsRunning)
                    return;
                CheckPool();
            });
        }

        private void CheckPool()
        {
            lock (ncsPool) {
                List<PoolItem> toBeRemoved = null;
                if (ncsPool.Count >= pool_max)
                    return;
                int avaliable = 0;
                foreach (var item in ncsPool) {
                    if (item.connectTask?.IsCompleted == true && item.nms == null) {
                        Logging.warning($"'{Name}' found a strange pool item, removing. (mono bug?)");
                        (toBeRemoved ?? (toBeRemoved = new List<PoolItem>())).Add(item);
                    } else if (item.ConnectionsCount + (item?.nms?.BaseChannels.TotalRemoteChannels ?? 0) < pool_concurrency) {
                        avaliable++;
                    }
                }
                if (toBeRemoved != null)
                    foreach (var item in toBeRemoved) {
                        ncsPool.Remove(item);
                    }
                int countToCreate = pool_min_free - avaliable;
                for (int i = 0; i < countToCreate; i++) {
                    NewPoolItem();
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
                    if (result != null)
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

        public async Task<IPAddress[]> ResolveName(string name)
        {
            var ncs = GetPoolItem();
            await ncs.ConnectIfNot();
            return await ncs.nms.DnsQuery(name);
        }

        internal class PoolItem
        {
            private NaiveMOutAdapter adapter;

            private object _lock => this;

            public NaiveMChannels nms;
            public Task connectTask;

            public int ConnectionsCount => nms?.BaseChannels?.TotalLocalChannels ?? 0;
            public bool IsConnected => nms != null;

            public event Action<PoolItem> Connected;
            public event Action<PoolItem, Exception> Error;
            public event Action<PoolItem> Closed;

            public PoolItem(NaiveMOutAdapter adapter)
            {
                this.adapter = adapter;
            }


            private async Task Connect()
            {
                try {
                    var beginTime = DateTime.Now;
                    Logging.info($"'{adapter.Name}' connecting...");
                    var settings = new NaiveMChannels.ConnectingSettings {
                        Headers = new Dictionary<string, string>(adapter.headers),
                        Host = adapter.server,
                        KeyString = adapter.key,
                        Encryption = adapter.encryption,
                        Path = adapter.path,
                        ImuxConnections = adapter.imux_ws,
                        ImuxHttpConnections = adapter.imux_http,
                        ImuxWsSendOnlyConnections = adapter.imux_wsso,
                        ImuxConnectionsDelay = adapter.imux_delay,
                        Timeout = adapter.timeout
                    };
                    var nms = await NaiveMChannels.ConnectTo(settings);
                    nms.OutAdapter = adapter;
                    nms.InAdapter = adapter;
                    nms.Logged += (log) => Logging.info($"'{adapter.Name}': {log}");
                    nms.HandleRemoteInConnection = (inc) => {
                        Logging.info($"'{adapter.Name}': conn from remote {(inc as NaiveMChannels.InConnection)?.Channel}" +
                                        $" (dest={inc.Dest}) redirecting to 127.1");
                        adapter.CheckPool();
                        inc.Dest.Host = "127.0.0.1";
                        return adapter.Controller.HandleInConnection(inc);
                    };
                    nms.InConnectionFastCallback = adapter.fastopen;
                    Logging.info($"'{adapter.Name}' connected: {nms.BaseChannels} in {(DateTime.Now - beginTime).TotalMilliseconds:0} ms");
                    if (adapter.network != null)
                        NaiveUtils.RunAsyncTask(async () => {
                            try {
                                await nms.JoinNetworks(adapter.network.Split(' '));
                                Logging.info($"{nms.BaseChannels} joined network(s).");
                            } catch (Exception e) {
                                Logging.error($"{nms.BaseChannels}: joining network(s) error: {e.Message}");
                            }
                        }).Forget();
                    this.nms = nms;
                    Connected?.Invoke(this);
                    NaiveUtils.RunAsyncTask(async () => {
                        try {
                            await nms.Start();
                        } catch (Exception e) {
                            Logging.exception(e, Logging.Level.Error, $"{nms.BaseChannels} error");
                            Error?.Invoke(this, e);
                        } finally {
                            Closed?.Invoke(this);
                            //connectTask = null;
                            nms = null;
                        }
                    }).Forget();
                } catch (Exception e) {
                    Error?.Invoke(this, e);
                    Closed?.Invoke(this);
                    //connectTask = null;
                    nms = null;
                    if (e.IsConnectionException()) {
                        Logging.error($"'{adapter.Name}' connecting failed: {e.Message}");
                    } else {
                        Logging.exception(e, Logging.Level.Error, $"'{adapter.Name}' connecting failed");
                    }
                    throw;
                }
            }

            public async Task ConnectIfNot()
            {
                if (nms == null) {
                    lock (_lock) {
                        if (connectTask == null) {
                            connectTask = Connect();
                        }
                    }
                    await connectTask;
                }
            }

            public async Task<ConnectResult> Connect(ConnectArgument arg)
            {
                await ConnectIfNot();
                return await nms.Connect(arg);
            }
        }
    }
}
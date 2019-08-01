using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

using Mono.Unix.Native;
using Naive.HttpSvr;
using System.Net;
using NaiveSocks;
using System.Net.Sockets;
using LiteDB;
using System.Diagnostics;

namespace NaiveSocksAndroid
{
    class VpnConfig
    {
        public bool Enabled { get; set; } = true;
        public bool EnableAppFilter { get; set; }
        public string AppList { get; set; }
        public bool ByPass { get; set; }

        public string Handler { get; set; }
        public int SocksPort { get; set; } = 5334;

        public string Socks { get; set; }

        public string DnsResolver { get; set; }
        public int LocalDnsPort { get; set; } = 5333;
        public int DnsTtl { get; set; } = 30;
        public int DnsCacheTtl { get; set; } = 120;
        public bool DnsDebug { get; set; } = false;
        public bool DnsDomainDb { get; set; } = true;

        public string DnsGw { get; set; }

        public bool UdpRelay { get; set; } = true;

        public int Mtu { get; set; } = 1500;

        public bool Ipv6 { get; set; } = false;

        public string[] RemoteDns { get; set; } = new[] { "8.8.8.8" };
    }

    partial class VpnHelper
    {
        public VpnHelper(BgService service)
        {
            Bg = service;
            Native.Init(service);
        }

        const string ClientIp = "172.31.1.2";
        const string RouterIp = "172.31.1.1";

        const string ClientIp6 = "fd11:4514:1919::2";
        const string RouterIp6 = "fd11:4514:1919::1";

        public BgService Bg { get; }

        public bool Running => _running != 0;
        private int _running = 0; // in order to use Interlocked.Exchange

        public VpnConfig VpnConfig { get; set; }

        public DnsDb DnsDb => dnsInAdapter?.cacheDns as DnsDb;

        ParcelFileDescriptor _pfd;

        NaiveSocks.IAdapter dnsResolver;
        SocksInAdapter socksInAdapter;
        DnsInAdapter dnsInAdapter;

        public void StartVpn()
        {
            if ((VpnConfig.Handler == null) == (VpnConfig.Socks == null)) {
                throw new Exception("Should specify (('Handler' and optional 'SocksPort') or 'Socks') and optional 'DnsResolver'");
            }

            InitAdapters();

            if (_pfd != null) return;

            var builder = new VpnService.Builder(Bg)
                .SetSession("NaiveSocks VPN bridge")
                .SetMtu(VpnConfig.Mtu)
                .AddAddress(ClientIp, 24)
                .AddDnsServer(RouterIp)
                .AddRoute("0.0.0.0", 0);
            if (VpnConfig.Ipv6) {
                builder.AddAddress(ClientIp6, 126);
                builder.AddRoute("::", 0);
            }
            foreach (var item in VpnConfig.RemoteDns) {
                builder.AddDnsServer(item);
            }
            var me = Bg.PackageName;
            bool isAnyAllowed = false;
            if (VpnConfig.EnableAppFilter && !string.IsNullOrEmpty(VpnConfig.AppList)) {
                Logging.info("applying AppList...");
                foreach (var item in from x in VpnConfig.AppList.Split('\n')
                                     let y = x.Trim()
                                     where y.Length > 0 && y != me
                                     select y) {
                    try {
                        if (VpnConfig.ByPass) {
                            builder.AddDisallowedApplication(item);
                        } else {
                            builder.AddAllowedApplication(item);
                            isAnyAllowed = true;
                        }
                    } catch (Exception e) {
                        Logging.error($"adding package '{item}': {e.Message}");
                    }
                }
            }
            if (!isAnyAllowed)
                builder.AddDisallowedApplication(me);

            _pfd = builder.Establish();
            _running = 1;
            Logging.info("VPN established, fd=" + _pfd.Fd);

            string dnsgw = null;
            if (VpnConfig.DnsGw.IsNullOrEmpty() == false) {
                dnsgw = VpnConfig.DnsGw;
            } else if (VpnConfig.LocalDnsPort > 0) {
                dnsgw = "127.0.0.1:" + VpnConfig.LocalDnsPort;
            }
            StartTun2Socks("127.0.0.1:" + socksInAdapter.listen.Port, dnsgw);
        }

        private void InitAdapters()
        {
            dnsResolver = null;
            socksInAdapter = null;

            var controller = Bg.Controller;
            if (VpnConfig.Handler == null) {
                socksInAdapter = controller.FindAdapter<SocksInAdapter>(VpnConfig.Socks) ?? throw new Exception($"SocksInAdapter '{VpnConfig.Socks}' not found.");
            } else {
                var existVpn = controller.FindAdapter<NaiveSocks.Adapter>("VPN");
                if (existVpn != null) {
                    throw new Exception("adapter 'VPN' already exists.");
                } else {
                    var handlerRef = controller.AdapterRefFromName(VpnConfig.Handler);
                    if (handlerRef.Adapter == null) {
                        throw new Exception("Handler not found.");
                    }
                    socksInAdapter = new SocksInAdapter() {
                        Name = "VPN",
                        listen = NaiveUtils.ParseIPEndPoint("127.1:" + VpnConfig.SocksPort),
                        @out = handlerRef
                    };
                    AddInAdapter(controller, socksInAdapter);
                }
            }
            if (VpnConfig.DnsResolver != null) {
                dnsResolver = controller.FindAdapter<NaiveSocks.Adapter>(VpnConfig.DnsResolver) as NaiveSocks.IAdapter;
                if (dnsResolver == null) {
                    Logging.warning($"Specified DNS resolver '{VpnConfig.DnsResolver}' is not found!");
                }
            } else {
                dnsResolver = controller.FindAdapter<NaiveSocks.IAdapter>(VpnConfig.Handler);
            }
            if (VpnConfig.LocalDnsPort > 0 && dnsResolver == null) {
                throw new Exception("local dns is enabled but cannot find a dns resolver. Check Handler or DnsResolver in configuration.");
            }
            Logging.info("VPN connections handler: " + socksInAdapter.QuotedName);

            if (VpnConfig.LocalDnsPort > 0) {
                InitLocalDns(controller);
            }

            if (VpnConfig.UdpRelay) {
                var relay = new UdpRelay() {
                    Name = "VPNUDP",
                    listen = new IPEndPoint(IPAddress.Loopback, VpnConfig.SocksPort),
                    redirect_dns = new IPEndPoint(IPAddress.Loopback, VpnConfig.LocalDnsPort)
                };
                AddInAdapter(controller, relay);
            }
        }

        private void InitLocalDns(Controller controller)
        {
            dnsInAdapter = new DnsInAdapter() {
                Name = "VPNDNS",
                @out = AdapterRef.FromAdapter(dnsResolver),
                listen = new IPEndPoint(IPAddress.Loopback, VpnConfig.LocalDnsPort),
                ttl = VpnConfig.DnsTtl,
                cache_ttl = VpnConfig.DnsCacheTtl,
                verbose = VpnConfig.DnsDebug
            };
            if (VpnConfig.DnsDomainDb) {
                dnsInAdapter.cache = "db";
                dnsInAdapter.cache_path = App.DnsDbFile;
            } else {
                dnsInAdapter.cache = "ram";
            }
            socksInAdapter.rdns = AdapterRef.FromAdapter(dnsInAdapter);
            Logging.info("Starting local DNS server at 127.0.0.1:" + VpnConfig.LocalDnsPort);
            string strResolver = dnsResolver?.GetAdapter().QuotedName;
            Logging.info("DNS resolver: " + strResolver);
            AddInAdapter(controller, dnsInAdapter);
        }

        private static void AddInAdapter(Controller controller, InAdapter adapter)
        {
            Logging.info("Automatically created adapter " + adapter);
            adapter.SetConfig(Nett.Toml.Create());
            controller.AddInAdapter(adapter, true);
            adapter.Start();
        }

        private void StartTun2Socks(string socksAddr, string dnsgw)
        {
            string sockPath = "t2s_sock_path";
            var arg = "--netif-ipaddr " + RouterIp
                         + " --socks-server-addr " + socksAddr
                         + " --tunmtu " + VpnConfig.Mtu
                         + " --sock-path " + sockPath
                         + " --loglevel warning"
                         + " --enable-udprelay";
            if (dnsgw != null) {
                arg += " --dnsgw " + dnsgw;
            }
            if (VpnConfig.Ipv6) {
                arg += " --netif-ip6addr " + RouterIp6;
            }
            var filesDir = AppConfig.FilesDir;
            var startTime = DateTime.UtcNow;
            StartProcess(Native.GetLibFullPath(Native.SsTun2Socks), arg, filesDir, (proc) => {
                if (Running && proc.ExitCode != 0) {
                    if (DateTime.UtcNow - startTime < TimeSpan.FromSeconds(5)) {
                        Logging.warning("tun2socks crashed in 5 seconds, no restarting.");
                        Stop();
                    }
                    Logging.warning("sleep 1s and restart tun2socks");
                    AsyncHelper.SetTimeout(1000, () => {
                        try {
                            StartTun2Socks(socksAddr, dnsgw);
                        } catch (Exception e) {
                            Logging.exception(e, Logging.Level.Error, "restarting tun2socks");
                        }
                    });
                } else {
                    Stop();
                }
            });
            int delay = 100;
            while (true) {
                if (Native.SendFd(Path.Combine(filesDir, sockPath), _pfd.FileDescriptor)) {
                    Logging.info($"sendfd OK.");
                    break;
                }
                if (delay > 2000) {
                    throw new Exception("failed to sendfd");
                }
                Logging.info($"Wait for {delay} ms and retry sendfd.");
                Thread.Sleep(delay);
                delay *= 3;
            }
        }


        List<System.Diagnostics.Process> startedProcesses = new List<System.Diagnostics.Process>();

        void StartProcess(string file, string arg, string dir, Action<System.Diagnostics.Process> onExit)
        {
            Logging.info("Starting process: " + file + "\nArgs: " + arg);
            var psi = new System.Diagnostics.ProcessStartInfo(file, arg);
            if (dir != null)
                psi.WorkingDirectory = dir;
            var proc = System.Diagnostics.Process.Start(psi);
            startedProcesses.Add(proc);
            Logging.info($"Process started (pid={proc.Id}): " + file);
            Task.Run(() => {
                proc.WaitForExit();
                Logging.warning("Process pid=" + proc.Id + " exited with code " + proc.ExitCode);
                onExit?.Invoke(proc);
            });
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _running, 0) != 0) {
                KillProcesses();
                try {
                    _pfd.Close();
                    _pfd = null;
                } catch (Exception) {
                }
            }
        }

        private void KillProcesses()
        {
            lock (startedProcesses) {
                foreach (var item in startedProcesses) {
                    try {
                        item.Kill();
                    } catch (Exception) {
                        ;
                    }
                }
                startedProcesses.Clear();
            }
        }

        static class Native
        {
            // native binaries from shadowsocks-android release apk (too lazy to compile):
            // https://github.com/shadowsocks/shadowsocks-android/releases
            // (v4.8.3)

            // tun -> socks. (executable)
            public const string SsTun2Socks = "libtun2socks.so";

            public static void Init(Context ctx)
            {
                NativeDir = ctx.ApplicationInfo.NativeLibraryDir;
            }

            public static string GetLibFullPath(string libName)
            {
                if (NativeDir == null)
                    throw new Exception("'Native' class havn't been initialized!");
                return Path.Combine(NativeDir, libName);
            }

            public static string NativeDir;

            public static bool SendFd(string sockPath, Java.IO.FileDescriptor fd)
            {
                var socket = new LocalSocket();
                try {
                    socket.Connect(new LocalSocketAddress(sockPath, LocalSocketAddress.Namespace.Filesystem));
                    socket.SetFileDescriptorsForSend(new Java.IO.FileDescriptor[] { fd });
                    socket.OutputStream.Write(new byte[] { 42 });
                } catch (Exception e) {
                    Logging.warning("sendfd: " + e.Message);
                    return false;
                } finally {
                    socket.Close();
                }
                return true;
            }
        }
    }
}

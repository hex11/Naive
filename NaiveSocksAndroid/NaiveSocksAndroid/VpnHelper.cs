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
        public string FakeDnsPrefix { get; set; } = "1.";
        public int DnsListenerCount { get; set; } = 1;
        public int DnsTtl { get; set; } = 30;
        public int DnsCacheTtl { get; set; } = 120;
        public bool DnsDebug { get; set; } = false;
        public bool DnsDomainDb { get; set; } = true;

        public string DnsGw { get; set; }

        public int Mtu { get; set; } = 1500;

        public string[] RemoteDns { get; set; } = new[] { "8.8.8.8" };
    }

    partial class VpnHelper
    {
        public VpnHelper(BgService service)
        {
            Bg = service;
            localDns = new LocalDns(this);
            Native.Init(service);
        }

        public BgService Bg { get; }
        public bool Running { get; private set; }
        public VpnConfig VpnConfig { get; set; }

        public DnsDb DnsDb => localDns.DnsDb;

        ParcelFileDescriptor pfd;

        IDnsProvider dnsResolver;
        SocksInAdapter socksInAdapter;
        LocalDns localDns;

        public void StartVpn()
        {
            if ((VpnConfig.Handler == null) == (VpnConfig.Socks == null)) {
                throw new Exception("Should specify (('Handler' and optional 'SocksPort') or 'Socks') and optional 'DnsResolver'");
            }

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
                    Logging.info("Automatically created adapter " + socksInAdapter);
                    socksInAdapter.SetConfig(Nett.Toml.Create());
                    controller.AddInAdapter(socksInAdapter, true);
                    socksInAdapter.Start();
                }
            }
            if (VpnConfig.DnsResolver != null) {
                dnsResolver = controller.FindAdapter<NaiveSocks.Adapter>(VpnConfig.DnsResolver) as IDnsProvider;
                if (dnsResolver == null) {
                    Logging.warning($"'{VpnConfig.DnsResolver}' is not a DNS resolver!");
                }
            } else {
                dnsResolver = controller.FindAdapter<IDnsProvider>(VpnConfig.Handler);
            }
            if (VpnConfig.LocalDnsPort > 0 && dnsResolver == null) {
                throw new Exception("local dns is enabled but cannot find a dns resolver. Check Handler or DnsResolver in configuration.");
            }
            socksInAdapter.ConnectionFilter += localDns.SocksConnectionFilter;
            Logging.info("VPN connections handler: " + socksInAdapter.QuotedName);

            var builder = new VpnService.Builder(Bg)
                .SetSession("NaiveSocks VPN bridge")
                .SetMtu(VpnConfig.Mtu)
                .AddAddress("172.31.1.1", 24);
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
            builder.AddRoute("0.0.0.0", 0);
            pfd = builder.Establish();
            var fd = pfd.Fd;
            Running = true;
            Logging.info("VPN established, fd=" + fd);

            if (VpnConfig.LocalDnsPort > 0) {
                Logging.info("Starting local DNS server at 127.0.0.1:" + VpnConfig.LocalDnsPort);
                string strResolver = dnsResolver?.GetAdapter().QuotedName;
                Logging.info("DNS resolver: " + strResolver);
                localDns.StartDnsServer();
            }

            string dnsgw = null;
            if (VpnConfig.DnsGw.IsNullOrEmpty() == false) {
                dnsgw = VpnConfig.DnsGw;
            } else if (VpnConfig.LocalDnsPort > 0) {
                dnsgw = "127.0.0.1:" + VpnConfig.LocalDnsPort;
            }
            StartTun2Socks(fd, "127.0.0.1:" + socksInAdapter.listen.Port, VpnConfig.Mtu, dnsgw);
        }

        private void StartTun2Socks(int fd, string socksAddr, int mtu, string dnsgw)
        {
            string sockPath = "t2s_sock_path";
            var arg = "--netif-ipaddr 172.31.1.2"
                         + " --netif-netmask 255.255.255.0"
                         + " --socks-server-addr " + socksAddr
                         + " --tunfd " + fd
                         + " --tunmtu " + mtu
                         + " --sock-path " + sockPath
                         + " --loglevel 3"
                         + " --enable-udprelay";
            if (dnsgw != null) {
                arg += " --dnsgw " + dnsgw;
            }
            var filesDir = AppConfig.FilesDir;
            StartProcess(Native.GetLibFullPath(Native.SsTun2Socks), arg, filesDir);
            int delay = 100;
            while (true) {
                if (Native.SendFd(Path.Combine(filesDir, sockPath), fd)) {
                    Logging.info($"sendfd OK.");
                    break;
                }
                Logging.info($"Failed to sendfd. Waiting for {delay} ms and retry.");
                Thread.Sleep(delay);
                delay *= 3;
                if (delay > 3000) {
                    Logging.error("Gived up to sendfd");
                    return;
                }
            }
        }


        List<System.Diagnostics.Process> startedProcesses = new List<System.Diagnostics.Process>();

        void StartProcess(string file, string arg, string dir = null)
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
                Logging.info("Process pid=" + proc.Id + " exited with " + proc.ExitCode);
            });
        }

        public void Stop()
        {
            localDns.StopDnsServer();
            socksInAdapter.ConnectionFilter -= localDns.SocksConnectionFilter;
            KillProcesses();
            if (Running) {
                Running = false;
                pfd.Close();
            }
        }

        private void KillProcesses()
        {
            foreach (var item in startedProcesses) {
                try {
                    item.Kill();
                } catch (Exception) {
                    ;
                }
            }
            startedProcesses.Clear();
        }

        static class Native
        {
            // native binaries from shadowsocks-android release apk (too lazy to compile):
            // https://github.com/shadowsocks/shadowsocks-android/releases
            // (v4.7.0)

            // to send TUN file descriptor to tun2socks.
            public const string SsJniHelper = "libjni-helper.so";

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

            [DllImport(SsJniHelper)]
            public static extern int ancil_send_fd(int sock, int fd);

            public static string NativeDir;

            public static bool SendFd(string sockPath, int tunFd)
            {
                var fd = Syscall.socket(UnixAddressFamily.AF_UNIX, UnixSocketType.SOCK_STREAM, 0);
                if (fd < 0) {
                    Logging.warning("Sendfd: socket() failed.");
                    return false;
                }
                try {
                    if (Syscall.connect(fd, new SockaddrUn(sockPath)) == -1) {
                        Logging.warning("Sendfd: connect() failed.");
                        return false;
                    }
                    if (ancil_send_fd(fd, tunFd) != 0) {
                        Logging.warning("Sendfd: ancil_send_fd() failed.");
                        return false;
                    }
                } finally {
                    Syscall.close(fd);
                }
                return true;
            }
        }
    }
}

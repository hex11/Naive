﻿using System;
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
using ARSoft.Tools.Net.Dns;
using System.Net;

namespace NaiveSocksAndroid
{
    class VpnConfig
    {
        public bool Enabled { get; set; } = true;
        public bool EnableAppFilter { get; set; }
        public string AppList { get; set; }
        public bool ByPass { get; set; }
        public string Socks { get; set; } = "vpnin";
        public int FakeDnsPort { get; set; } = 5333;
        public string FakeDnsIpPrefix { get; set; } = "1.";

        public string DnsGw { get; set; }

        public int Mtu { get; set; } = 1500;

        public string[] RemoteDns { get; set; } = new[] { "8.8.8.8" };
    }

    class BgServiceVpnHelper
    {
        public BgServiceVpnHelper(BgService service, VpnConfig config)
        {
            Bg = service;
            vpnConfig = config;
            Native.Init(service);
        }

        public BgService Bg { get; }
        public bool Running { get; private set; }
        VpnConfig vpnConfig;

        ParcelFileDescriptor pfd;

        Dictionary<long, string> mapIpHost = new Dictionary<long, string>();
        ReaderWriterLockSlim mapLock = new ReaderWriterLockSlim();

        Dictionary<string, long> mapHostIp = new Dictionary<string, long>();

        public void StartVpn()
        {
            var socksInAdapter = Bg.Controller.FindAdapter<NaiveSocks.SocksInAdapter>(vpnConfig.Socks)
                ?? throw new Exception($"SocksInAdapter '{vpnConfig.Socks}' not found.");
            socksInAdapter.AddrMap = (x) => {
                if (IPAddress.TryParse(x.Host, out var ip)) {
                    if (ip.ToString().StartsWith(ipPrefix)) {
                        mapLock.EnterReadLock();
                        try {
                            if (mapIpHost.TryGetValue(ip.Address, out var host)) {
                                x.Host = host;
                            } else {
                                Logging.warning("Fake DNS not found: " + ip);
                            }
                        } finally {
                            mapLock.ExitReadLock();
                        }
                    }
                }
                return x;
            };

            var builder = new VpnService.Builder(Bg)
                .SetSession("NaiveSocks")
                .SetMtu(vpnConfig.Mtu)
                .AddAddress("172.31.1.1", 24);
            foreach (var item in vpnConfig.RemoteDns) {
                builder.AddDnsServer(item);
            }
            var me = Bg.PackageName;
            bool isAnyAllowed = false;
            if (vpnConfig.EnableAppFilter) {
                if (!string.IsNullOrEmpty(vpnConfig.AppList))
                    foreach (var item in from x in vpnConfig.AppList.Split('\n')
                                         let y = x.Trim()
                                         where y.Length > 0 && y != me
                                         select y) {
                        try {
                            if (vpnConfig.ByPass) {
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
            Logging.info("VPN established, fd=" + pfd.Fd);

            if (vpnConfig.FakeDnsPort > 0) {
                Logging.info("Starting fake DNS server at 127.0.0.1:" + vpnConfig.FakeDnsPort);
                StartDnsServer();
            }

            string sockPath = "t2s_sock_path";
            var t2s = Native.GetLibFullPath(Native.SsTun2Socks);
            var arg = "--netif-ipaddr 172.31.1.2"
                         + " --netif-netmask 255.255.255.0"
                         + " --socks-server-addr " + "127.0.0.1:" + socksInAdapter.listen.Port
                         + " --tunfd " + fd
                         + " --tunmtu " + vpnConfig.Mtu
                         + " --sock-path " + sockPath
                         + " --loglevel 3"
                         + " --enable-udprelay";
            if (vpnConfig.DnsGw.IsNullOrEmpty() == false) {
                arg += " --dnsgw " + vpnConfig.DnsGw;
            } else if (vpnConfig.FakeDnsPort > 0) {
                arg += " --dnsgw 127.0.0.1:" + vpnConfig.FakeDnsPort;
            }
            var filesDir = AppConfig.FilesDir;
            StartProcess(t2s, arg, filesDir);
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

        DnsServer dnsServer;
        string ipPrefix;
        int lastIp;

        private void StartDnsServer()
        {
            ipPrefix = vpnConfig.FakeDnsIpPrefix;
            dnsServer = new DnsServer(new IPEndPoint(IPAddress.Any, vpnConfig.FakeDnsPort), 1, 1);
            dnsServer.QueryReceived += DnsServer_QueryReceived;
            dnsServer.Start();
        }

        private Task DnsServer_QueryReceived(object sender, QueryReceivedEventArgs eventArgs)
        {
            try {
                HandleQuery(eventArgs);
            } catch (Exception e) {
                Logging.exception(e, Logging.Level.Error, "Fake DNS handling query");
            }
            return AsyncHelper.CompletedTask;
        }

        private void HandleQuery(QueryReceivedEventArgs eventArgs)
        {
            var q = eventArgs.Query as DnsMessage;
            var r = q.CreateResponseInstance();
            eventArgs.Response = r;
            r.ReturnCode = ReturnCode.ServerFailure;
            foreach (var item in q.Questions) {
                if (item.RecordType == RecordType.A) {
                    var strName = item.Name.ToString();
                    IPAddress ip;
                    mapLock.EnterWriteLock();
                    try {
                        if (mapHostIp.TryGetValue(strName, out var ipLong)) {
                            ip = new IPAddress(ipLong);
                        } else {
                            lastIp++;
                            ip = IPAddress.Parse(ipPrefix + lastIp);
                            ipLong = ip.Address;
                            mapIpHost.Add(ipLong, strName);
                            mapHostIp.Add(strName, ipLong);
                            Logging.info("Fake DNS: " + ip.ToString() + " -> " + strName);
                        }
                    } finally {
                        mapLock.ExitWriteLock();
                    }
                    r.AnswerRecords.Add(new ARecord(item.Name, 120, ip));
                    r.ReturnCode = ReturnCode.NoError;
                } else {
                    Logging.warning("Notsupported DNS record: " + item);
                }
            }
        }

        private void StopDnsServer()
        {
            if (dnsServer == null)
                return;
            dnsServer.Stop();
            dnsServer = null;
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
            if (Running) {
                Running = false;
                StopDnsServer();
                KillProcesses();
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
            // native binaries from shadowsocks-android release apk:
            // https://github.com/shadowsocks/shadowsocks-android/releases
            // (v4.6.1)

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
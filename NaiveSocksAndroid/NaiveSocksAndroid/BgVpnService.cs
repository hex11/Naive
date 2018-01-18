using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

using Mono.Unix.Native;
using Naive.HttpSvr;

namespace NaiveSocksAndroid
{
    class VpnConfig
    {
        public bool EnableAppFilter { get; set; }
        public string AppList { get; set; }
        public bool ByPass { get; set; }

        public string[] RemoteDns { get; set; } = new[] { "8.8.8.8" };
    }

    class BgVpnService : VpnService
    {
        VpnConfig vpnConfig;

        [return: GeneratedEnum]
        public override StartCommandResult OnStartCommand(Intent intent, [GeneratedEnum] StartCommandFlags flags, int startId)
        {
            // TODO: start vpn
            return StartCommandResult.Sticky;
        }

        void StartVpn()
        {
            var builder = new Builder(this)
                .SetSession("NaiveSocks")
                .SetMtu(1500)
                .AddAddress("26.26.26.1", 24);
            foreach (var item in vpnConfig.RemoteDns) {
                builder.AddDnsServer(item);
            }
            if (vpnConfig.EnableAppFilter) {
                var me = PackageName;
                if (!string.IsNullOrEmpty(vpnConfig.AppList))
                    foreach (var item in from x in vpnConfig.AppList.Split('\n')
                                         let y = x.Trim()
                                         where y.Length > 0 && y != me
                                         select y) {
                        try {
                            if (vpnConfig.ByPass)
                                builder.AddDisallowedApplication(item);
                            else
                                builder.AddAllowedApplication(item);
                        } catch (Exception e) {
                            Logging.error($"adding package '{item}': {e.Message}");
                        }
                    }
                if (!vpnConfig.ByPass)
                    builder.AddAllowedApplication(me);
            }
            builder.AddRoute("0.0.0.0", 0);
            builder.Establish();
        }
    }

    static class Native
    {
        public const string SsJniHelper = "libjni-helper.so";
        public const string SsOverture = "liboverture.so";
        public const string SsTun2Socks = "libtun2socks.so";

        public static void Init(Context ctx)
        {
            NativeDir = ctx.ApplicationInfo.NativeLibraryDir;
        }

        [DllImport(SsJniHelper)]
        public static extern int ancil_send_fd(int sock, int fd);

        public static string NativeDir;
    }
}
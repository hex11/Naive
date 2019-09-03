using Microsoft.Win32;
using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NaiveSocks.WinForm
{
    class SystemProxy
    {
        [DllImport("wininet.dll")]
        public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
        public const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        public const int INTERNET_OPTION_REFRESH = 37;

        public static AddrPort Get()
        {
            try {
                using (var reg = OpenProxyReg()) {
                    if (reg.GetValue("ProxyEnable") is int enabled && enabled != 0) {
                        var str = reg.GetValue("ProxyServer") as string;
                        if (str?.Length == 0) str = null;
                        return AddrPort.Parse(str);
                    }
                }
            } catch (Exception e) {
                Logging.exception(e, Logging.Level.Warning, "Error getting proxy setting");
            }
            return AddrPort.Empty;
        }

        public static void Set(string proxy)
        {
            try {
                using (var reg = OpenProxyReg()) {
                    if (proxy == null) {
                        reg.SetValue("ProxyEnable", 0);
                    } else {
                        reg.SetValue("ProxyEnable", 1);
                        reg.SetValue("ProxyServer", proxy);
                    }
                    if (!InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0))
                        throw new Win32Exception();
                    if (!InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0))
                        throw new Win32Exception();
                }
            } catch (Exception e) {
                Logging.exception(e, Logging.Level.Warning, "Error setting proxy setting");
            }
        }

        private static RegistryKey OpenProxyReg()
        {
            return Registry.CurrentUser.OpenSubKey(@"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings", true);
        }
    }
}

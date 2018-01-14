using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Support.V4.App;
using Android.Util;
using Android.Views;
using Android.Widget;
using Naive.HttpSvr;
using NaiveSocks;

namespace NaiveSocksAndroid
{
    public class GlobalConfig
    {
        public static GlobalConfig Current;

        public static void Init(Context ctx)
        {
            if (Current != null)
                return;
            Current = new GlobalConfig() {
                MainPreference = GetPreference(ctx)
            };
        }

        public ISharedPreferences MainPreference { get; private set; }

        public static ISharedPreferences CurrentMainPreference => Current.MainPreference;

        public static ISharedPreferences GetPreference(Context ctx)
        {
            return ctx.GetSharedPreferences("Config", FileCreationMode.Private);
        }

        public void Set(string key, bool value)
        {
            MainPreference.Edit()
                .PutBoolean(key, value)
                .Commit();
        }

        public bool GetBool(string key, bool def)
        {
            return MainPreference.GetBoolean(key, def);
        }

        public const string notification_show_logs = "notification_show_logs";
        public const string start_on_boot = "start_on_boot";

        public void SetShowLogs(bool value) => Set(notification_show_logs, value);
        public bool GetShowLogs() => GetBool(notification_show_logs, true);

        public void SetAutostart(bool value) => Set(start_on_boot, value);
        public bool GetAutostart() => GetBool(start_on_boot, false);

        public static string[] GetNaiveSocksConfigPaths(Context ctx)
        {
            string[] paths = {
                        ctx.GetExternalFilesDir(null).AbsolutePath,
                        Path.Combine(Android.OS.Environment.ExternalStorageDirectory.AbsolutePath, "nsocks"),
                    };
            for (int i = 0; i < paths.Length; i++) {
                paths[i] = Path.Combine(paths[i], "naivesocks.tml");
            }
            return paths;
        }
    }
}
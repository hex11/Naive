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
    public class AppConfig
    {
        public static AppConfig Current;

        public static void Init(Context ctx)
        {
            if (Current != null)
                return;
            Current = new AppConfig() {
                MainPreference = GetPreference(ctx)
            };
            FilesDir = ctx.ApplicationContext.FilesDir.AbsolutePath;
        }

        public ISharedPreferences MainPreference { get; private set; }

        public static ISharedPreferences CurrentMainPreference => Current.MainPreference;

        public static ISharedPreferences GetPreference(Context ctx)
        {
            return ctx.GetSharedPreferences("Config", FileCreationMode.Private);
        }

        public static string FilesDir { get; private set; }

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

        public const string start_on_boot = "start_on_boot";
        public const string tip_cxn = "tip_cxn";
        public const string log_dynamic_margin = "log_dynamic_margin";
        public const string service_running = "service_running";
        public const string conn_sort_by_speed = "conn_sort_by_speed";
        public const string conn_more_info = "conn_more_info";

        public bool Autostart { get => GetBool(start_on_boot, false); set => Set(start_on_boot, value); }

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
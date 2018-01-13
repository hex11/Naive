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
    public class ConfigService : Service
    {
        public ISharedPreferences MainPreference { get; private set; }

        public override void OnCreate()
        {
            base.OnCreate();
            this.MainPreference = GetPreference(ApplicationContext);
        }

        public override IBinder OnBind(Intent intent)
        {
            return new ControlServiceBinder(this);
        }

        public static ISharedPreferences GetPreference(Context ctx)
        {
            return ctx.GetSharedPreferences("Config", FileCreationMode.Private);
        }

        public static void SetShowLogs(Context ctx, bool show)
        {
            GetPreference(ctx).Edit()
                .PutBoolean("notification_show_logs", show)
                .Commit();
        }

        public static bool GetShowLogs(Context ctx)
        {
            return GetPreference(ctx).GetBoolean("notification_show_logs", true);
        }
    }

    public class ControlServiceBinder : Binder
    {
        public ControlServiceBinder(ConfigService service)
        {
            Service = service;
        }

        public ConfigService Service { get; }
    }
}
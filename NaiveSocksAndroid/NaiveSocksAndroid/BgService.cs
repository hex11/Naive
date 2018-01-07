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
using Android.Util;
using Android.Views;
using Android.Widget;
using Naive.HttpSvr;
using NaiveSocks;

namespace NaiveSocksAndroid
{
    [Service(
        //IsolatedProcess = true,
        //Process = ":bg"
        )]
    public class BgService : Service
    {
        public Controller Controller { get; private set; }

        public override void OnCreate()
        {
            base.OnCreate();

            var b = new Notification.Builder(this)
                        .SetContentIntent(BuildIntentToShowMainActivity())
                        .SetContentTitle("NaiveSocks")
                        .SetContentText("NaiveSocks is running.")
                        .AddAction(BuildServiceAction("stop!", "Stop", 0, 1))
                        .AddAction(BuildServiceAction("reload!", "Reload", 0, 2))
                        .AddAction(BuildServiceAction("gc!", "GC", 0, 2))
                        .SetSmallIcon(Resource.Drawable.N)
                        .SetPriority((int)NotificationPriority.Min)
                        .SetVisibility(NotificationVisibility.Secret)
                        .SetOngoing(true);

            StartForeground(1, b.Build());

            Task.Run(() => {
                Logging.Logged += Logging_Logged;
                Logging.info("starting controller...");
                try {
                    Controller = new Controller();
                    string[] paths = {
                        GetExternalFilesDir(null).AbsolutePath,
                        Path.Combine(Android.OS.Environment.ExternalStorageDirectory.AbsolutePath, "nsocks"),
                    };
                    for (int i = 0; i < paths.Length; i++) {
                        paths[i] = Path.Combine(paths[i], "naivesocks.tml");
                    }
                    Controller.LoadConfigFileFromMultiPaths(paths);
                    Controller.Start();
                    Logging.info("controller started.");
                } catch (System.Exception e) {
                    Logging.exception(e, Logging.Level.Error, "loading/starting controller");
                    ShowToast("starting error: " + e.Message);
                }
            });
        }

        [return: GeneratedEnum]
        public override StartCommandResult OnStartCommand(Intent intent, [GeneratedEnum] StartCommandFlags flags, int startId)
        {
            if (intent.Action == "stop!") {
                StopForeground(true);
                this.StopSelf();
            } else if (intent.Action == "reload!") {
                try {
                    Controller.Reload();
                    ShowToast("controller reloaded");
                } catch (Exception e) {
                    ShowToast("reloading error: " + e.Message);
                }
            } else if (intent.Action == "gc!") {
                ShowToast("performing GC collection...");
                GC.Collect();
            }
            return StartCommandResult.Sticky;
        }

        public override IBinder OnBind(Intent intent)
        {
            return new BgServiceBinder(this);
        }

        public override void OnDestroy()
        {
            Controller.Stop();
            base.OnDestroy();
        }

        private void Logging_Logged(Logging.Log log)
        {
            Log.WriteLine(GetPri(log), "Naive.Logging", log.text);
        }

        private static LogPriority GetPri(Logging.Log log)
        {
            switch (log.level) {
            case Logging.Level.None:
                return LogPriority.Verbose;
            case Logging.Level.Debug:
                return LogPriority.Debug;
            case Logging.Level.Info:
                return LogPriority.Info;
            case Logging.Level.Warning:
                return LogPriority.Warn;
            case Logging.Level.Error:
                return LogPriority.Error;
            default:
                return LogPriority.Info;
            }
        }

        void ShowToast(string text)
        {
            Toast.MakeText(this.ApplicationContext, text, ToastLength.Short).Show();
        }

        PendingIntent BuildIntentToShowMainActivity()
        {
            var notificationIntent = new Intent(this, typeof(MainActivity));
            notificationIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTask);

            var pendingIntent = PendingIntent.GetActivity(this, 0, notificationIntent, PendingIntentFlags.UpdateCurrent);
            return pendingIntent;
        }

        Notification.Action BuildServiceAction(string action, string text, int icon, int requestCode)
        {
            var intent = new Intent(this, GetType());
            intent.SetAction(action);
            var stopServicePendingIntent = PendingIntent.GetService(this, requestCode, intent, 0);

            var builder = new Notification.Action.Builder(icon, text, stopServicePendingIntent);
            return builder.Build();
        }
    }

    public class BgServiceBinder : Binder
    {
        public BgServiceBinder(BgService bgService)
        {
            BgService = bgService;
        }

        public BgService BgService { get; }
    }
}
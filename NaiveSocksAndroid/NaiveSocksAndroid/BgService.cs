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

        private PowerManager powerManager;
        private NotificationManager notificationManager;

        Notification.Builder builder;
        Notification.BigTextStyle bigText;
        const int MainNotificationId = 1;

        private Receiver receiver;

        bool isScreenOn;

        public override void OnCreate()
        {
            base.OnCreate();

            powerManager = (PowerManager)GetSystemService(Context.PowerService);
            notificationManager = (NotificationManager)GetSystemService(Context.NotificationService);

            bigText = new Notification.BigTextStyle();
            bigText.BigText("logs will shown here.\n(multiple lines)");

            builder = new Notification.Builder(this)
                        .SetContentIntent(BuildIntentToShowMainActivity())
                        .SetContentTitle("NaiveSocks")
                        //.SetSubText("running")
                        .SetStyle(bigText)
                        .AddAction(BuildServiceAction(Actions.STOP, "Stop", 0, 1))
                        .AddAction(BuildServiceAction(Actions.RELOAD, "Reload", 0, 2))
                        .AddAction(BuildServiceAction(Actions.GC, "GC", 0, 3))
                        .SetSmallIcon(Resource.Drawable.N)
                        .SetPriority((int)NotificationPriority.Min)
                        .SetVisibility(NotificationVisibility.Secret)
                        .SetShowWhen(false)
                        .SetOngoing(true);

            StartForeground(MainNotificationId, builder.Build());

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
                    StopSelf();
                }
            });

            isScreenOn = powerManager.IsInteractive;
            var filter = new IntentFilter(Intent.ActionScreenOff);
            filter.AddAction(Intent.ActionScreenOn);
            RegisterReceiver(receiver = new Receiver((c, i) => {
                if (i.Action == Intent.ActionScreenOn) {
                    isScreenOn = true;
                    if (_needUpdateNotif)
                        updateNotif();
                } else if (i.Action == Intent.ActionScreenOff) {
                    isScreenOn = false;
                }
            }), filter);
        }

        [return: GeneratedEnum]
        public override StartCommandResult OnStartCommand(Intent intent, [GeneratedEnum] StartCommandFlags flags, int startId)
        {
            if (intent != null) {
                if (intent.Action == Actions.STOP) {
                    StopForeground(true);
                    this.StopSelf();
                } else if (intent.Action == Actions.RELOAD) {
                    try {
                        Controller.Reload();
                        ShowToast("controller reloaded");
                    } catch (Exception e) {
                        ShowToast("reloading error: " + e.Message);
                    }
                } else if (intent.Action == Actions.GC) {
                    ShowToast("performing GC collection...");
                    GC.Collect();
                }
            }
            return StartCommandResult.Sticky;
        }

        public override IBinder OnBind(Intent intent)
        {
            return new BgServiceBinder(this);
        }

        public override void OnDestroy()
        {
            UnregisterReceiver(receiver);
            Controller.Stop();
            base.OnDestroy();
        }

        string textLast;
        string textSecondLast;

        private void Logging_Logged(Logging.Log log)
        {
            Log.WriteLine(GetPri(log), "naivelog", log.text);
            textSecondLast = textLast;
            textLast = log.text;
            needUpdateNotif();
        }


        bool _needUpdateNotif = false;

        void needUpdateNotif()
        {
            if (isScreenOn) {
                updateNotif();
            } else {
                _needUpdateNotif = true;
            }
        }

        void updateNotif()
        {
            _needUpdateNotif = false;
            builder.SetContentText(textLast);
            //bigText.SetSummaryText(lastText1);
            bigText.BigText(textSecondLast + "\n" + textLast);
            builder.SetStyle(bigText);
            notificationManager.Notify(MainNotificationId, builder.Build());
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

        public static class Actions
        {
            public const string START = "start!";
            public const string STOP = "stop!";
            public const string RELOAD = "reload!";
            public const string GC = "gc!";
        }
    }

    public class Receiver : BroadcastReceiver
    {
        private readonly Action<Context, Intent> _onReceive;

        public Receiver(Action<Context, Intent> onReceive)
        {
            _onReceive = onReceive;
        }

        public override void OnReceive(Context context, Intent intent)
        {
            _onReceive(context, intent);
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
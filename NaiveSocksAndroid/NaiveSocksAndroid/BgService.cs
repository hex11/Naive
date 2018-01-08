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

        Notification.Builder builder, restartBuilder;
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
                        .AddAction(BuildServiceAction(Actions.COL_NOTIF, "Collapse", 0, 4))
                        .AddAction(BuildServiceAction(Actions.STOP, "Stop", 0, 1))
                        //.AddAction(BuildServiceAction(Actions.RELOAD, "Reload", 0, 2))
                        .AddAction(BuildServiceAction(Actions.GC, "GC", 0, 3))
                        .SetSmallIcon(Resource.Drawable.N)
                        .SetPriority((int)NotificationPriority.Min)
                        .SetVisibility(NotificationVisibility.Secret)
                        //.SetShowWhen(false)
                        .SetOngoing(true);

            restartBuilder = new Notification.Builder(this)
                        .SetContentIntent(BuildServicePendingIntent("start!", 10086))
                        .SetContentTitle("Touch to restart NaiveSocks service")
                        .SetContentText("or just delete this notification")
                        .SetSmallIcon(Resource.Drawable.N)
                        .SetAutoCancel(true)
                        .SetPriority((int)NotificationPriority.Min)
                        .SetVisibility(NotificationVisibility.Secret)
                        .SetShowWhen(false);

            StartForeground(MainNotificationId, builder.Build());

            Logging.Logged += Logging_Logged;

            Task.Run(() => {
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

            onScreen(powerManager.IsInteractive);
            var filter = new IntentFilter(Intent.ActionScreenOff);
            filter.AddAction(Intent.ActionScreenOn);
            RegisterReceiver(receiver = new Receiver((c, i) => {
                if (i.Action == Intent.ActionScreenOn) {
                    onScreen(true);
                } else if (i.Action == Intent.ActionScreenOff) {
                    onScreen(false);
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
                    notificationManager.Notify(MainNotificationId, restartBuilder.Build());
                } else if (intent.Action == Actions.RELOAD) {
                    try {
                        Controller.Reload();
                        putLine("controller reloaded");
                    } catch (Exception e) {
                        ShowToast("reloading error: " + e.Message);
                        putLine("reloading error: " + e.ToString());
                    }
                } else if (intent.Action == Actions.GC) {
                    var before = GC.GetTotalMemory(false);
                    GC.Collect();
                    putLine($"GC Done. {before:N0} -> {GC.GetTotalMemory(false):N0}");
                } else if (intent.Action == Actions.COL_NOTIF) {
                    lock (builder) {
                        StopForeground(true);
                        StartForeground(MainNotificationId, builder.Build());
                    }
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
            isDestroyed = true;
            Logging.Logged -= Logging_Logged;
            UnregisterReceiver(receiver);
            Controller.Stop();
            base.OnDestroy();
        }

        bool isDestroyed = false;

        string[] textLines = new string[3];
        bool textLinesChanged = false;

        private void Logging_Logged(Logging.Log log)
        {
            if (isDestroyed)
                return;
            Log.WriteLine(GetPri(log), "naivelog", log.text);
            putLine(log.text);
        }

        private void clearLines(bool updateNow)
        {
            for (int i = 0; i < textLines.Length; i++) {
                textLines[i] = null;
            }
            if (updateNow)
                needUpdateNotif();
        }

        private void putLine(string line, bool updateNow = true)
        {
            for (int i = 0; i < textLines.Length - 1; i++) {
                textLines[i] = textLines[i + 1];
            }
            textLines.Set(-1, line);
            textLinesChanged = true;
            if (updateNow)
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
            if (isDestroyed)
                return;
            lock (builder) {
                _needUpdateNotif = false;
                builder.SetContentTitle($"{Controller.RunningConnections}/{Controller.TotalHandledConnections} cxn, {MyStream.TotalCopiedBytes / 1024:N0} KB, {MyStream.TotalCopiedPackets:N0} pkt - NaiveSocks");
                if (textLinesChanged) {
                    builder.SetContentText(textLines.Get(-1));
                    bigText.BigText(string.Join("\n", textLines.Where(x => !string.IsNullOrEmpty(x))));
                    builder.SetStyle(bigText);
                    textLinesChanged = false;
                }
                notificationManager.Notify(MainNotificationId, builder.Build());
            }
        }

        int id = 0;

        void onScreen(bool on)
        {
            isScreenOn = on;
            if (on) {
                var _id = ++id;
                NaiveUtils.RunAsyncTask(async () => {
                    while (true) {
                        await Task.Delay(2000);
                        if (_id != id || !isScreenOn || isDestroyed)
                            return;
                        updateNotif();
                    }
                });
                if (_needUpdateNotif)
                    updateNotif();
            }
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
            return PendingIntent.GetActivity(this, 0, notificationIntent, PendingIntentFlags.UpdateCurrent);
        }

        Notification.Action BuildServiceAction(string action, string text, int icon, int requestCode)
        {
            var pendingIntent = BuildServicePendingIntent(action, requestCode);
            var builder = new Notification.Action.Builder(icon, text, pendingIntent);
            return builder.Build();
        }

        private PendingIntent BuildServicePendingIntent(string action, int requestCode)
        {
            var intent = new Intent(this, GetType());
            intent.SetAction(action);
            intent.SetFlags(ActivityFlags.ClearTop);
            return PendingIntent.GetService(this, requestCode, intent, 0);
        }

        public static class Actions
        {
            public const string START = "start!";
            public const string STOP = "stop!";
            public const string RELOAD = "reload!";
            public const string GC = "gc!";
            public const string COL_NOTIF = "colnotif!";
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
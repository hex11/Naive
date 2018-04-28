using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Support.V4.App;
using Android.Support.V4.Content;
using Android.Util;
using Android.Views;
using Android.Widget;
using Naive.HttpSvr;
using NaiveSocks;

namespace NaiveSocksAndroid
{

    [BroadcastReceiver(Name = "naive.NaiveSocksAndroid.BootReceiver", Enabled = false)]
    [IntentFilter(new[] { Intent.ActionBootCompleted })]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            AppConfig.Init(Application.Context);
            if (AppConfig.Current.Autostart) {
                Intent serviceIntent = new Intent(context, typeof(BgService));
                serviceIntent.SetAction("start!");
                serviceIntent.PutExtra("isAutostart", true);
                Application.Context.StartService(serviceIntent);
            }
        }
    }

    [Service(
        //IsolatedProcess = true,
        //Process = ":bg"
        )]
    public class BgService : Service
    {
        static bool isN = Build.VERSION.SdkInt >= BuildVersionCodes.N;
        static bool isO = Build.VERSION.SdkInt >= BuildVersionCodes.O;

        public Controller Controller { get; private set; }

        private PowerManager powerManager;
        private NotificationManager notificationManager;

        NotificationCompat.Builder builder, restartBuilder;
        NotificationCompat.BigTextStyle bigText;
        const int MainNotificationId = 1;

        private Receiver receiver;

        bool isScreenOn;

        Config currentConfig = new Config();

        class Config
        {
            public int manage_interval_screen_off { get; set; } = 20;
            public int manage_interval_screen_on { get; set; } = 2;
            public int notif_update_interval { get; set; } = 2;

            public string title_format { get; set; } = "{0}/{1} cxn, {2:N0} KB, {3:N0} pkt, load: {4:N2}";
        }

        bool notification_show_logs = true;

        //private ServiceConnection<ConfigService> cfgService;

        public override void OnCreate()
        {
            CrashHandler.CheckInit();
            AppConfig.Init(ApplicationContext);

            base.OnCreate();

            Logging.info("service is starting...");

            powerManager = (PowerManager)GetSystemService(Context.PowerService);
            notificationManager = (NotificationManager)GetSystemService(Context.NotificationService);

            var chId = "";
            if (isO) {
                chId = "nsocks_service";
                var chan = new NotificationChannel(chId, "NaiveSocks Service", NotificationImportance.Min);
                chan.LockscreenVisibility = NotificationVisibility.Private;
                notificationManager.CreateNotificationChannel(chan);
            }

            builder = new NotificationCompat.Builder(this, chId)
                        .SetContentIntent(BuildIntentToShowMainActivity())
                        //.SetContentTitle("NaiveSocks")
                        //.SetSubText("running")
                        //.SetStyle(bigText)
                        //.AddAction(BuildServiceAction(Actions.COL_NOTIF, "Collapse", Android.Resource.Drawable.StarOff, 4))
                        .AddAction(BuildServiceAction(Actions.STOP, "Stop", Android.Resource.Drawable.StarOff, 1))
                        .AddAction(BuildServiceAction(Actions.RELOAD, "Reload", Android.Resource.Drawable.StarOff, 2))
                        .AddAction(BuildServiceAction(Actions.GC, "GC", Android.Resource.Drawable.StarOff, 3))
                        .AddAction(BuildServiceAction(Actions.GC_0, "GC(gen0)", Android.Resource.Drawable.StarOff, 5))
                        .SetSmallIcon(Resource.Drawable.N)
                        .SetColor(unchecked((int)0xFF2196F3))
                        //.SetShowWhen(false)
                        .SetOngoing(true);
            if (!isO) {
                builder.SetPriority((int)NotificationPriority.Min)
                    .SetVisibility(NotificationCompat.VisibilitySecret);
            }
            if (!isN) {
                builder.SetContentTitle("NaiveSocks");
            }

            restartBuilder = new NotificationCompat.Builder(this, chId)
                        .SetContentIntent(BuildServicePendingIntent("start!", 10086))
                        .SetContentTitle("NaiveSocks service is stopped")
                        .SetContentText("touch here to restart")
                        .SetSmallIcon(Resource.Drawable.N)
                        .SetColor(unchecked((int)0xFF2196F3))
                        .SetAutoCancel(true)
                        .SetPriority((int)NotificationPriority.Min)
                        .SetVisibility(NotificationCompat.VisibilitySecret)
                        .SetShowWhen(false);

            StartForeground(MainNotificationId, builder.Build());

            Controller = new Controller();
            Controller.Logger.ParentLogger = Logging.RootLogger;

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

            //BindService(new Intent(this, typeof(ConfigService)), cfgService = new ServiceConnection<ConfigService>(), Bind.AutoCreate);

            notification_show_logs = AppConfig.Current.ShowLogs;

            notification_show_logs ^= true; // to force update
            SetShowLogs(!notification_show_logs);

            Logging.Logged += Logging_Logged;

            Task.Run(() => {
                Logging.info("starting controller...");
                try {
                    Controller.ConfigTomlLoaded += (t) => {
                        CrashHandler.CrashLogFile = Controller.ProcessFilePath("UnhandledException.txt");
                        if (t.TryGetValue<Config>("android", out var config)) {
                            currentConfig = config;
                            onScreen(isScreenOn);
                        }
                    };
                    var paths = AppConfig.GetNaiveSocksConfigPaths(this);
                    Controller.LoadConfigFileFromMultiPaths(paths);
                    Controller.Start();
                    Logging.info("controller started.");
                } catch (System.Exception e) {
                    Logging.exception(e, Logging.Level.Error, "loading/starting controller");
                    ShowToast("starting error: " + e.Message);
                    StopSelf();
                }
            });

            updateNotif(true);
        }

        [return: GeneratedEnum]
        public override StartCommandResult OnStartCommand(Intent intent, [GeneratedEnum] StartCommandFlags flags, int startId)
        {
            if (intent != null) {
                switch (intent.Action) {
                case Actions.STOP:
                    StopForeground(true);
                    this.StopSelf();
                    notificationManager.Notify(MainNotificationId, restartBuilder.Build());
                    break;
                case Actions.RELOAD:
                    try {
                        Controller.Reload();
                        putLine("controller reloaded");
                    } catch (Exception e) {
                        ShowToast("reloading error: " + e.Message);
                        putLine("reloading error: " + e.ToString());
                    }
                    break;
                case Actions.GC:
                case Actions.GC_0:
                    var before = GC.GetTotalMemory(false);
                    GC.Collect(intent.Action == Actions.GC ? GC.MaxGeneration : 0);
                    putLine($"GC Done. {before:N0} -> {GC.GetTotalMemory(false):N0}");
                    break;
                case Actions.COL_NOTIF:
                    lock (builder) {
                        StopForeground(true);
                        StartForeground(MainNotificationId, builder.Build());
                    }
                    break;
                }
            }
            return StartCommandResult.Sticky;
        }

        public override IBinder OnBind(Intent intent)
        {
            return new Binder<BgService>(this);
        }

        public override void OnDestroy()
        {
            notifyTimer?.Dispose();
            isDestroyed = true;
            Logging.Logged -= Logging_Logged;
            Logging.warning("service is being destroyed.");
            //UnbindService(cfgService);
            UnregisterReceiver(receiver);
            var tmp = Controller;
            Controller = null;
            Task.Run(() => tmp.Stop());
            base.OnDestroy();
        }

        bool isDestroyed = false;

        string[] textLines;
        bool textLinesChanged = false;

        private void Logging_Logged(Logging.Log log)
        {
            if (isDestroyed)
                return;
            Log.WriteLine(GetPri(log), "naivelog", log.text);
            if (notification_show_logs)
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

        void needUpdateNotif()
        {
            if (isScreenOn) {
                updateNotif();
            }
        }

        string lastTitle;

        void updateNotif(bool force = false)
        {

            if (isDestroyed)
                return;
            lock (builder) {
                if (updateNotifBuilder() | force) {
                    notificationManager.Notify(MainNotificationId, builder.Build());
                }
            }
        }

        System.Diagnostics.Process currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        long lastRuntime = 0;
        long lastCpuTime = 0;

        private bool updateNotifBuilder()
        {
            void SetSingleLineText(string text)
            {
                //if (isN) {
                //    builder.SetSubText(text);
                //} else {
                builder.SetContentText(text);
                //}
            }

            var needRenotify = false;

            var curRuntime = (DateTime.Now - currentProcess.StartTime).Ticks;
            var curCpuTime = currentProcess.TotalProcessorTime.Ticks;
            var load = (float)(curCpuTime - lastCpuTime) / (curRuntime - lastRuntime);
            lastRuntime = curRuntime;
            lastCpuTime = curCpuTime;

            var title = string.Format(currentConfig.title_format ?? "", Controller.RunningConnections, Controller.TotalHandledConnections, MyStream.TotalCopiedBytes / 1024, MyStream.TotalCopiedPackets, load);
            if (title != lastTitle) {
                needRenotify = true;
                lastTitle = title;
                //if (notification_show_logs) {
                builder.SetContentTitle(title);
                //} else {
                //    SetSingleLineText(title);
                //}
            }
            if (textLinesChanged) {
                textLinesChanged = false;
                needRenotify = true;
                SetSingleLineText(textLines.Get(-1));
                if (notification_show_logs) {
                    if (bigText == null)
                        bigText = new NotificationCompat.BigTextStyle();
                    bigText.BigText(string.Join("\n", textLines.Where(x => !string.IsNullOrEmpty(x))));
                    builder.SetStyle(bigText);
                }
            }
            return needRenotify;
        }

        public void SetShowLogs(bool show)
        {
            if (show == notification_show_logs)
                return;
            AppConfig.Current.ShowLogs = show;
            lock (builder) {
                notification_show_logs = show;
                if (show) {
                    textLines = new string[3];
                } else {
                    textLines = new string[1];
                    builder.SetStyle(null);
                    builder.SetContentTitle((Java.Lang.ICharSequence)null);
                }
                updateNotif();
            }
        }

        Timer notifyTimer;

        int _tid = 0;

        void onScreen(bool on)
        {
            isScreenOn = on;
            int manageIntervalSeconds;
            if (on) {
                manageIntervalSeconds = currentConfig.manage_interval_screen_on;
                var notifInterval = currentConfig.notif_update_interval;
                if (manageIntervalSeconds == notifInterval) {
                    var tid = ++_tid;
                    WebSocket.AddManagementTask(() => {
                        if (isDestroyed)
                            return true;
                        updateNotif();
                        return tid != _tid;
                    });
                } else {
                    if (notifyTimer == null)
                        notifyTimer = new Timer(_ => {
                            updateNotif();
                        });
                    notifyTimer.Change(notifInterval * 1000, notifInterval * 1000);
                }
                updateNotif();
            } else {
                _tid++;
                notifyTimer?.Change(-1, -1);
                manageIntervalSeconds = currentConfig.manage_interval_screen_off;
            }
            WebSocket.ConfigManageTask(manageIntervalSeconds, manageIntervalSeconds * 1000);
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
            var notificationIntent = new Intent(this, typeof(MainActivity))
                                     .SetFlags(ActivityFlags.ReorderToFront);
            return PendingIntent.GetActivity(this, 0, notificationIntent, PendingIntentFlags.UpdateCurrent);
        }

        NotificationCompat.Action BuildServiceAction(string action, string text, int icon, int requestCode)
        {
            var pendingIntent = BuildServicePendingIntent(action, requestCode);
            var builder = new NotificationCompat.Action.Builder(icon, text, pendingIntent);
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
            public const string GC_0 = "gc!0";
            public const string COL_NOTIF = "colnotif!";
        }
    }

    public class ServiceConnection : Java.Lang.Object, IServiceConnection
    {
        public event Action<ComponentName, IBinder> Connected;
        public event Action<ComponentName> Disconnected;

        public bool IsConnected { get; private set; }

        public ServiceConnection()
        {
        }

        public ServiceConnection(Action<ComponentName, IBinder> connected, Action<ComponentName> disconnected)
        {
            Connected += connected;
            Disconnected += disconnected;
        }

        public virtual void OnServiceConnected(ComponentName name, IBinder service)
        {
            IsConnected = true;
            Connected?.Invoke(name, service);
        }

        public virtual void OnServiceDisconnected(ComponentName name)
        {
            IsConnected = false;
            Disconnected?.Invoke(name);
        }
    }

    public class ServiceConnection<T> : ServiceConnection where T : class
    {
        public T Value;

        public ServiceConnection()
        {
        }


        public ServiceConnection(Action<ComponentName, IBinder> connected, Action<ComponentName> disconnected) : base(connected, disconnected)
        {
        }

        public override void OnServiceConnected(ComponentName name, IBinder service)
        {
            Value = (service as Binder<T>)?.Value;
            base.OnServiceConnected(name, service);
        }

        public override void OnServiceDisconnected(ComponentName name)
        {
            Value = null;
            base.OnServiceDisconnected(name);
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

    public class Binder<T> : Binder where T : class
    {
        public Binder(T value)
        {
            Value = value;
        }

        public T Value { get; }
    }
}
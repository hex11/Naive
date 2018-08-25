﻿using System;
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
            CrashHandler.CheckInit();
            AppConfig.Init(Application.Context);
            if (AppConfig.Current.Autostart) {
                Intent serviceIntent = new Intent(context, typeof(BgService));
                serviceIntent.SetAction(BgService.Actions.START);
                serviceIntent.PutExtra("isAutostart", true);
                ContextCompat.StartForegroundService(Application.Context, serviceIntent);
            }
        }
    }

    [Service(
        Name = "naive.NaiveSocksAndroid.BgService"
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

        private Receiver receiverScreenState;

        bool isScreenOn;

        public bool IsForegroundRunning { private set; get; }

        Config currentConfig = new Config();

        Logger Logger = new Logger("Service", Logging.RootLogger);

        class Config
        {
            public int manage_interval_screen_off { get; set; } = 30;
            public int manage_interval_screen_on { get; set; } = 5;
            public int notif_update_interval { get; set; } = 2;

            public bool socket_underlying { get; set; } = false;

            public string title_format { get; set; } = "[{0}/{1}] {2:N0} KB, {3:N0} pkt, {4:N2} CPUs";
        }

        bool notification_show_logs = true;

        //private ServiceConnection<ConfigService> cfgService;

        public override void OnCreate()
        {
            CrashHandler.CheckInit();
            AppConfig.Init(ApplicationContext);

            base.OnCreate();

            Logging.Logged += Logging_Logged;

            Logger.info("service is starting...");

            powerManager = (PowerManager)GetSystemService(Context.PowerService);
            notificationManager = (NotificationManager)GetSystemService(Context.NotificationService);

            var chId = "nsocks_service";
            if (isO) {
                var chan = new NotificationChannel(chId, "NaiveSocks Service", NotificationImportance.Low);
                chan.LockscreenVisibility = NotificationVisibility.Private;
                notificationManager.CreateNotificationChannel(chan);
            }

            builder = new NotificationCompat.Builder(this, chId)
                        .SetContentIntent(BuildIntentToShowMainActivity())
                        //.SetContentTitle("NaiveSocks")
                        //.SetSubText("running")
                        //.SetStyle(bigText)
                        .AddAction(BuildServiceAction(Actions.KILL, "Kill", Android.Resource.Drawable.StarOff, 1))
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
                        //.SetContentIntent(BuildServicePendingIntent("start!", 10086))
                        .SetContentTitle("NaiveSocks service is stopped")
                        //.SetContentText("touch here to restart")
                        .AddAction(BuildServiceAction(Actions.START, "Start", Android.Resource.Drawable.StarOff, 6))
                        .SetSmallIcon(Resource.Drawable.N)
                        .SetColor(unchecked((int)0xFF2196F3))
                        .SetAutoCancel(true)
                        .SetPriority((int)NotificationPriority.Min)
                        .SetVisibility(NotificationCompat.VisibilitySecret)
                        .SetShowWhen(false);
            //StartForegroundService();
        }

        public event Action<BgService> ForegroundStateChanged;

        private void ToForeground()
        {
            Logger.info("ToForeground()");
            if (IsForegroundRunning) {
                Logger.logWithStackTrace("RunForegound() while isForegroundRunning", Logging.Level.Warning);
                return;
            }

            StartForeground(MainNotificationId, builder.Build());

            Controller = new Controller();
            Controller.Logger.ParentLogger = Logging.RootLogger;

            IsForegroundRunning = true;
            ForegroundStateChanged?.Invoke(this);

            onScreen(powerManager.IsInteractive);
            var filter = new IntentFilter(Intent.ActionScreenOff);
            filter.AddAction(Intent.ActionScreenOn);
            RegisterReceiver(receiverScreenState = new Receiver((c, i) => {
                if (i.Action == Intent.ActionScreenOn) {
                    onScreen(true);
                } else if (i.Action == Intent.ActionScreenOff) {
                    onScreen(false);
                }
            }), filter);

            SetShowLogs(AppConfig.Current.ShowLogs, true);

            Task.Run(() => {
                Logger.info("starting controller...");
                try {
                    Controller.ConfigTomlLoaded += (t) => {
                        CrashHandler.CrashLogFile = Controller.ProcessFilePath("UnhandledException.txt");
                        if (t.TryGetValue<Config>("android", out var config)) {
                            currentConfig = config;
                            SocketStream.EnableUnderlyingCalls = config.socket_underlying;
                            onScreen(isScreenOn);
                        }
                    };
                    var paths = AppConfig.GetNaiveSocksConfigPaths(this);
                    Controller.LoadConfigFileFromMultiPaths(paths);
                    Controller.Start();
                    Logger.info("controller started.");
                } catch (System.Exception e) {
                    Logger.exception(e, Logging.Level.Error, "loading/starting controller");
                    ShowToast("starting error: " + e.Message);
                    StopSelf();
                }
            });

            updateNotif(true);
        }

        private void ToBackground(bool removeNotif)
        {
            Logger.info("ToBackground(" + removeNotif + ")");
            notifyTimer?.Dispose();
            notifyTimer = null;
            StopForeground(removeNotif);
            UnregisterReceiver(receiverScreenState);
            receiverScreenState = null;
            onScreen(false);
            IsForegroundRunning = false;
            ForegroundStateChanged?.Invoke(this);
            Controller.Stop();
        }

        [return: GeneratedEnum]
        public override StartCommandResult OnStartCommand(Intent intent, [GeneratedEnum] StartCommandFlags flags, int startId)
        {
            if (intent != null) {
                string action = intent.Action;
                if (action == Actions.TOGGLE) {
                    Logger.info("toggling controller...");
                    action = IsForegroundRunning ? Actions.STOP : Actions.START;
                }
                switch (action) {
                case Actions.START:
                    this.ToForeground();
                    break;
                case Actions.STOP:
                    if (IsForegroundRunning)
                        ToBackground(true);
                    this.StopSelf();
                    break;
                case Actions.KILL:
                    if (IsForegroundRunning)
                        ToBackground(false);
                    this.StopSelf(startId);
                    notificationManager.Notify(MainNotificationId, restartBuilder.Build());
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    break;
                case Actions.RELOAD:
                    if (!IsForegroundRunning) {
                        this.StopSelf();
                        Logging.warning("intent.Action == RELOAD while !IsForegroundRunning");
                        break;
                    }

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
                    GC.Collect(action == Actions.GC ? GC.MaxGeneration : 0);
                    putLine($"GC Done. {before:N0} -> {GC.GetTotalMemory(false):N0}");
                    if (!IsForegroundRunning)
                        this.StopSelf();
                    break;
                default:
                    Logging.warning("Unknown intent.Action: " + action);
                    if (!IsForegroundRunning)
                        this.StopSelf();
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
            isDestroyed = true;
            Logging.Logged -= Logging_Logged;
            Logging.info("service is being destroyed.");
            if (IsForegroundRunning) {
                ToBackground(true);
            }
            base.OnDestroy();
        }

        bool isDestroyed = false;

        string[] textLines = new string[1];
        bool textLinesChanged = false;

        private void Logging_Logged(Logging.Log log)
        {
            if (isDestroyed)
                return;
            Log.WriteLine(GetPri(log), "naivelog", log.text);
            if (IsForegroundRunning && notification_show_logs) {
                putLine(log.text);
            }
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
            if (IsForegroundRunning)
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
                builder.SetContentTitle(title);
            }
            if (textLinesChanged) {
                textLinesChanged = false;
                needRenotify = true;
                builder.SetContentText(textLines.Get(-1));
                if (notification_show_logs) {
                    if (bigText == null)
                        bigText = new NotificationCompat.BigTextStyle();
                    bigText.BigText(string.Join("\n", textLines.Where(x => !string.IsNullOrEmpty(x))));
                    builder.SetStyle(bigText);
                }
            }
            return needRenotify;
        }

        public void SetShowLogs(bool show, bool forceUpdate = false)
        {
            if (!forceUpdate && show == notification_show_logs)
                return;
            AppConfig.Current.ShowLogs = show;
            lock (builder) {
                notification_show_logs = show;
                if (show) {
                    textLines = new string[3];
                } else {
                    textLines = new string[1];
                    if (bigText != null) {
                        bigText = null;
                        builder.SetStyle(null);

                    }
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
            public const string TOGGLE = "toggle!";
            public const string KILL = "kill!";
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
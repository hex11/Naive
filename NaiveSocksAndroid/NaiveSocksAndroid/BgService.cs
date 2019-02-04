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
using Android.Net;
using Android.OS;
using Android.Runtime;
using Android.Support.V4.App;
using Android.Support.V4.Content;
using Android.Util;
using Android.Views;
using Android.Widget;
using Naive.HttpSvr;
using NaiveSocks;
using R = NaiveSocksAndroid.Resource;

namespace NaiveSocksAndroid
{

    [BroadcastReceiver(Name = "naive.NaiveSocksAndroid.BootReceiver", Enabled = false)]
    [IntentFilter(new[] { Intent.ActionBootCompleted }, Priority = 114514)]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            App.CheckInit();
            if (AppConfig.Current.Autostart) {
                Logging.info("Autostart...");
                Intent serviceIntent = new Intent(context, typeof(BgService));
                serviceIntent.SetAction(BgService.Actions.START);
                serviceIntent.PutExtra("isAutostart", true);
                ContextCompat.StartForegroundService(Application.Context, serviceIntent);
            }
        }
    }

    static class BgServiceRunningState
    {
        public static bool IsRunning;
        public static bool IsInOperation;
        public static event Action StateChanged;
        public static void InvokeStateChanged() => StateChanged?.Invoke();
    }

    [Service(
        Name = "naive.NaiveSocksAndroid.BgService",
        Permission = "android.permission.BIND_VPN_SERVICE"
        //IsolatedProcess = true,
        //Process = ":bg"
        )]
    [IntentFilter(new[] { "android.net.VpnService" })]
    [MetaData("android.net.VpnService.SUPPORTS_ALWAYS_ON", Value = "true")]
    public class BgService : VpnService
    {
        static bool isN = Build.VERSION.SdkInt >= BuildVersionCodes.N;
        static bool isO = Build.VERSION.SdkInt >= BuildVersionCodes.O;

        public Controller Controller { get; private set; }

        private PowerManager powerManager;
        private NotificationManager notificationManager;

        NotificationCompat.Builder builder;
        const int MainNotificationId = 1;
        const string MainNotifChannelId = "nsocks_service";

        private Receiver receiverScreenState;

        bool isScreenOn;

        public bool IsForegroundRunning { get; private set; }
        public bool IsInOperation { get; private set; }

        public MainActivity ShowingActivity;

        Config currentConfig = new Config();

        Logger Logger = new Logger("Service", Logging.RootLogger);

        public static WeakReference<BgService> Instance;

        internal DnsDb DnsDb => vpnHelper?.DnsDb;

        class Config
        {
            public int manage_interval_screen_off { get; set; } = 30;
            public int manage_interval_screen_on { get; set; } = 5;
            public int notif_update_interval { get; set; } = 2;

            public string socket_impl { get; set; }

            public bool copier_sync_r { get; set; }
            public bool copier_sync_w { get; set; }

            public bool socket_underlying { get; set; } = false;

            public string title_format { get; set; } = null;

            public VpnConfig vpn { get; set; }
        }

        //private ServiceConnection<ConfigService> cfgService;

        public override void OnCreate()
        {
            App.CheckInit();

            base.OnCreate();

            Instance = new WeakReference<BgService>(this);

            Logger.info("service is starting...");

            powerManager = (PowerManager)GetSystemService(Context.PowerService);
            notificationManager = (NotificationManager)GetSystemService(Context.NotificationService);
        }

        private void CreateNotifBuilder()
        {
            if (isO) {
                var chan = new NotificationChannel(MainNotifChannelId, "NaiveSocks Service", NotificationImportance.Min);
                chan.LockscreenVisibility = NotificationVisibility.Private;
                notificationManager.CreateNotificationChannel(chan);
            }

            builder = new NotificationCompat.Builder(this, MainNotifChannelId)
                                    .SetContentText(GetString(R.String.initializing))
                                    .SetContentIntent(BuildIntentToShowMainActivity())
                                    .AddAction(BuildServiceAction(Actions.STOP_NOTIF, R.String.stop, Android.Resource.Drawable.StarOff, 1))
                                    //.AddAction(BuildServiceAction(Actions.KILL, R.String.kill, Android.Resource.Drawable.StarOff, 1))
                                    .AddAction(BuildServiceAction(Actions.RELOAD, R.String.reload, Android.Resource.Drawable.StarOff, 2))
                                    .AddAction(BuildServiceAction(Actions.GC, R.String.gc, Android.Resource.Drawable.StarOff, 3))
                                    .SetSmallIcon(R.Drawable.N)
                                    .SetColor(unchecked((int)0xFF2196F3))
                                    .SetUsesChronometer(true)
                                    .SetOngoing(true);

            if (!isO) {
                builder.SetPriority((int)NotificationPriority.Min)
                    .SetVisibility(NotificationCompat.VisibilitySecret);
            }

            if (!isN) {
                builder.SetContentTitle(GetString(R.String.app_name));
            }
        }

        public event Action<BgService> ForegroundStateChanged;

        private void SetRunningState(bool isForeground, bool inOperation)
        {
            bool stateChanged = IsForegroundRunning != isForeground || IsInOperation != inOperation;
            IsForegroundRunning = isForeground;
            IsInOperation = inOperation;
            AppConfig.Current.Set(AppConfig.service_running, isForeground);
            ForegroundStateChanged?.Invoke(this);
            if (stateChanged) {
                BgServiceRunningState.IsInOperation = IsInOperation;
                BgServiceRunningState.IsRunning = isForeground;
                BgServiceRunningState.InvokeStateChanged();
            }
        }

        private void ToForeground()
        {
            Logger.info("ToForeground()");

            if (IsInOperation) {
                Logger.logWithStackTrace("ToForeground() while the service is in operation", Logging.Level.Warning);
                return;
            }

            if (IsForegroundRunning) {
                Logger.logWithStackTrace("ToForeground() while the service is already running in foreground", Logging.Level.Warning);
                return;
            }

            SetRunningState(true, true);

            CreateNotifBuilder();

            StartForeground(MainNotificationId, builder.Build());

            Controller = new Controller();
            Controller.Logger.ParentLogger = Logging.RootLogger;

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

            Task.Run(() => {
                Logger.info("load and start controller...");
                try {
                    Controller.ConfigTomlLoaded += (t) => {
                        App.CrashLogFile = Controller.ProcessFilePath("UnhandledException.txt");
                        if (t.TryGetValue<Config>("android", out var config)) {
                            currentConfig = config;
                            if (config.socket_impl != null) {
                                MyStream.SetSocketImpl(config.socket_impl);
                            } else {
                                MyStream.CurrentSocketImpl = MyStream.SocketImpl.YASocket;
                            }
                            MyStream.Copier.TryReadSync = config.copier_sync_r;
                            MyStream.Copier.TryWriteSync = config.copier_sync_w;
                            SocketStream.EnableUnderlyingCalls = config.socket_underlying;
                            onScreen(isScreenOn);
                        }
                    };
                    var paths = AppConfig.GetNaiveSocksConfigPaths(this);
                    putLine(GetString(R.String.controller_loading));
                    Controller.LoadConfigFileFromMultiPaths(paths, true);
                    putLine(GetString(R.String.controller_starting));
                    Controller.Start();
                    Logger.info("controller started.");
                    putLine(GetString(R.String.controller_started));
                    CheckVPN();
                } catch (System.Exception e) {
                    Logger.exception(e, Logging.Level.Error, "loading/starting controller");
                    string msg = GetString(R.String.starting_error) + e.Message;
                    putLine(msg, 10000);
                    ShowToast(msg);
                    StopSelf();
                } finally {
                    SetRunningState(true, false);
                }
            });

            updateNotif(true);
        }

        private void CheckVPN()
        {
            if (currentConfig?.vpn?.Enabled == true) {
                Logging.info("Starting VPN");
                StartVpn();
            } else {
                vpnHelper = null;
            }
        }

        VpnHelper vpnHelper;

        private void StartVpn()
        {
            if (VpnService.Prepare(this) != null) {
                var activity = ShowingActivity;
                if (activity?.Handler.Post(activity.VpnServicePrepare) == true) {
                    Logging.info("Using showing activity to request VPN permission.");
                } else {
                    Logging.info("Starting activity to request VPN permission.");
                    StartActivity(new Intent(this, typeof(MainActivity)).SetAction("PREP_VPN").SetFlags(ActivityFlags.NewTask));
                }
            } else {
                // continue starting vpn service
                try {
                    if (vpnHelper == null)
                        vpnHelper = new VpnHelper(this);
                    // else it may be reloading
                    vpnHelper.VpnConfig = currentConfig.vpn;
                    vpnHelper.StartVpn();
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "Starting VPN");
                }
            }
        }

        private void ToBackground(bool removeNotif)
        {
            Logger.info("ToBackground(" + removeNotif + ")");
            if (IsInOperation) {
                Logger.logWithStackTrace("ToBackground() while the service is in operation", Logging.Level.Warning);
                return;
            }
            notifyTimer?.Dispose();
            notifyTimer = null;
            StopForeground(removeNotif);
            UnregisterReceiver(receiverScreenState);
            receiverScreenState = null;
            onScreen(false);
            SetRunningState(false, true);
            vpnHelper?.Stop();
            vpnHelper = null;
            Task.Run(() => {
                try {
                    Controller.Stop();
                } finally {
                    SetRunningState(false, false);
                }
            });
        }

        [return: GeneratedEnum]
        public override StartCommandResult OnStartCommand(Intent intent, [GeneratedEnum] StartCommandFlags flags, int startId)
        {
            string action;
            if (intent == null) {
                bool wasRunning = AppConfig.Current.GetBool(AppConfig.service_running, false);
                action = wasRunning ? Actions.START : Actions.STOP;
                Logger.warning("The service has been killed and now it's being restarted by OS. last running state: " + wasRunning);
            } else {
                action = intent.Action;
            }
            if (action == Actions.TOGGLE) {
                Logger.info("toggling controller...");
                action = IsForegroundRunning ? Actions.STOP : Actions.START;
                var msg = string.Format(Resources.GetString(IsForegroundRunning ? R.String.format_stopping : R.String.format_starting),
                    Resources.GetString(R.String.app_name));
                ShowToast(msg);
            }
            switch (action) {
                case Actions.START:
                    this.ToForeground();
                    break;
                case "android.net.VpnService":
                    Logger.info("(intent: android.net.VpnService)");
                    this.ToForeground();
                    break;
                case Actions.START_VPN:
                    StartVpn();
                    break;
                case Actions.STOP:
                case Actions.STOP_NOTIF:
                    if (IsForegroundRunning)
                        ToBackground(true);
                    this.StopSelf();
                    if (action == Actions.STOP_NOTIF)
                        ShowRestartNotification(false);
                    break;
                case Actions.KILL:
                    if (IsForegroundRunning)
                        ToBackground(false);
                    this.StopSelf(startId);
                    ShowRestartNotification(true);
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    break;
                case Actions.RELOAD:
                    putLine(GetString(R.String.controller_reloading));
                    Reload();
                    break;
                case Actions.GC:
                case Actions.GC_0:
                    var before = GC.GetTotalMemory(false);
                    GC.Collect(action == Actions.GC ? GC.MaxGeneration : 0);
                    putLine($"{GetString(R.String.gc_done)} {before:N0} -> {GC.GetTotalMemory(false):N0}");
                    if (!IsForegroundRunning)
                        this.StopSelf();
                    break;
                default:
                    Logging.warning("Unknown intent.Action: " + action);
                    if (!IsForegroundRunning)
                        this.StopSelf();
                    break;
            }
            return StartCommandResult.Sticky;
        }

        private void ShowRestartNotification(bool killed)
        {
            var builder = new NotificationCompat.Builder(this, MainNotifChannelId)
                           .SetContentTitle(GetString(killed ? R.String.process_is_killed : R.String.service_is_stopped))
                           .SetContentIntent(BuildIntentToShowMainActivity())
                           .AddAction(BuildServiceAction(Actions.START, R.String.start, Android.Resource.Drawable.StarOff, 6))
                           .SetSmallIcon(Resource.Drawable.N)
                           .SetColor(unchecked((int)0xFF2196F3))
                           .SetAutoCancel(true)
                           .SetPriority((int)NotificationPriority.Min)
                           .SetVisibility(NotificationCompat.VisibilitySecret)
                           .SetShowWhen(false);
            if (!killed)
                builder.AddAction(BuildServiceAction(Actions.KILL, R.String.kill, Android.Resource.Drawable.StarOff, 1));
            notificationManager.Notify(MainNotificationId, builder.Build());
        }

        public void Reload()
        {
            Task.Run(() => {
                try {
                    if (IsInOperation) {
                        Logging.warning("Reload() while the service is in operation");
                        return;
                    }
                    if (!IsForegroundRunning) {
                        this.StopSelf();
                        Logging.warning("Reload() while the service is foreground running");
                        return;
                    }
                    SetRunningState(true, true);
                    vpnHelper?.Stop();
                    Controller.Reload();
                    CheckVPN();
                    putLine(GetString(R.String.controller_reloaded));
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "BgService.Reload()");
                    var errString = GetString(R.String.reloading_error);
                    ShowToast(errString + e.Message);
                    putLine(errString + e.ToString());
                } finally {
                    SetRunningState(true, false);
                }
            });
        }

        public override IBinder OnBind(Intent intent)
        {
            return new Binder<BgService>(this);
        }

        public override void OnDestroy()
        {
            isDestroyed = true;
            Logging.info("service is being destroyed.");
            if (IsForegroundRunning) {
                ToBackground(true);
            }
            base.OnDestroy();
        }

        bool isDestroyed = false;

        string textLine;
        long textLineEndtime = 0;
        bool textLinesChanged = false;

        private void putLine(string line, int timeout = 5000)
        {
            textLineEndtime = Logging.getRuntime() + timeout;
            textLine = line;
            textLinesChanged = true;
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

        long lastRuntime = 0;
        long lastCpuTime = 0;
        long lastCopiedBytes = 0;

        StringBuilder titleSb = null;

        private bool updateNotifBuilder()
        {
            var needRenotify = false;

            var curRuntime = Logging.getRuntime();
            string title = FormatTitle(curRuntime);

            if (title != lastTitle) {
                needRenotify = true;
                lastTitle = title;
                builder.SetContentTitle(title);
            }
            if (textLine != null && curRuntime > textLineEndtime) {
                textLine = null;
                textLinesChanged = true;
            }
            if (textLinesChanged) {
                textLinesChanged = false;
                needRenotify = true;
                builder.SetContentText(textLine);
            }
            return needRenotify;
        }

        private string FormatTitle(long curRuntime)
        {
            var curCpuTime = Process.ElapsedCpuTime;
            float deltaRuntime = (float)Math.Max(1, curRuntime - lastRuntime);
            var load = (curCpuTime - lastCpuTime) / deltaRuntime;
            lastRuntime = curRuntime;
            lastCpuTime = curCpuTime;

            long copiedBytes = MyStream.TotalCopiedBytes;
            var deltaBytes = copiedBytes - lastCopiedBytes;
            lastCopiedBytes = copiedBytes;

            var kiBps = ((float)deltaBytes / 1024) / (deltaRuntime / 1000);

            string title;
            if (currentConfig.title_format == null) {
                // "{0}/{1} | {2:N0} KB ({5:N0}/s) | {4:N2} CPUs"
                var sb = titleSb ?? (titleSb = new StringBuilder());
                sb.Append(Controller.RunningConnections).Append('/').Append(Controller.TotalHandledConnections);
                sb.Append(" | ");
                sb.Append((copiedBytes / 1024).ToString("N0")).Append(" KB (");
                if (kiBps == 0)
                    sb.Append('0');
                else if (kiBps >= 1)
                    sb.Append(kiBps.ToString("N0"));
                else
                    sb.Append("<1");
                sb.Append("/s) | ");
                sb.Append(load.ToString("N2")).Append(" CPUs");
                title = sb.ToString();
                sb.Clear();
            } else {
                title = string.Format(currentConfig.title_format,
                        Controller.RunningConnections,
                        Controller.TotalHandledConnections,
                        copiedBytes / 1024,
                        MyStream.TotalCopiedPackets,
                        load,
                        kiBps);
            }

            return title;
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

        NotificationCompat.Action BuildServiceAction(string action, int textResId, int icon, int requestCode)
            => BuildServiceAction(action, Resources.GetString(textResId), icon, requestCode);

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
            public const string START_VPN = "startVpn!";
            public const string STOP = "stop!";
            public const string STOP_NOTIF = "stop_notif!";
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
            base.OnServiceDisconnected(name);
            Value = null;
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
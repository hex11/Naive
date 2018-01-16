using Android.App;
using Android.Widget;
using Android.OS;
using System.Threading.Tasks;
using NaiveSocks;
using Naive.HttpSvr;
using Naive.Console;
using System.IO;
using Android.Content;
using Android.Net;
using Android.Views;
using Android.Support.V7.App;
using Toolbar = Android.Support.V7.Widget.Toolbar;
using Android.Graphics;
using System;
using Android.Support.V4.Widget;
using Android.Content.PM;
using R = NaiveSocksAndroid.Resource;
using Android.Support.V7.Widget;
using System.Linq;
using Android.Support.Design.Widget;

namespace NaiveSocksAndroid
{
    [Activity(
        Label = "NaiveSocks",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTask,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class MainActivity : AppCompatActivity
    {
        LinearLayout outputParent;
        NestedScrollView outputParentScroll;

        ContextThemeWrapper logThemeWrapper;

        CoordinatorLayout topView;

        Intent serviceIntent;

        bool isConnected => bgServiceConn?.IsConnected ?? false;
        BgService service => bgServiceConn?.Value;
        ServiceConnection<BgService> bgServiceConn;

        private Toolbar toolbar;
        const string TOOLBAR_TITLE = "NaiveSocks";

        //private ServiceConnection<ConfigService> cfgService;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            CrashHandler.CheckInit();
            GlobalConfig.Init(ApplicationContext);

            base.OnCreate(savedInstanceState);

            //BindService(new Intent(this, typeof(ConfigService)), cfgService = new ServiceConnection<ConfigService>(), Bind.AutoCreate);

            serviceIntent = new Intent(this, typeof(BgService));
            serviceIntent.SetAction("start!");

            bgServiceConn = new ServiceConnection<BgService>(
                connected: (ComponentName name, IBinder service) => {
                    toolbar.Title = TOOLBAR_TITLE + " - running";
                    InvalidateOptionsMenu();
                },
                disconnected: (ComponentName name) => {
                    toolbar.Title = TOOLBAR_TITLE;
                    InvalidateOptionsMenu();
                });

            logThemeWrapper = new ContextThemeWrapper(this, R.Style.LogTextView);

            // Set our view from the "main" layout resource
            SetContentView(R.Layout.Main);

            topView = FindViewById<CoordinatorLayout>(R.Id.topview);

            toolbar = FindViewById<Toolbar>(R.Id.toolbar);
            SetSupportActionBar(toolbar);
            toolbar.Title = TOOLBAR_TITLE;

            outputParent = this.FindViewById<LinearLayout>(R.Id.logparent);
            outputParentScroll = this.FindViewById<NestedScrollView>(R.Id.logparentScroll);

            Logging.Logged += Logging_Logged;
            var logs = Logging.getLogsHistoryArray();
            if (logs.Length > 0) {
                for (int i = 0; i < logs.Length; i++) {
                    putLog(logs[i], false);
                }
                putText("========== end of log history ==========", true);
            }
        }

        private void startService()
        {
            Task.Run(() => {
                if (!isConnected) {
                    Logging.info("starting/binding service...");
                    StartService(serviceIntent);
                    this.BindService(serviceIntent, bgServiceConn, Bind.None);
                } else {
                    Logging.info("cannot start service: service is already running.");
                }
            });
        }

        private void stopService()
        {
            Task.Run(() => {
                Logging.info("requesting to stop service.");
                if (!StopService(serviceIntent)) {
                    Logging.info("cannot stop service: service is not connected.");
                }
            });
        }

        private void reloadService()
        {
            Task.Run(() => {
                if (isConnected) {
                    try {
                        service.Controller.Reload();
                    } catch (Exception e) {
                        Logging.exception(e, Logging.Level.Error, "reloading controller");
                    }
                } else {
                    Logging.info("connot reload: service is not connected");
                }
            });
        }

        const string menu_showLogs = "Show logs in notification";
        const string menu_autostart = "Autostart";
        const string menu_openConfig = "Open configuation file...";

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            MenuInflater.Inflate(R.Menu.menu_control, menu);
            if (isConnected) {
                menu.FindItem(R.Id.menu_start).SetVisible(false);
            } else {
                menu.FindItem(R.Id.menu_stop).SetVisible(false);
                menu.FindItem(R.Id.menu_reload).SetVisible(false);
            }
            menu.Add(menu_showLogs)
                .SetCheckable(true)
                .SetChecked(GlobalConfig.Current.GetShowLogs())
                .SetShowAsActionFlags(ShowAsAction.Never);
            menu.Add(menu_autostart)
                .SetCheckable(true)
                .SetChecked(GlobalConfig.Current.GetAutostart())
                .SetShowAsActionFlags(ShowAsAction.Never);
            menu.Add(menu_openConfig)
                .SetShowAsActionFlags(ShowAsAction.Never);
            return true;
        }

        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            var id = item.ItemId;
            if (id == R.Id.menu_start) {
                startService();
            } else if (id == R.Id.menu_stop) {
                stopService();
            } else if (id == R.Id.menu_reload) {
                reloadService();
            } else {
                var title = item.TitleFormatted.ToString();
                if (title == menu_showLogs) {
                    setShowLogs(!item.IsChecked);
                } else if (title == menu_autostart) {
                    setAutostart(!item.IsChecked);
                } else if (title == menu_openConfig) {
                    var paths = GlobalConfig.GetNaiveSocksConfigPaths(this);
                    var found = paths.FirstOrDefault(x => File.Exists(x));
                    if (found == null) {
                        Snackbar.Make(topView, "No configuation file.", Snackbar.LengthLong).Show();
                    } else {
                        var intent = new Intent(Intent.ActionEdit);
                        intent.SetDataAndType(Android.Net.Uri.FromFile(new Java.IO.File(found)), "text/plain");
                        intent.SetFlags(ActivityFlags.NewTask);
                        StartActivity(intent);
                    }
                }
            }
            return base.OnOptionsItemSelected(item);
        }

        private void setAutostart(bool enabled)
        {
            GlobalConfig.Current.SetAutostart(enabled);
            var pm = this.PackageManager;
            var componentName = new ComponentName(this, Java.Lang.Class.FromType(typeof(BootReceiver)));
            var enabledState = (enabled) ? ComponentEnabledState.Enabled : ComponentEnabledState.Disabled;
            pm.SetComponentEnabledSetting(componentName, enabledState, ComponentEnableOption.DontKillApp);
            this.InvalidateOptionsMenu();
            Snackbar.Make(topView, $"Autostart is {(enabled ? "enabled" : "disabled")}.", Snackbar.LengthLong).Show();
        }

        void setShowLogs(bool show)
        {
            GlobalConfig.Current.SetShowLogs(show);
            this.InvalidateOptionsMenu();
            if (isConnected) {
                service.SetShowLogs(show);
            }
            Snackbar.Make(topView, $"Logger output will{(show ? "" : " not")} be shown in notification.", Snackbar.LengthLong).Show();
        }

        protected override void OnDestroy()
        {
            Logging.Logged -= Logging_Logged;
            base.OnDestroy();
        }

        private void Logging_Logged(Logging.Log log)
        {
            outputParent.Post(() => putLog(log, true));
        }

        private void putLog(Logging.Log log, bool autoScroll)
        {
            putText($"[{log.time.ToLongTimeString()} {log.levelStr}] {log.text}", autoScroll, getColorFromLevel(log.level));
        }

        private Color? getColorFromLevel(Logging.Level level)
        {
            switch (level) {
            case Logging.Level.None:
            case Logging.Level.Debug:
                return null;
            case Logging.Level.Info:
                return Color.Argb(30, 0, 255, 0);
            case Logging.Level.Warning:
                return Color.Argb(30, 255, 255, 0);
            case Logging.Level.Error:
            default:
                return Color.Argb(30, 255, 0, 0);
            }
        }

        private void putText(string text, bool autoScroll = true, Android.Graphics.Color? color = null)
        {
            var tv = new TextView(logThemeWrapper);
            tv.Text = text;
            if (color != null)
                tv.SetBackgroundColor(color.Value);
            //autoScroll = autoScroll && !outputParentScroll.CanScrollVertically(0);
            outputParent.AddView(tv);
            if (autoScroll) {
                outputParentScroll.Post(() => outputParentScroll.FullScroll((int)FocusSearchDirection.Down));
            }
        }

        protected override void OnStart()
        {
            base.OnStart();
            this.BindService(serviceIntent, bgServiceConn, Bind.None);
        }

        protected override void OnStop()
        {
            if (bgServiceConn?.IsConnected == true)
                this.UnbindService(bgServiceConn);
            base.OnStop();
        }
    }
}


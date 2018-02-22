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
using Android.Support.V4.Content;
using Android;
using Android.Support.V4.App;
using Android.Runtime;

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
            AppConfig.Init(ApplicationContext);

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

            if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.ReadExternalStorage)
                != Permission.Granted) {
                putText("requesting storage read/write permissions...");
                ActivityCompat.RequestPermissions(this, new[] {
                    Manifest.Permission.ReadExternalStorage,
                    Manifest.Permission.WriteExternalStorage
                }, 1);
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
        {
            for (int i = 0; i < permissions.Length; i++) {
                putText($"permission {(grantResults[i] == Permission.Granted ? "granted" : "denied")}: {permissions[i]}");
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
                .SetChecked(AppConfig.Current.ShowLogs)
                .SetShowAsActionFlags(ShowAsAction.Never);
            menu.Add(menu_autostart)
                .SetCheckable(true)
                .SetChecked(AppConfig.Current.Autostart)
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
                    var paths = AppConfig.GetNaiveSocksConfigPaths(this);
                    var found = paths.FirstOrDefault(x => File.Exists(x));
                    if (found == null) {
                        MakeSnackbar("No configuation file.", Snackbar.LengthLong).Show();
                    } else {
                        var intent = new Intent(Intent.ActionEdit);
                        Android.Net.Uri fileUri;
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.N) {
                            fileUri = FileProvider.GetUriForFile(this, "naive.NaiveSocksAndroid.fp", new Java.IO.File(found));
                            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                            intent.AddFlags(ActivityFlags.GrantWriteUriPermission);
                        } else {
                            fileUri = Android.Net.Uri.FromFile(new Java.IO.File(found));
                        }
                        intent.SetDataAndType(fileUri, "text/plain");
                        intent.AddFlags(ActivityFlags.NewTask);
                        try {
                            StartActivity(intent);
                        } catch (ActivityNotFoundException) {
                            MakeSnackbar("No activity to handle", Snackbar.LengthLong).Show();
                        }
                    }
                }
            }
            return base.OnOptionsItemSelected(item);
        }

        private void setAutostart(bool enabled)
        {
            AppConfig.Current.Autostart = enabled;
            var pm = this.PackageManager;
            var componentName = new ComponentName(this, Java.Lang.Class.FromType(typeof(BootReceiver)));
            var enabledState = (enabled) ? ComponentEnabledState.Enabled : ComponentEnabledState.Disabled;
            pm.SetComponentEnabledSetting(componentName, enabledState, ComponentEnableOption.DontKillApp);
            this.InvalidateOptionsMenu();
            MakeSnackbar($"Autostart is {(enabled ? "enabled" : "disabled")}.", Snackbar.LengthLong).Show();
        }

        void setShowLogs(bool show)
        {
            AppConfig.Current.ShowLogs = show;
            this.InvalidateOptionsMenu();
            if (isConnected) {
                service.SetShowLogs(show);
            }
            MakeSnackbar($"Logger output will{(show ? "" : " not")} be shown in notification.", Snackbar.LengthLong).Show();
        }

        private Snackbar MakeSnackbar(string text, int duration)
        {
            return Snackbar.Make(topView, text, duration);
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
            tv.Dispose();
            if (autoScroll) {
                outputParentScroll.Post(() => outputParentScroll.FullScroll((int)FocusSearchDirection.Down));
            }
        }

        protected override void OnStart()
        {
            base.OnStart();
            Logging.Logged += Logging_Logged;
            var logs = Logging.getLogsHistoryArray();
            if (logs.Length > 0) {
                for (int i = 0; i < logs.Length; i++) {
                    putLog(logs[i], false);
                }
                putText("========== end of log history ==========", true);
            }
            this.BindService(serviceIntent, bgServiceConn, Bind.None);
        }

        protected override void OnStop()
        {
            Logging.Logged -= Logging_Logged;
            outputParent.RemoveAllViews();
            if (bgServiceConn?.IsConnected == true)
                this.UnbindService(bgServiceConn);
            GC.Collect(0);
            base.OnStop();
        }
    }
}

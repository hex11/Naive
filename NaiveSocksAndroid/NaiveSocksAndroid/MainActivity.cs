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

namespace NaiveSocksAndroid
{
    [Activity(
        Label = "NaiveSocks",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTask,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class MainActivity : AppCompatActivity, IServiceConnection
    {
        LinearLayout outputParent;
        NestedScrollView outputParentScroll;

        ContextThemeWrapper logThemeWrapper;

        Intent serviceIntent;

        BgService service;
        bool isConnected;
        private Toolbar toolbar;
        const string TOOLBAR_TITLE = "NaiveSocks";

        //private ServiceConnection<ConfigService> cfgService;

        public void OnServiceConnected(ComponentName name, IBinder service)
        {
            var binder = service as BgServiceBinder;
            this.service = binder.BgService;
            isConnected = true;
            toolbar.Title = TOOLBAR_TITLE + " - running";
            InvalidateOptionsMenu();
        }

        public void OnServiceDisconnected(ComponentName name)
        {
            isConnected = false;
            toolbar.Title = TOOLBAR_TITLE;
            InvalidateOptionsMenu();
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            CrashHandler.CheckInit();

            base.OnCreate(savedInstanceState);

            //BindService(new Intent(this, typeof(ConfigService)), cfgService = new ServiceConnection<ConfigService>(), Bind.AutoCreate);

            serviceIntent = new Intent(this, typeof(BgService));
            serviceIntent.SetAction("start!");

            logThemeWrapper = new ContextThemeWrapper(this, Resource.Style.LogTextView);

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.Main);

            toolbar = FindViewById<Toolbar>(Resource.Id.toolbar);
            SetSupportActionBar(toolbar);
            toolbar.Title = TOOLBAR_TITLE;

            outputParent = this.FindViewById<LinearLayout>(Resource.Id.logparent);
            outputParentScroll = this.FindViewById<NestedScrollView>(Resource.Id.logparentScroll);

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
                    this.BindService(serviceIntent, this, Bind.None);
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

        const string menu_hideLogs = "Hide logs in notification";
        const string menu_showLogs = "Show logs in notification";

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            MenuInflater.Inflate(R.Menu.menu_control, menu);
            if (isConnected) {
                menu.FindItem(R.Id.menu_start).SetVisible(false);
            } else {
                menu.FindItem(R.Id.menu_stop).SetVisible(false);
                menu.FindItem(R.Id.menu_reload).SetVisible(false);
            }
            menu.Add(ConfigService.GetShowLogs(ApplicationContext) ? menu_hideLogs : menu_showLogs)
                .SetShowAsActionFlags(ShowAsAction.Never);
            return true;
        }

        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            var id = item.ItemId;
            if (id == Resource.Id.menu_start) {
                startService();
            } else if (id == Resource.Id.menu_stop) {
                stopService();
            } else if (id == Resource.Id.menu_reload) {
                reloadService();
            } else {
                var title = item.TitleFormatted.ToString();
                if (title == menu_showLogs) {
                    setShowLogs(true);
                } else if (title == menu_hideLogs) {
                    setShowLogs(false);
                }
            }
            return base.OnOptionsItemSelected(item);
        }

        void setShowLogs(bool show)
        {
            this.InvalidateOptionsMenu();
            ConfigService.SetShowLogs(ApplicationContext, show);
            if (isConnected) {
                service.SetShowLogs(show);
            }
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
            this.BindService(serviceIntent, this, Bind.None);
        }

        protected override void OnStop()
        {
            if (this.isConnected)
                this.UnbindService(this);
            base.OnStop();
        }
    }
}


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

namespace NaiveSocksAndroid
{
    [Activity(
        Label = "NaiveSocks",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTask,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    public class MainActivity : AppCompatActivity, IServiceConnection
    {
        TextView state;
        LinearLayout outputParent;
        NestedScrollView outputParentScroll;

        ContextThemeWrapper logThemeWrapper;

        Intent serviceIntent;

        BgService service;
        bool isConnected;

        public void OnServiceConnected(ComponentName name, IBinder service)
        {
            var binder = service as BgServiceBinder;
            this.service = binder.BgService;
            isConnected = true;
            state.Text = "service connected";
        }

        public void OnServiceDisconnected(ComponentName name)
        {
            isConnected = false;
            state.Text = "service disconncected";
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            CrashHandler.CheckInit();

            base.OnCreate(savedInstanceState);

            serviceIntent = new Intent(this, typeof(BgService));
            serviceIntent.SetAction("start!");

            logThemeWrapper = new ContextThemeWrapper(this, Resource.Style.LogTextView);

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.Main);

            var toolbar = FindViewById<Toolbar>(Resource.Id.toolbar);
            SetSupportActionBar(toolbar);
            toolbar.Title = "NaiveSocks";

            outputParent = this.FindViewById<LinearLayout>(Resource.Id.logparent);
            outputParentScroll = this.FindViewById<NestedScrollView>(Resource.Id.logparentScroll);
            state = this.FindViewById<TextView>(Resource.Id.state);

            var btnStart = this.FindViewById<Button>(Resource.Id.start);
            btnStart.Click += (s, e) => {
                if (!isConnected) {
                    Logging.info("starting/binding service...");
                    Task.Run(() => {
                        StartService(serviceIntent);
                        this.BindService(serviceIntent, this, Bind.None);
                    });
                } else {
                    Logging.info("cannot start service: service is already running.");
                }
            };
            var btnStop = this.FindViewById<Button>(Resource.Id.stop);
            btnStop.Click += (s, e) => {
                Logging.info("requesting to stop service.");
                if (!StopService(serviceIntent)) {
                    Logging.info("cannot stop service: service is not running.");
                }
            };
            Logging.Logged += Logging_Logged;
            var logs = Logging.getLogsHistoryArray();
            if (logs.Length > 0) {
                for (int i = 0; i < logs.Length; i++) {
                    putLog(logs[i], false);
                }
                putText("========== end of log history ==========", true);
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


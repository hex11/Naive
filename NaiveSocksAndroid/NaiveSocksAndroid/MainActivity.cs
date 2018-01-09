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

namespace NaiveSocksAndroid
{
    [Activity(Label = "NaiveSocks", MainLauncher = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTask)]
    public class MainActivity : Activity, IServiceConnection
    {
        TextView state;
        LinearLayout outputParent;
        ScrollView outputParentScroll;

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

            outputParent = this.FindViewById<LinearLayout>(Resource.Id.logparent);
            outputParentScroll = this.FindViewById<ScrollView>(Resource.Id.logparentScroll);
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
                if (!StopService(serviceIntent)) {
                    Logging.info("cannot stop service: service is not running.");
                } else {
                    Logging.info("requested to stop service.");
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
            putText($"[{log.time.ToLongTimeString()} {log.levelStr}] {log.text}", autoScroll);
        }

        private void putText(string text, bool autoScroll = true)
        {
            var tv = new TextView(logThemeWrapper);
            tv.Text = text;
            //autoScroll = autoScroll && !outputParentScroll.CanScrollVertically(0);
            outputParent.AddView(tv);
            if (autoScroll) {
                outputParentScroll.Post(() => outputParentScroll.FullScroll(FocusSearchDirection.Down));
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


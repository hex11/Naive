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

namespace NaiveSocksAndroid
{
    [Activity(Label = "NaiveSocks", MainLauncher = true)]
    public class MainActivity : Activity, IServiceConnection
    {
        TextView logoutput, state;

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
            base.OnCreate(savedInstanceState);

            serviceIntent = new Intent(this, typeof(BgService));

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.Main);

            logoutput = this.FindViewById<TextView>(Resource.Id.logoutput);
            state = this.FindViewById<TextView>(Resource.Id.state);

            var btnStart = this.FindViewById<Button>(Resource.Id.start);
            btnStart.Click += (s, e) => {
                if (!isConnected) {
                    StartService(serviceIntent);
                    this.BindService(serviceIntent, this, Bind.None);
                }
            };
            var btnStop = this.FindViewById<Button>(Resource.Id.stop);
            btnStop.Click += (s, e) => {
                StopService(serviceIntent);
            };
        }

        protected override void OnStart()
        {
            base.OnStart();
            this.BindService(serviceIntent, this, Bind.None);
        }

        protected override void OnStop()
        {
            this.UnbindService(this);
            base.OnStop();
        }
    }
}


using Android.Views;
using Android.Support.V4.App;
using System.Threading;
using Android.OS;
using Java.Lang;
using Android.Content;
using Naive.HttpSvr;
using NaiveSocks;
using System;

namespace NaiveSocksAndroid
{
    public class MyBaseFragment : Fragment
    {
        Handler handler;
        int timerInterval = -1;
        Runnable callbackRunnable;

        static IncrNumberGenerator idGen = new IncrNumberGenerator();
        int id = idGen.Get();

        protected virtual bool UpdateOnConnected => true;
        bool lastUpdateConnected = false;

        void DebugEvent(string text)
        {
            //Logging.debugForce(this.GetType().Name + "#" + id + ": " + text);
        }

        public MyBaseFragment()
        {
            DebugEvent(".ctor");
            callbackRunnable = new Runnable(Callback);
        }

        public MainActivity MainActivity { get; private set; }

        public NaiveSocks.Controller Controller => MainActivity?.Service?.Controller;

        public event Action<string> InfoStrChanged;

        protected bool InfoStrSupport => InfoStrChanged != null;

        protected void ChangeInfoStr(string str) => InfoStrChanged?.Invoke(str);

        public void OnConnected()
        {
            if (IsVisible && !lastUpdateConnected && UpdateOnConnected)
                InvokeUpdate();
        }

        public override void OnAttach(Context context)
        {
            DebugEvent("OnAttach");
            base.OnAttach(context);
            this.MainActivity = context as MainActivity;
            this.handler = MainActivity.Handler;
        }

        public override void OnCreate(Bundle savedInstanceState)
        {
            DebugEvent("OnCreate");
            MainActivity.InvalidateOptionsMenu();
            base.OnCreate(savedInstanceState);
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            DebugEvent("OnCreateView");
            return base.OnCreateView(inflater, container, savedInstanceState);
        }

        public int TimerInterval
        {
            get => timerInterval;
            set {
                if (timerInterval == value)
                    return;
                if (handler != null && timerInterval <= 0 && value > 0) {
                    if (!postOnTheFly)
                        PostDelayed();
                }
                timerInterval = value;
            }
        }

        bool postOnTheFly = false;

        private void Callback()
        {
            postOnTheFly = false;
            if (IsVisible) {
                InvokeUpdate();
            }
            PostDelayed();
        }

        private void PostDelayed()
        {
            if (postOnTheFly)
                throw new System.Exception("PostDelayed() should not be called when postOnTheFly == true");
            if (timerInterval > 0) {
                handler.PostDelayed(callbackRunnable, timerInterval);
                postOnTheFly = true;
            }
        }

        private void InvokeUpdate()
        {
            lastUpdateConnected = MainActivity.Service != null;
            OnUpdate();
        }

        protected virtual void OnUpdate()
        {
        }

        public override void OnStart()
        {
            DebugEvent("OnStart");
            base.OnStart();
            InvokeUpdate();
            if (!postOnTheFly)
                PostDelayed();
        }

        public override void OnResume()
        {
            DebugEvent("OnResume");
            base.OnResume();
        }

        public override void OnPause()
        {
            DebugEvent("OnPause");
            base.OnPause();
        }

        public override void OnStop()
        {
            DebugEvent("OnStop");
            base.OnStop();
            if (postOnTheFly) {
                postOnTheFly = false;
                handler.RemoveCallbacks(callbackRunnable);
            }
        }
    }
}

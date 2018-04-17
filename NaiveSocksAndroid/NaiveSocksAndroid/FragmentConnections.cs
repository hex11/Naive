using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Android.Graphics;

using Android.Support.V4.App;
using Android.Support.V4.Widget;

using R = NaiveSocksAndroid.Resource;
using Naive.HttpSvr;
using NaiveSocks;
using System.Threading;

namespace NaiveSocksAndroid
{
    public class FragmentConnections : Fragment
    {
        LinearLayout connParent;
        private MainActivity mainActivity;
        private ContextThemeWrapper themeWrapper;

        public FragmentConnections(MainActivity mainActivity)
        {
            this.mainActivity = mainActivity;
            timer = new Timer(timer_Callback);
        }

        public override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            themeWrapper = new ContextThemeWrapper(this.Context, R.Style.ConnTextView);

            // Create your fragment here
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            // Use this to return your custom view for this Fragment
            var view = inflater.Inflate(R.Layout.connections, container, false);

            connParent = view.FindViewById<LinearLayout>(R.Id.connparent);

            return view;
        }

        Timer timer;

        private void timer_Callback(object state)
        {
            View.Post(() => {
                if (!IsVisible)
                    return;
                Refresh();
            });
        }

        public override void OnStart()
        {
            base.OnResume();
            timer.Change(2000, 2000);
            Refresh();
        }

        public override void OnStop()
        {
            base.OnPause();
            timer.Change(-1, -1);
            connParent.RemoveAllViews();
        }

        void Refresh()
        {
            connParent.RemoveAllViews();
            var controller = mainActivity.Service?.Controller;
            if (controller != null) {
                lock (controller.InConnectionsLock) {
                    foreach (var item in controller.InConnections) {
                        AddConn(item);
                    }
                }
            }
        }

        void AddConn(InConnection conn)
        {
            var tv = new TextView(themeWrapper) { Text = conn.ToString() };
            if (conn.ConnectResult?.Ok == true)
                tv.SetBackgroundColor(Color.Argb(30, 0, 255, 0));
            connParent.AddView(tv);
            tv.Dispose();
        }
    }
}
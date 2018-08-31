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
    public class FragmentAdapters : Fragment
    {
        LinearLayout connParent;
        private MainActivity mainActivity;
        private ContextThemeWrapper themeWrapper;

        public FragmentAdapters(MainActivity mainActivity)
        {
            this.mainActivity = mainActivity;
            timer = new Timer(timer_Callback);
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            if (this.Context == null)
                throw new Exception("this.Context == null");

            themeWrapper = new ContextThemeWrapper(this.Context, R.Style.ConnTextView);

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
                var adapters = controller.InAdapters.Union<NaiveSocks.Adapter>(controller.OutAdapters).ToList();
                foreach (var item in adapters) {
                    AddAdapter(item);
                }
            }
        }

        void AddAdapter(NaiveSocks.Adapter ada)
        {
            using (var tv = new TextView(themeWrapper) { Text = ada.ToString() }) {
                connParent.AddView(tv);
            }
            var rw = ada.BytesCountersRW;
            var rwstr = rw.TotalValue.Packets > 0 ? rw.ToString() : "---";
            using (var tv = new TextView(themeWrapper) { Text = rwstr, Gravity = GravityFlags.End }) {
                tv.SetBackgroundColor(Color.Argb(30, 128, 128, 128));
                connParent.AddView(tv);
            }
        }
    }
}
﻿using System;
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
using Android.Content;

namespace NaiveSocksAndroid
{
    public class FragmentAdapters : MyBaseFragment
    {
        LinearLayout connParent;
        private ContextThemeWrapper themeWrapper;

        public FragmentAdapters()
        {
            TimerInterval = 2000;
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            if (this.Context == null)
                throw new Exception("this.Context == null");

            themeWrapper = new ContextThemeWrapper(this.Context, R.Style.ConnTextView);

            var view = inflater.CloneInContext(themeWrapper).Inflate(R.Layout.connections, container, false);

            connParent = view.FindViewById<LinearLayout>(R.Id.connparent);

            return view;
        }

        public override void OnStop()
        {
            base.OnStop();
            connParent.RemoveAllViews();
        }

        protected override void OnUpdate()
        {
            connParent.RemoveAllViews();
            var controller = Controller;
            if (controller != null) {
                var adapters = controller.Adapters;
                if (InfoStrSupport)
                    ChangeInfoStr("[" + adapters.Count + "]");
                foreach (var item in adapters) {
                    AddAdapter(item);
                }
            } else {
                if (InfoStrSupport)
                    ChangeInfoStr("[no controller]");
            }
        }

        StringBuilder sb = new StringBuilder();

        void AddAdapter(NaiveSocks.Adapter ada)
        {
            using (var tv = new TextView(themeWrapper) { Text = ada.ToString() }) {
                connParent.AddView(tv);
            }
            var rw = ada.BytesCountersRW;
            if (ada.CreatedConnections != 0)
                sb.Append("Created=").Append(ada.CreatedConnections);
            if (ada.HandledConnections != 0)
                sb.Append(" Handled=").Append(ada.HandledConnections);
            sb.Append(' ').Append(rw.ToString());
            using (var tv = new TextView(themeWrapper) { Text = sb.ToString(), Gravity = GravityFlags.End }) {
                tv.SetBackgroundColor(Color.Argb(30, 128, 128, 128));
                connParent.AddView(tv);
            }
            sb.Clear();
        }
    }
}
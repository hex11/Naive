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
using System.Threading.Tasks;
using Android.Content;

namespace NaiveSocksAndroid
{
    public class FragmentConnections : Fragment
    {
        LinearLayout connParent;
        List<ItemView> displayingViews = new List<ItemView>();
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
            Clear();
        }

        void Refresh()
        {
            var controller = mainActivity.Service?.Controller;
            if (controller != null) {
                lock (controller.InConnectionsLock) {
                    var conns = controller.InConnections;

                    for (int i = displayingViews.Count - 1; i >= 0; i--) {
                        ItemView view = displayingViews[i];
                        bool found = false;
                        foreach (var item in conns) {
                            if (object.ReferenceEquals(view.Connection, item)) {
                                found = true;
                                break;
                            }
                        }
                        if (!found) {
                            if (view.pendingRemoving) {
                                displayingViews.RemoveAt(i);
                                connParent.RemoveViewAt(i);
                            } else {
                                view.pendingRemoving = true;
                            }
                        }
                    }

                    foreach (var item in conns) {
                        bool found = false;
                        foreach (var view in displayingViews) {
                            if (object.ReferenceEquals(view.Connection, item)) {
                                found = true;
                                break;
                            }
                        }
                        if (!found) {
                            var newView = new ItemView(themeWrapper) { Connection = item };
                            displayingViews.Add(newView);
                            connParent.AddView(newView);
                        }
                    }

                    var sb = new StringBuilder(64);
                    foreach (var view in displayingViews) {
                        view.Update(sb);
                    }
                }
            } else {
                Clear();
            }
        }

        private void Clear()
        {
            connParent.RemoveAllViews();
            displayingViews.Clear();
        }

        class ItemView : LinearLayout, View.IOnLongClickListener
        {
            public ItemView(Context context) : base(context)
            {
                this.Orientation = Orientation.Vertical;

                tv1 = new TextView(context);
                tv2 = new TextView(context);

                tv2.Gravity = GravityFlags.End;
                tv2.SetBackgroundColor(Color.Argb(30, 128, 128, 128));

                this.AddView(tv1);
                this.AddView(tv2);

                this.SetOnLongClickListener(this);
            }

            readonly TextView tv1, tv2;
            public InConnection Connection { get; set; }
            public bool pendingRemoving = false;

            bool stopping = false;

            public void Update(StringBuilder sb)
            {
                var conn = Connection;
                if (conn == null) {
                    tv1.Text = null;
                    this.SetBackgroundColor(Color.Argb(0, 0, 255, 0));
                    tv2.Text = null;
                    return;
                }

                if (conn.ConnectResult?.Ok == true)
                    this.SetBackgroundColor(Color.Argb(conn.IsFinished ? 15 : 30, 0, 255, 0));
                else
                    this.SetBackgroundColor(Color.Argb(conn.IsFinished ? 15 : 30, 255, 255, 0));

                sb.Clear();
                conn.ToString(sb, InConnection.ToStringFlags.None);
                sb.AppendLine();
                sb.Append(conn.GetInfoStr());
                tv1.Text = sb.ToString();

                sb.Clear();
                sb.Append(conn.BytesCountersRW.ToString()).Append(" T=").Append(WebSocket.CurrentTime - conn.CreateTime);
                var adap = conn.ConnectResult?.Adapter;
                if (adap != null)
                    sb.Append(" -> '").Append(adap.Name).Append("'");
                tv2.Text = sb.ToString();
            }

            public bool OnLongClick(View v)
            {
                stopping = true;
                Task.Run(() => {
                    Connection?.Stop();
                    this.Post(() => {
                        Update(new StringBuilder(64));
                    });
                });
                return true;
            }
        }
    }
}
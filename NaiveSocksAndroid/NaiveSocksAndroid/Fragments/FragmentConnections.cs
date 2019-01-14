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
using Android.Animation;

namespace NaiveSocksAndroid
{
    public class FragmentConnections : MyBaseFragment, ICanHandleMenu
    {
        LinearLayout connParent;
        List<ItemView> displayingViews = new List<ItemView>();
        private ContextThemeWrapper themeWrapper;

        private bool sorting = true;
        private bool moreInfo = false;

        public FragmentConnections()
        {
            TimerInterval = 1000;
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            if (this.Context == null)
                throw new Exception("this.Context == null");

            themeWrapper = new ContextThemeWrapper(this.Context, R.Style.ConnTextView);

            var view = inflater.CloneInContext(themeWrapper).Inflate(R.Layout.connections, container, false);

            connParent = view.FindViewById<LinearLayout>(R.Id.connparent);

            if (AppConfig.Current.GetBool(AppConfig.tip_cxn, true)) {
                MainActivity.MakeSnackbar(R.String.long_press_cxn_to_stop, Android.Support.Design.Widget.Snackbar.LengthLong)
                    .SetAction(R.String.got_it, v => {
                        AppConfig.Current.Set(AppConfig.tip_cxn, false);
                    })
                    .Show();
            }

            sorting = AppConfig.Current.GetBool(AppConfig.conn_sort_by_speed, sorting);
            moreInfo = AppConfig.Current.GetBool(AppConfig.conn_more_info, moreInfo);

            return view;
        }

        const int menuItemIdBase = 114514;

        public void OnCreateMenu(IMenu menu)
        {
            {
                var menuItem = menu.Add(200, menuItemIdBase + 1, 200, R.String.conn_sort_by_speed);
                menuItem.SetShowAsAction(ShowAsAction.Never);
                menuItem.SetCheckable(true);
                menuItem.SetChecked(sorting);
            }
            {
                var menuItem = menu.Add(200, menuItemIdBase + 2, 200, R.String.conn_more_info);
                menuItem.SetShowAsAction(ShowAsAction.Never);
                menuItem.SetCheckable(true);
                menuItem.SetChecked(moreInfo);
            }
        }

        public void OnMenuItemSelected(IMenuItem item)
        {
            if (item.ItemId == menuItemIdBase + 1) {
                sorting = !item.IsChecked;
                item.SetChecked(sorting);
                AppConfig.Current.Set(AppConfig.conn_sort_by_speed, sorting);
            } else if (item.ItemId == menuItemIdBase + 2) {
                moreInfo = !item.IsChecked;
                item.SetChecked(moreInfo);
                AppConfig.Current.Set(AppConfig.conn_more_info, moreInfo);
            }
        }

        long lastUpdateTime = -1;

        public override void OnStop()
        {
            base.OnStop();
            Clear();
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            var controller = Controller;
            if (controller != null) {
                lock (controller.InConnectionsLock) {
                    if (InfoStrSupport)
                        ChangeInfoStr(
                            "[" + controller.RunningConnections +
                            "/" + controller.TotalFailedConnections +
                            "/" + controller.TotalHandledConnections +
                            "]");
                    var conns = controller.InConnections;
                    CheckListChanges(conns);
                    UpdateItemViews();
                }
            } else {
                if (InfoStrSupport)
                    ChangeInfoStr("[no controller]");
                Clear();
            }
        }

        ValueAnimator animator;
        List<Action<float>> animateActions = new List<Action<float>>();

        private void UpdateItemViews()
        {
            animateActions.Clear();
            Action<Action<float>> registerAnimation = (action) => {
                if (animator == null) {
                    animator = ValueAnimator.OfFloat(1, 0);
                    int ctr = 0;
                    animator.Update += (s, e) => {
                        var val = (float)e.Animation.AnimatedValue;
                        // skip every second frame to save power:
                        if (val == 0f) { // avoid skipping the last frame
                            ctr = 0; // reset the counter so the first frame of next animation won't be skipped
                        } else if (ctr++ % 2 == 1) {
                            return;
                        }
                        foreach (var item in animateActions) {
                            item(val);
                        }
                    };
                    animator.AnimationEnd += (s, e) => animateActions.Clear();
                }
                animateActions.Add(action);
            };

            var sb = new StringBuilder(64);
            var time = Logging.getRuntime();
            foreach (var view in displayingViews) {
                view.moreInfo = moreInfo;
                view.Update(sb, registerAnimation, time);
            }
            if (sorting) {
                displayingViews.Sort((a, b) => b.KBps - a.KBps);
                int pos = 0;
                foreach (var item in displayingViews) {
                    if (item.KBps < 0)
                        break;
                    connParent.RemoveView(item);
                    connParent.AddView(item, pos++);
                }
            }
            lastUpdateTime = time;
            if (animateActions.Count > 0) {
                animator.Start();
            }
        }

        private void CheckListChanges(List<InConnection> conns)
        {
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
                        connParent.RemoveView(view);
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
                    newView.lastTime = lastUpdateTime;
                    displayingViews.Add(newView);
                    connParent.AddView(newView);
                }
            }
        }

        private void Clear()
        {
            lastUpdateTime = -1;
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

                this.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = 8 };

                this.AddView(tv1);
                this.AddView(tv2);

                this.SetOnLongClickListener(this);
            }

            readonly TextView tv1, tv2;
            public InConnection Connection { get; set; }
            public bool pendingRemoving = false;

            long lastTotalBytes = 0;
            public long lastTime = -1;

            public int KBps;

            public bool moreInfo = true;

            public void Update(StringBuilder sb, Action<Action<float>> registerAnimation, long time)
            {
                var conn = Connection;
                if (conn == null) {
                    tv1.Text = null;
                    this.SetBackgroundColor(Color.Argb(0, 0, 255, 0));
                    tv2.Text = null;
                    return;
                }

                var ctr = conn.BytesCountersRW;
                int KBps = -1;

                if (conn.ConnectResult?.Ok == true) {
                    var totalBytes = ctr.TotalValue.Bytes;

                    int blue;
                    if (totalBytes > 1000) {
                        blue = (int)Math.Log((double)totalBytes / 10, 1.1);
                        if (blue > 255)
                            blue = 255;
                    } else {
                        blue = 0;
                    }
                    var color = Color.Argb((conn.IsFinished ? 30 : 50) + (blue / 5), 0, 255, blue);
                    this.SetBackgroundColor(color);
                    if (registerAnimation != null && lastTime != -1 && lastTotalBytes != totalBytes) {
                        var deltaBytes = totalBytes - lastTotalBytes;
                        var deltaTime = Math.Max(1, time - lastTime);
                        KBps = (int)(deltaBytes * 1000 / 1024 / deltaTime);
                        // deltaBytes / 1024 / (deltaTime / 1000)

                        var jumpValue = 50 + Math.Min(50, KBps);
                        registerAnimation((x) => {
                            var animColor = color;
                            animColor.A += (byte)(jumpValue * x);
                            this.SetBackgroundColor(animColor);
                        });
                    }

                    lastTotalBytes = totalBytes;
                    lastTime = time;
                } else {
                    this.SetBackgroundColor(Color.Argb((conn.IsFinished ? 40 : 80), 255, 255, 0));
                }

                sb.Clear();
                sb.Append('#').Append(conn.Id).Append(' ');
                conn.ToString(sb, InConnection.ToStringFlags.None);
                if (moreInfo) {
                    sb.AppendLine();
                    sb.Append(conn.GetInfoStr());
                }
                tv1.Text = sb.ToString();

                sb.Clear();
                this.KBps = KBps;
                if (KBps > 0) {
                    sb.Append('[').Append(KBps.ToString("N0")).Append(" KB/s] ");
                } else if (KBps == 0) {
                    sb.Append("[<1 KB/s] ");
                }
                if (conn.Sniffer != null) {
                    conn.GetSniffingInfo(sb);
                    sb.Append(' ');
                }
                ctr.ToString(sb, !moreInfo);
                sb.Append(" T=").Append(WebSocket.CurrentTime - conn.CreateTime);
                var adap = conn.ConnectResult?.Adapter ?? conn.RunningHandler;
                if (adap != null)
                    sb.Append(" -> '").Append(adap.Name).Append("'");
                if (moreInfo) {
                    var outStream = conn.ConnectResult?.Stream;
                    if (outStream != null) {
                        sb.Append("\n-> ").Append(outStream.ToString());
                    }
                }
                tv2.Text = sb.ToString();
            }

            public bool OnLongClick(View v)
            {
                Task.Run(() => {
                    Connection?.Stop();
                    this.Post(() => {
                        Update(new StringBuilder(64), null, Logging.getRuntime());
                    });
                });
                return true;
            }
        }
    }
}
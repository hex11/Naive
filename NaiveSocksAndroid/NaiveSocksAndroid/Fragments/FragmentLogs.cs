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
using Android.Content;
using Android.Text;
using Android.Text.Style;
using Android.Support.V7.Widget;
using ClipboardManager = Android.Content.ClipboardManager;

namespace NaiveSocksAndroid
{
    public class FragmentLogs : MyBaseFragment, ICanHandleMenu
    {
        private RecyclerView recycler;
        private LinearLayoutManager linearlayout;
        private MyData dataset;

        private ContextThemeWrapper themeWrapper;

        private int menuItemId_AutoScroll;
        private int menuItemId_DynMargin;
        private bool autoScroll = true;
        private bool eventRegistered = false;

        public bool InHome = false;

        private bool dynamicMargin = true;

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            if (this.Context == null)
                throw new Exception("this.Context == null");

            themeWrapper = new ContextThemeWrapper(this.Context, R.Style.ConnTextView);

            var View = inflater.Inflate(R.Layout.logs, container, false);

            recycler = View.FindViewById<RecyclerView>(R.Id.logparent);
            recycler.SetPadding(0, DpInt(4), 0, DpInt(4));
            linearlayout = new LinearLayoutManager(this.Context) { StackFromEnd = true };
            recycler.SetLayoutManager(linearlayout);
            recycler.SetItemAnimator(null);
            recycler.AddOnScrollListener(new ScrollListenr(this));
            dataset = new MyData() { Context = themeWrapper };
            recycler.SetAdapter(dataset);

            ReadConfig();
            dataset.DynamicMargin = dynamicMargin;

            return View;
        }

        private void ReadConfig()
        {
            dynamicMargin = AppConfig.Current.GetBool(AppConfig.log_dynamic_margin, dynamicMargin);
        }

        private void UpdateConfig()
        {
            AppConfig.Current.Set(AppConfig.log_dynamic_margin, dynamicMargin);
        }

        public override void OnStart()
        {
            base.OnStart();
            if (!eventRegistered) {
                Register();
                UpdateDatasetRange();
                dataset.NotifyDataSetChanged();
            }
        }

        private void UpdateDatasetRange() => UpdateDatasetRange(out _, out _);

        private void UpdateDatasetRange(out int removed, out int appended)
        {
            var oldBegin = dataset.IndexBegin;
            var oldEnd = dataset.IndexEnd;
            Logging.GetLogsStat(out var begin, out var count);
            dataset.IndexBegin = begin;
            dataset.IndexEnd = begin + count;
            removed = dataset.IndexBegin - oldBegin;
            appended = dataset.IndexEnd - oldEnd;
            if (InfoStrSupport) {
                ChangeInfoStr("[" + dataset.IndexBegin + " - " + (dataset.IndexEnd - 1) + "]");
            }
        }

        public override void OnStop()
        {
            base.OnStop();
            if (autoScroll) {
                Unregister();
                ClearDataset();
            }
        }

        public override void OnDestroyView()
        {
            base.OnDestroyView();
            if (eventRegistered) {
                Unregister();
            }
        }

        private void ClearDataset()
        {
            dataset.IndexBegin = dataset.IndexEnd = 0;
            dataset.NotifyDataSetChanged();
        }

        private void Register()
        {
            Logging.Logged += Logging_Logged;
            eventRegistered = true;
        }

        private void Unregister()
        {
            Logging.Logged -= Logging_Logged;
            eventRegistered = false;
        }

        bool posting = false;

        private void Logging_Logged(Logging.Log log)
        {
            try {
                if (!posting) {
                    posting = true;
                    recycler.PostDelayed(() => {
                        posting = false;
                        if (!this.IsAdded)
                            return;
                        UpdateDatasetRange(out var removed, out var appended);
                        if (removed > 0) {
                            dataset.NotifyItemRangeRemoved(0, removed);
                        }
                        if (appended > 0) {
                            dataset.NotifyItemRangeInserted(dataset.Count - appended, appended);
                            if (autoScroll) {
                                AutoScroll();
                            }
                        }
                    }, 50);
                }
            } catch (Exception e) {
                Unregister();
                Logging.exception(e, Logging.Level.Error, "Logging_Logged exception");
            }
        }

        private void AutoScroll()
        {
            recycler.SmoothScrollToPosition(dataset.Count);
        }

        private static Color? getColorFromLevel(Logging.Level level)
        {
            switch (level) {
                case Logging.Level.None:
                case Logging.Level.Debug:
                    return Color.Argb(40, 0, 0, 0);
                case Logging.Level.Info:
                    return Color.Argb(60, 0, 255, 0);
                case Logging.Level.Warning:
                    return Color.Argb(80, 255, 255, 0);
                case Logging.Level.Error:
                default:
                    return Color.Argb(50, 255, 0, 0);
            }
        }

        bool isAtMostBottom = true;

        public void OnCreateMenu(IMenu menu)
        {
            if (!isAtMostBottom) {
                var menuItem = menu.Add(0, menuItemId_AutoScroll = 114514, 99, R.String.autoscroll);
                menuItem.SetShowAsAction(ShowAsAction.Always | ShowAsAction.WithText);
            }
            {
                var menuItem = menu.Add(200, menuItemId_DynMargin = 114514 + 1, 200, R.String.log_dynamic_margin);
                menuItem.SetShowAsAction(ShowAsAction.Never);
                menuItem.SetCheckable(true);
                menuItem.SetChecked(dynamicMargin);
            }
        }

        public void OnMenuItemSelected(IMenuItem item)
        {
            if (item.ItemId == menuItemId_AutoScroll) {
                autoScroll = true;
                AutoScroll();
            } else if (item.ItemId == menuItemId_DynMargin) {
                dynamicMargin ^= true;
                UpdateConfig();
                dataset.DynamicMargin = dynamicMargin;
                dataset.NotifyDataSetChanged();
                MainActivity.InvalidateOptionsMenu();
            } else {
                Logging.warning($"FragmentLogs.OnMenuItemSelected: unknown menuitem id={item.ItemId}, title={item.TitleFormatted}");
            }
        }

        class ScrollListenr : RecyclerView.OnScrollListener
        {
            public ScrollListenr(FragmentLogs fragment)
            {
                Fragment = fragment;
            }

            public FragmentLogs Fragment { get; }

            private int oldState;

            public override void OnScrollStateChanged(RecyclerView recyclerView, int newState)
            {
                var oldAMB = Fragment.isAtMostBottom;
                if (newState == RecyclerView.ScrollStateDragging) {
                    Fragment.isAtMostBottom = false;
                    Fragment.autoScroll = false;
                } else if (newState == RecyclerView.ScrollStateIdle && !Fragment.isAtMostBottom) {
                    var pos = Fragment.linearlayout.FindLastCompletelyVisibleItemPosition();
                    if (pos == Fragment.dataset.Count - 1) {
                        Fragment.isAtMostBottom = true;
                        Fragment.autoScroll = true;
                    } else {
                        Fragment.isAtMostBottom = false;
                    }
                }
                if (!Fragment.InHome && oldAMB != Fragment.isAtMostBottom) {
                    Fragment.MainActivity.InvalidateOptionsMenu();
                }
                oldState = newState;
                base.OnScrollStateChanged(recyclerView, newState);
            }
        }

        class MyData : RecyclerView.Adapter
        {
            public Context Context;

            public int IndexBegin, IndexEnd; // excluding IndexEnd

            public int Count => IndexEnd - IndexBegin;

            public override int ItemCount => Count;

            public bool DynamicMargin;

            public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
            {
                return new MyViewHolder(this);
            }

            public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
            {
                var myHolder = (MyViewHolder)holder;
                int index = IndexBegin + position;
                var log = Logging.TryGetLog(index) ?? new Logging.Log() {
                    level = Logging.Level.None,
                    runningTime = 0,
                    text = " (Failed to fetch log index " + position + " (deleted))"
                };
                myHolder.Render(log, Logging.TryGetLog(index - 1, false));
            }

            public override void OnViewRecycled(Java.Lang.Object holder)
            {
                var myHolder = (MyViewHolder)holder;
                myHolder.Reset();
            }
        }

        class MyViewHolder : RecyclerView.ViewHolder, View.IOnLongClickListener
        {
            MyData MyData;

            float dip;

            public MyViewHolder(MyData myData) : base(new TextView(myData.Context))
            {
                MyData = myData;
                dip = textView.Resources.DisplayMetrics.Density;
                textView.SetPadding((int)(dip * 4), 0, (int)(dip * 4), 0);
                textView.SetOnLongClickListener(this);
            }

            SpannableStringBuilder ssb = new SpannableStringBuilder();
            TextView textView => (TextView)ItemView;

            public void Render(Logging.Log log, Logging.Log? prevLog)
            {
                string timestamp = "[" + log.time.ToString("HH:mm:ss.fff") + " " + log.levelStr + "]";

                Color color = getColorFromLevel(log.level) ?? Color.LightGray;
                var color2 = color;
                color2.A = (byte)(color2.A / 2);
                color2 = AlphaComposite(Color.White, color2);
                var color1 = AlphaComposite(color2, color);


                ssb.Append(timestamp, new BackgroundColorSpan(color1), SpanTypes.ExclusiveExclusive);
                ssb.Append(log.text);
                textView.SetBackgroundColor(color2);
                textView.SetText(ssb, TextView.BufferType.Spannable);
                ssb.Clear();
                int topMargin;
                if (MyData.DynamicMargin == false || prevLog == null) {
                    topMargin = 0;
                } else {
                    var delta = log.runningTime - prevLog.Value.runningTime;
                    if (delta < 10) {
                        topMargin = 0;
                    } else {
                        topMargin = (int)(Math.Log(delta / 10, 1.5) * 1.5 * dip);
                    }
                }
                textView.LayoutParameters = new LinearLayout.LayoutParams(-1, -2) { TopMargin = topMargin };
            }

            /// <summary>
            /// A over B
            /// </summary>
            private static Color AlphaComposite(Color b, Color a)
            {
                var aa = a.A / 255f;
                var aar = (255 - a.A) * b.A / (255 * 255f);
                var alpha = aa + aar;
                var fac = 1 / alpha;
                return new Color(
                    r: (byte)((a.R * aa + b.R * aar) * fac),
                    g: (byte)((a.G * aa + b.G * aar) * fac),
                    b: (byte)((a.B * aa + b.B * aar) * fac),
                    a: (byte)(alpha * 255)
                    );
            }

            public void Reset()
            {
                textView.Text = null;
            }

            public bool OnLongClick(View v)
            {
                var cs = textView.Context.GetSystemService(Context.ClipboardService) as ClipboardManager;
                cs.PrimaryClip = ClipData.NewPlainText(new Java.Lang.String("log"), textView.TextFormatted);
                Toast.MakeText(textView.Context, R.String.copied, ToastLength.Short).Show();
                return true;
            }
        }
    }
}
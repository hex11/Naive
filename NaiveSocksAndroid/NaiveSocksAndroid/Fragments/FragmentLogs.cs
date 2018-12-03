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
        private MyData dataset;

        private ContextThemeWrapper themeWrapper;

        private int menuItemId;
        private bool autoScroll = true;
        private bool eventRegistered = false;

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            if (this.Context == null)
                throw new Exception("this.Context == null");

            themeWrapper = new ContextThemeWrapper(this.Context, R.Style.ConnTextView);

            var View = inflater.Inflate(R.Layout.logs, container, false);

            recycler = View.FindViewById<RecyclerView>(R.Id.logparent);
            recycler.SetLayoutManager(new LinearLayoutManager(this.Context) { StackFromEnd = true });
            recycler.SetItemAnimator(null);
            dataset = new MyData() { Context = themeWrapper };
            recycler.SetAdapter(dataset);

            return View;
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
            MainActivity.Title = Resources.GetString(R.String.logs) + " [" + dataset.IndexBegin + " - " + (dataset.IndexEnd - 1) + "]";
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
                            int count = dataset.IndexEnd - dataset.IndexBegin;
                            dataset.NotifyItemRangeInserted(count - appended, appended);
                            if (autoScroll) {
                                recycler.SmoothScrollToPosition(count - 1);
                            }
                        }
                    }, 50);
                }
            } catch (Exception e) {
                Unregister();
                Logging.exception(e, Logging.Level.Error, "Logging_Logged exception");
            }
        }

        private static Color? getColorFromLevel(Logging.Level level)
        {
            switch (level) {
                case Logging.Level.None:
                case Logging.Level.Debug:
                    return Color.Argb(30, 0, 0, 0);
                case Logging.Level.Info:
                    return Color.Argb(50, 0, 255, 0);
                case Logging.Level.Warning:
                    return Color.Argb(50, 255, 255, 0);
                case Logging.Level.Error:
                default:
                    return Color.Argb(50, 255, 0, 0);
            }
        }

        public void OnCreateMenu(IMenu menu)
        {
            var menuItem = menu.Add(0, menuItemId = 114514, 0, R.String.autoscroll);
            menuItem.SetCheckable(true);
            menuItem.SetShowAsAction(ShowAsAction.Always | ShowAsAction.WithText);
            menuItem.SetChecked(autoScroll);
        }

        public void OnMenuItemSelected(IMenuItem item)
        {
            if (item.ItemId == menuItemId) {
                autoScroll = !item.IsChecked;
                item.SetChecked(autoScroll);
                MainActivity.MakeSnackbar(MainActivity.FormatSwitchString(this.Context, R.String.autoscroll, autoScroll),
                    Android.Support.Design.Widget.Snackbar.LengthShort).Show();
                //mainActivity.InvalidateOptionsMenu();
            } else {
                Logging.warning($"FragmentLogs.OnMenuItemSelected: unknown menuitem id={item.ItemId}, title={item.TitleFormatted}");
            }
        }

        class MyData : RecyclerView.Adapter
        {
            public Context Context;

            public int IndexBegin, IndexEnd; // excluding IndexEnd

            public override int ItemCount => IndexEnd - IndexBegin;

            public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
            {
                return new MyViewHolder(Context);
            }

            public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
            {
                var myHolder = (MyViewHolder)holder;
                var log = Logging.TryGetLog(IndexBegin + position) ?? new Logging.Log() {
                    level = Logging.Level.None,
                    runningTime = 0,
                    text = " (Failed to fetch log index " + position + " (deleted))"
                };
                myHolder.Render(log);
            }

            public override void OnViewRecycled(Java.Lang.Object holder)
            {
                var myHolder = (MyViewHolder)holder;
                myHolder.Reset();
            }
        }

        class MyViewHolder : RecyclerView.ViewHolder, View.IOnLongClickListener
        {
            public MyViewHolder(Context context) : base(new TextView(context))
            {
                textView.SetOnLongClickListener(this);
            }

            SpannableStringBuilder ssb = new SpannableStringBuilder();
            TextView textView => (TextView)ItemView;

            public void Render(Logging.Log log)
            {
                Color color = getColorFromLevel(log.level) ?? Color.LightGray;
                string timestamp = "[" + log.time.ToString("HH:mm:ss.fff") + " " + log.levelStr + "]";
                ssb.Append(timestamp, new BackgroundColorSpan(color), SpanTypes.ExclusiveExclusive);
                ssb.Append(log.text);
                textView.SetText(ssb, TextView.BufferType.Spannable);
                ssb.Clear();
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
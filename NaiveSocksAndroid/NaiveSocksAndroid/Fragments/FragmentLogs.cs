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
            dataset = new MyData() { Context = themeWrapper };
            recycler.SetAdapter(dataset);

            return View;
        }

        public override void OnStart()
        {
            base.OnStart();
            if (!eventRegistered) {
                Register();
                var logs = Logging.getLogsHistoryArray();
                if (logs.Length > 0) {
                    dataset.Logs.AddRange(logs);
                    dataset.Logs.Add(Logging.CreateLog(Logging.Level.None, "===== end of history log ====="));
                    dataset.NotifyDataSetChanged();
                }
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
            dataset.Logs.Clear();
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

        private void Logging_Logged(Logging.Log log)
        {
            recycler.Post(() => putLog(log, autoScroll));
        }

        private void putLog(Logging.Log log, bool autoScroll)
        {
            dataset.Logs.Add(log);
            int pos = dataset.Logs.Count - 1;
            dataset.NotifyItemInserted(pos);
            if (autoScroll) {
                recycler.SmoothScrollToPosition(pos);
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
            public List<Logging.Log> Logs = new List<Logging.Log>();

            public Context Context;

            public override int ItemCount => Logs.Count;

            public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
            {
                return new MyViewHolder(Context);
            }

            public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
            {
                var myHolder = (MyViewHolder)holder;
                var log = Logs[position];
                myHolder.Render(log);
            }

            public override void OnViewRecycled(Java.Lang.Object holder)
            {
                var myHolder = (MyViewHolder)holder;
                myHolder.Reset();
            }
        }

        class MyViewHolder : RecyclerView.ViewHolder
        {
            public MyViewHolder(Context context) : base(new TextView(context))
            {
            }

            SpannableStringBuilder ssb = new SpannableStringBuilder();
            TextView textView => (TextView)ItemView;

            public void Render(Logging.Log log)
            {
                Color color = getColorFromLevel(log.level) ?? Color.Black;
                string timestamp = "[" + log.time.ToString("HH:mm:ss.fff") + " " + log.levelStr + "]";
                ssb.Clear();
                ssb.Append(timestamp, new BackgroundColorSpan(color), SpanTypes.ExclusiveExclusive);
                ssb.Append(log.text);
                textView.SetText(ssb, TextView.BufferType.Spannable);
            }

            public void Reset()
            {
                ssb.Clear();
                textView.SetText(ssb, TextView.BufferType.Spannable);
            }
        }
    }
}
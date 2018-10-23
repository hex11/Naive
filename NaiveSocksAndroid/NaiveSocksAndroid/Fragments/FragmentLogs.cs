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

namespace NaiveSocksAndroid
{
    public class FragmentLogs : MyBaseFragment, ICanHandleMenu
    {
        private TextView textView;
        private NestedScrollView outputParentScroll;
        private SpannableStringBuilder ssb;

        private int menuItemId;
        private bool autoScroll = true;

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            if (this.Context == null)
                throw new Exception("this.Context == null");

            var View = inflater.Inflate(R.Layout.logs, container, false);

            textView = View.FindViewById<TextView>(R.Id.logparent);
            outputParentScroll = View.FindViewById<NestedScrollView>(R.Id.logparentScroll);

            ssb = new SpannableStringBuilder();

            return View;
        }

        public override void OnStart()
        {
            base.OnStart();
            Logging.Logged += Logging_Logged;
            var logs = Logging.getLogsHistoryArray();
            if (logs.Length > 0) {
                for (int i = 0; i < logs.Length; i++) {
                    putLog(logs[i], false);
                }
                putText("========== end of log history ==========\n", true);
            }
        }

        public override void OnStop()
        {
            base.OnStop();
            Logging.Logged -= Logging_Logged;
            ssb.Clear();
            textView.SetText(ssb, TextView.BufferType.Spannable);
        }

        private void Logging_Logged(Logging.Log log)
        {
            textView.Post(() => putLog(log, autoScroll));
        }

        private void putLog(Logging.Log log, bool autoScroll)
        {
            putText("[" + log.time.ToString("HH:mm:ss.fff") + " " + log.levelStr + "]", false, getColorFromLevel(log.level));
            putText(log.text, false);
            putText("\n", autoScroll);
        }

        private Color? getColorFromLevel(Logging.Level level)
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

        bool opsPending;
        bool scrollPending;

        private void putText(string text, bool autoScroll = true, Android.Graphics.Color? color = null)
        {
            if (color != null) {
                ssb.Append(text, new BackgroundColorSpan(color.Value), SpanTypes.ExclusiveExclusive);
            } else {
                ssb.Append(text);
            }
            scrollPending |= autoScroll;
            if (!opsPending) {
                opsPending = true;
                outputParentScroll.PostDelayed(() => {
                    opsPending = false;
                    textView.SetText(ssb, TextView.BufferType.Spannable);
                    textView.RequestLayout();
                    if (scrollPending) {
                        scrollPending = false;
                        outputParentScroll.Post(() => {
                            outputParentScroll.FullScroll((int)FocusSearchDirection.Down);
                        });
                    }
                }, 10);
            }
        }

        public void OnCreateMenu(IMenu menu)
        {
            var menuItem = menu.Add(0, menuItemId = 114514, 0, "Autoscroll");
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
    }
}
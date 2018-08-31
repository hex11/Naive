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

namespace NaiveSocksAndroid
{
    public class FragmentLogs : Fragment, ICanHandleMenu
    {
        private ContextThemeWrapper logThemeWrapper;
        private LinearLayout outputParent;
        private NestedScrollView outputParentScroll;

        private readonly MainActivity mainActivity;
        private int menuItemId;
        private bool autoScroll = true;

        public FragmentLogs(MainActivity mainActivity)
        {
            this.mainActivity = mainActivity;
        }

        public override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            if (this.Context == null)
                throw new Exception("this.Context == null");

            logThemeWrapper = new ContextThemeWrapper(this.Context, R.Style.LogTextView);

            var View = inflater.Inflate(R.Layout.logs, container, false);

            outputParent = View.FindViewById<LinearLayout>(R.Id.logparent);
            outputParentScroll = View.FindViewById<NestedScrollView>(R.Id.logparentScroll);

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
                putText("========== end of log history ==========", true);
            }
        }

        public override void OnStop()
        {
            base.OnStop();
            Logging.Logged -= Logging_Logged;
            outputParent.RemoveAllViews();
        }

        private void Logging_Logged(Logging.Log log)
        {
            outputParent.Post(() => putLog(log, autoScroll));
        }

        private void putLog(Logging.Log log, bool autoScroll)
        {
            putText("[" + log.time.ToString("HH:mm:ss.fff") + " " + log.levelStr + "] " + log.text, autoScroll, getColorFromLevel(log.level));
        }

        private Color? getColorFromLevel(Logging.Level level)
        {
            switch (level) {
            case Logging.Level.None:
            case Logging.Level.Debug:
                return null;
            case Logging.Level.Info:
                return Color.Argb(30, 0, 255, 0);
            case Logging.Level.Warning:
                return Color.Argb(30, 255, 255, 0);
            case Logging.Level.Error:
            default:
                return Color.Argb(30, 255, 0, 0);
            }
        }

        private void putText(string text, bool autoScroll = true, Android.Graphics.Color? color = null)
        {
            var tv = new TextView(logThemeWrapper);
            tv.Text = text;
            if (color != null)
                tv.SetBackgroundColor(color.Value);
            //autoScroll = autoScroll && !outputParentScroll.CanScrollVertically(0);
            outputParent.AddView(tv);
            tv.Dispose();
            if (autoScroll) {
                outputParentScroll.Post(() => outputParentScroll.FullScroll((int)FocusSearchDirection.Down));
            }
        }

        public void OnCreateMenu(IMenu menu)
        {
            var menuItem = mainActivity.toolbar.Menu.Add(0, menuItemId = 114514, 0, "Autoscroll");
            menuItem.SetCheckable(true);
            menuItem.SetShowAsAction(ShowAsAction.Always | ShowAsAction.WithText);
            menuItem.SetChecked(autoScroll);
        }

        public void OnMenuItemSelected(IMenuItem item)
        {
            if (item.ItemId == menuItemId) {
                autoScroll = !item.IsChecked;
                item.SetChecked(autoScroll);
                mainActivity.MakeSnackbar("Autoscroll is " + (autoScroll ? "enabled" : "disabled"),
                    Android.Support.Design.Widget.Snackbar.LengthShort).Show();
                //mainActivity.InvalidateOptionsMenu();
            } else {
                Logging.warning($"FragmentLogs.OnMenuItemSelected: unknown menuitem id={item.ItemId}, title={item.TitleFormatted}");
            }
        }
    }
}
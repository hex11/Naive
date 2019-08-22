using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Android.Support.V4.App;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using NaiveSocks;
using Naive.HttpSvr;
using Android.Content;

namespace NaiveSocksAndroid
{
    public class FragmentHome : MyBaseFragment
    {
        public FragmentHome()
        {
            TimerInterval = 2000;
        }

        private LinearLayout rootLayout;

        private LinearLayout itemsLayout;

        private int pad;

        StringBuilder _sb = new StringBuilder();
        List<KeyValuePair<TextView, Action<StringBuilder>>> items = new List<KeyValuePair<TextView, Action<StringBuilder>>>();

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            var match_parent = ViewGroup.LayoutParams.MatchParent;

            pad = DpInt(4);

            rootLayout = new LinearLayout(Context);
            rootLayout.Orientation = Orientation.Vertical;
            rootLayout.SetPadding(pad, 0, pad, 0);

            var itemsOuter = new ScrollView(Context);
            itemsOuter.LayoutParameters = new LinearLayout.LayoutParams(match_parent, 0, 1);

            itemsLayout = new LinearLayout(Context);
            itemsLayout.Orientation = Orientation.Vertical;
            itemsLayout.SetPadding(0, pad, 0, pad);

            itemsOuter.AddView(itemsLayout);
            rootLayout.AddView(itemsOuter);

            CreateDataItems();

            TextView logTitleView = CreateDataTitleView("Log");
            rootLayout.AddView(logTitleView);
            var frame = new FrameLayout(Context);
            frame.Id = 233;
            frame.LayoutParameters = new LinearLayout.LayoutParams(match_parent, 0, 1);
            rootLayout.AddView(frame);
            var logView = new FragmentLogs() { InHome = true };
            logView.InfoStrChanged += (str) => {
                if (str == null)
                    logTitleView.Text = "Log";
                else
                    logTitleView.Text = "Log " + str;
            };
            ChildFragmentManager.BeginTransaction()
                .Replace(frame.Id, logView)
                .Commit();

            return rootLayout;
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            Refresh();
        }

        private void Refresh()
        {
            foreach (var item in items) {
                item.Value(_sb);
                item.Key.Text = _sb.ToString();
                _sb.Clear();
            }
        }

        private void CreateDataItems()
        {
            System.Diagnostics.Process proc = null;

            NewDataItem("Memory", sb => {
                proc = System.Diagnostics.Process.GetCurrentProcess();
                sb.Append("GC Memory: ").Append((GC.GetTotalMemory(false) / 1024).ToString("N0")).Append(" KB")
                    .Append(" / RSS: ").Append((proc.WorkingSet64 / 1024).ToString("N0")).Append(" KB");
            });

            NewDataItem("GC Counters", sb => {
                int max = GC.MaxGeneration + 1;
                for (int i = 0; i < max; i++) {
                    if (i != 0)
                        sb.Append(" / ");
                    sb.Append("Gen").Append(i).Append(": ").Append(GC.CollectionCount(i));
                }
            });

            NewDataItem("Time", sb => {
                var cpuTime = Android.OS.Process.ElapsedCpuTime;
                var procTimeMs = SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;
                var procTime = TimeSpan.FromMilliseconds(procTimeMs);
                sb.Append("Process running: ").Append(procTime.ToString(@"d\.hh\:mm\:ss"));
                sb.Append(" / CPU used: ").Append(cpuTime.ToString("N0")).Append(" ms")
                    .Append(" (").Append(((float)cpuTime / procTimeMs * 100).ToString("N2")).Append("%)");
                var controller = Controller;
                if (controller != null) {
                    var controllerTime = DateTime.UtcNow - controller.LastStart;
                    if (controller.StartTimes > 1 || (controllerTime - procTime) > TimeSpan.FromSeconds(30)) {
                        sb.Append("\nController running: ").Append(controllerTime.ToString(@"d\.hh\:mm\:ss"));
                    }
                }
            });

            NewDataItem("Threads", sb => {
                ThreadPool.GetMinThreads(out var workersMin, out var portsMin);
                ThreadPool.GetMaxThreads(out var workersMax, out var portsMax);
                sb.Append(proc.Threads.Count).Append(" (workers: ").Append(workersMin).Append("-").Append(workersMax)
                    .Append(", ports: ").Append(portsMin).Append("-").Append(portsMax).Append(")");
            });

            NewDataItem("Connections", sb => {
                if (Controller != null) {
                    sb.Append(Controller.RunningConnections).Append(" running, ")
                        .Append(Controller.TotalFailedConnections).Append(" failed, ")
                        .Append(Controller.TotalHandledConnections).Append(" handled");
                } else {
                    sb.Append("(controller is not running)");
                }
            });

            NewDataItem("Relay Counters", sb => {
                sb.Append(MyStream.TotalCopiedPackets.ToString("N0")).Append(" packets, ").Append(MyStream.TotalCopiedBytes.ToString("N0")).Append(" bytes");
            });

            NewDataItem("Socket Counters", sb => {
                sb.Append(SocketStream.GlobalCounters.StringRead).AppendLine();
                sb.Append(SocketStream.GlobalCounters.StringWrite);
            });
        }

        void NewDataItem(string title, Action<StringBuilder> contentFunc)
        {
            TextView t = CreateDataTitleView(title);

            var c = new TextView(Context);
            c.SetPadding(pad * 4, pad, pad * 2, pad);

            itemsLayout.AddView(t);
            itemsLayout.AddView(c);

            items.Add(new KeyValuePair<TextView, Action<StringBuilder>>(c, contentFunc));
        }

        private TextView CreateDataTitleView(string title)
        {
            var t = new TextView(Context);
            t.SetPadding(pad * 2, pad, pad * 2, pad);
            t.SetBackgroundColor(new Android.Graphics.Color(unchecked((int)0xFF6FBFFF)));
            t.SetTextColor(Android.Graphics.Color.Black);
            t.Text = title;
            return t;
        }
    }
}
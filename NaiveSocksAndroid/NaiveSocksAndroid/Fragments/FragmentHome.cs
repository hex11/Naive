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

        private LinearLayout linearLayout;

        StringBuilder _sb = new StringBuilder();
        List<KeyValuePair<TextView, Action<StringBuilder>>> items = new List<KeyValuePair<TextView, Action<StringBuilder>>>();

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            linearLayout = new LinearLayout(Context);
            linearLayout.Orientation = Orientation.Vertical;
            linearLayout.SetPadding(8, 8, 8, 8);

            CreateDataItems();

            TextView logTitleView = CreateDataTitleView("Log");
            linearLayout.AddView(logTitleView);
            var frame = new FrameLayout(Context);
            frame.Id = 233;
            var match_parent = ViewGroup.LayoutParams.MatchParent;
            frame.LayoutParameters = new LinearLayout.LayoutParams(match_parent, 0, 1);
            linearLayout.AddView(frame);
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

            return linearLayout;
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
                sb.Append("GC Memory: ").Append(GC.GetTotalMemory(false).ToString("N0"))
                    .Append(" / WS: ").Append(proc.WorkingSet64.ToString("N0"));
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
                var runTime = SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;
                sb.Append("Process running: ").Append(TimeSpan.FromMilliseconds(runTime).ToString(@"d\.hh\:mm\:ss"));
                sb.Append(" / CPU used: ").Append(cpuTime.ToString("N0")).Append(" ms")
                    .Append(" (").Append(((float)cpuTime / runTime * 100).ToString("N2")).Append("%)");
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
                        .Append(Controller.TotalHandledConnections).Append(" handled, ")
                        .Append(Controller.TotalFailedConnections).Append(" failed");
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
            c.SetPadding(32, 8, 16, 8);

            linearLayout.AddView(t);
            linearLayout.AddView(c);

            items.Add(new KeyValuePair<TextView, Action<StringBuilder>>(c, contentFunc));
        }

        private TextView CreateDataTitleView(string title)
        {
            var t = new TextView(Context);
            t.SetPadding(16, 8, 16, 8);
            t.SetBackgroundColor(new Android.Graphics.Color(unchecked((int)0xFF6FBFFF)));
            t.SetTextColor(Android.Graphics.Color.Black);
            t.Text = title;
            return t;
        }
    }
}
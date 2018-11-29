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
            linearLayout.SetPadding(16, 16, 16, 16);
            CreateDataItems();
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

            NewDataItem("TotalMemory", sb => {
                proc = System.Diagnostics.Process.GetCurrentProcess();
                sb.Append(GC.GetTotalMemory(false).ToString("N0"));
            });
            NewDataItem("CollectionCount", sb => sb.Append(string.Join(
                ", ",
                Enumerable.Range(0, GC.MaxGeneration + 1)
                    .Select(x => $"({x}) {GC.CollectionCount(x)}"))));

            NewDataItem("WorkingSet", sb => sb.Append(proc.WorkingSet64.ToString("N0")));

            NewDataItem("PrivateMemory", sb => sb.Append(proc.PrivateMemorySize64.ToString("N0")));

            NewDataItem("CPU Time", sb => {
                var cpuTime = Android.OS.Process.ElapsedCpuTime;
                var runTime = SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;
                sb.Append(cpuTime.ToString("N0")).Append(" ms")
                    .Append(" (").Append(((float)cpuTime / runTime * 100).ToString("N2")).Append("% since process started)");
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

            NewDataItem("MyStream Copied", sb => {
                sb.Append(MyStream.TotalCopiedPackets.ToString("N0")).Append(" packets, ").Append(MyStream.TotalCopiedBytes.ToString("N0")).Append(" bytes");
            });

            NewDataItem("SocketStream Counters", sb => {
                sb.Append(SocketStream.GlobalCounters.StringRead).AppendLine();
                sb.Append(SocketStream.GlobalCounters.StringWrite);
            });
        }

        void NewDataItem(string title, Action<StringBuilder> contentFunc)
        {
            var t = new TextView(Context);
            t.SetPadding(16, 8, 16, 8);
            t.SetBackgroundColor(new Android.Graphics.Color(unchecked((int)0xFF6FBFFF)));
            t.Text = title;

            var c = new TextView(Context);
            c.SetPadding(32, 8, 16, 8);

            linearLayout.AddView(t);
            linearLayout.AddView(c);

            items.Add(new KeyValuePair<TextView, Action<StringBuilder>>(c, contentFunc));
        }
    }
}
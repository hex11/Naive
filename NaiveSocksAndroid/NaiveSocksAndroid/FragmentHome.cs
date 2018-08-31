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
using Java.Lang;
using NaiveSocks;
using Naive.HttpSvr;

namespace NaiveSocksAndroid
{
    public class FragmentHome : Fragment
    {
        public FragmentHome(MainActivity mainActivity)
        {
            this.mainActivity = mainActivity;
            timer = new Timer(timer_Callback);
        }

        public override void OnStart()
        {
            base.OnResume();
            timer.Change(1000, 1000);
            Refresh();
        }

        public override void OnStop()
        {
            base.OnPause();
            timer.Change(-1, -1);
        }

        private readonly MainActivity mainActivity;
        private readonly Timer timer;
        private Runnable timerRunnable;
        private TextView textView;

        private void timer_Callback(object state)
        {
            if (timerRunnable != null)
                View.Post(timerRunnable);
        }

        private void Refresh()
        {
            var sb = new System.Text.StringBuilder();
            MakeText(sb);
            textView.SetText(sb.ToString(), TextView.BufferType.Normal);
        }

        private void MakeText(System.Text.StringBuilder sb)
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var controller = mainActivity?.Service?.Controller;

            sb.Append("TotalMemory: ").AppendLine(GC.GetTotalMemory(false).ToString("N0"));

            sb.Append("CollectionCount: ").AppendLine(string.Join(
                ", ",
                Enumerable.Range(0, GC.MaxGeneration + 1)
                    .Select(x => $"({x}) {GC.CollectionCount(x)}")));

            sb.Append("WorkingSet: ").AppendLine(proc.WorkingSet64.ToString("N0"));

            sb.Append("PrivateMemory: ").AppendLine(proc.PrivateMemorySize64.ToString("N0"));

            var cpuTime = proc.TotalProcessorTime.TotalMilliseconds;
            sb.Append("CPUTime: ").Append(cpuTime.ToString("N0")).Append(" ms")
                .Append(" (").Append((cpuTime / Logging.Runtime * 100).ToString("N2")).Append("% since process started");

            ThreadPool.GetMinThreads(out var workersMin, out var portsMin);
            ThreadPool.GetMaxThreads(out var workersMax, out var portsMax);
            sb.Append("Threads: ").Append(proc.Threads.Count).Append(" (workers: ").Append(workersMin).Append("-").Append(workersMax).Append(", ports: ").Append(portsMin).Append("-").Append(portsMax).AppendLine(")");

            sb.Append("Connections: ");
            if (controller != null) {
                sb.Append(controller.RunningConnections).Append(" running, ")
                    .Append(controller.TotalHandledConnections).Append(" handled, ")
                    .Append(controller.TotalFailedConnections).AppendLine(" failed");
            } else {
                sb.AppendLine("(controller is not running)");
            }

            sb.Append("MyStream Copied: ").Append(MyStream.TotalCopiedPackets.ToString("N0")).Append(" packets, ").Append(MyStream.TotalCopiedBytes.ToString("N0")).AppendLine(" bytes");

            sb.AppendLine("SocketStream:");
            sb.Append("    ").Append(SocketStream.GlobalCounters.StringRead).AppendLine(";");
            sb.Append("    ").Append(SocketStream.GlobalCounters.StringWrite).AppendLine(".");
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            textView = new TextView(this.Context);
            textView.SetPadding(16, 16, 16, 16);
            timerRunnable = new Runnable(() => {
                if (this.IsVisible == false)
                    return;
                Refresh();
            });
            return textView;
        }
    }
}
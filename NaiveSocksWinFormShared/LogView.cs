using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NaiveSocks.WinForm
{
    public class LogView : ListViewDoubleBuffered
    {
        public class Tab : TabBase
        {
            public override string Name => "Log";
            public LogView LogView = new LogView();
            public override Control View => LogView;

            protected override void OnShow()
            {
                base.OnShow();
                LogView.ScrollToBottom();
            }
        }

        public LogView()
        {
            this.View = View.Details;
            this.FullRowSelect = true;
            this.BorderStyle = BorderStyle.None;
            this.ShowItemToolTips = true;
            this.Columns.Add("Time & Level", 180);
            this.Columns.Add("Text", 470);
            updateDelegate = new SendOrPostCallback(UpdateSync);
            syncContext = SynchronizationContext.Current;

            VirtualMode = true;
            RetrieveVirtualItem += LogView_RetrieveVirtualItem;
            Logging.Logged += Logging_Logged;
            UpdateListSize();
        }

        private void LogView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var log = Logging.TryGetLog(e.ItemIndex);
            e.Item = ItemFromLog(log ?? new Logging.Log { level = Logging.Level.Error, _timestamp = "!(log rotated)!" });
        }

        SynchronizationContext syncContext;

        private void Logging_Logged(Logging.Log log)
        {
            PendingUpdate();
        }

        private void PendingUpdate()
        {
            lock (updateDelegate) {
                if (!pending) {
                    pending = true;
                    syncContext.Post(updateDelegate, null);
                }
            }
        }

        bool pending;

        readonly SendOrPostCallback updateDelegate;
        private void UpdateSync(object state)
        {
            lock (updateDelegate) {
                pending = false;
                UpdateListSize();
            }
            ScrollToBottom();
        }

        void UpdateListSize()
        {
            Logging.GetLogsStat(out var min, out var count);
            VirtualListSize = min + count;
        }

        private void ScrollToBottom()
        {
            this.EnsureVisible(this.Items.Count - 1);
        }

        ListViewItem ItemFromLog(Logging.Log log)
        {
            var item = new ListViewItem(new string[] { log.timestamp, log.text });
            switch (log.level) {
                case Logging.Level.None:
                case Logging.Level.Debug:
                    item.BackColor = Color.FromArgb(unchecked((int)0xffdddddd));
                    break;
                case Logging.Level.Info:
                    //item.BackColor = Color.FromArgb(unchecked((int)0xffaaffaa));
                    break;
                case Logging.Level.Warning:
                    item.BackColor = Color.FromArgb(unchecked((int)0xffffffaa));
                    break;
                case Logging.Level.Error:
                    item.BackColor = Color.FromArgb(unchecked((int)0xffffaaaa));
                    break;
                default:
                    break;
            }
            return item;
        }
    }
}

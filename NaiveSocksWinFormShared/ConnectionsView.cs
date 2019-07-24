using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace NaiveSocks.WinForm
{
    public class ConnectionsView : UserControl
    {
        public class Tab : TabBase
        {
            public Tab(Controller controller)
            {
                ConnectionsView = new ConnectionsView(controller);
            }

            public override string Name => "Connections";
            public ConnectionsView ConnectionsView { get; }
            public override Control View => ConnectionsView;

            public override void OnTick()
            {
                ConnectionsView.Render();
            }
        }

        public ConnectionsView(Controller controller)
        {
            Controller = controller;
            listView = new ListViewDoubleBuffered() {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                ShowItemToolTips = true,
                BorderStyle = BorderStyle.None
            };
            this.Controls.Add(listView);
            ListView.ColumnHeaderCollection col = listView.Columns;
            col.Add("#", 50);
            col.Add("Time", 40);
            col.Add("Creator", 70);
            col.Add("Dest", 150);
            col.Add("Handler", 70);
            col.Add("Handler Stream", 250);
        }

        public Controller Controller { get; }

        ListView listView;
        Dictionary<int, Item> shownItems = new Dictionary<int, Item>();
        List<Item> queue = new List<Item>();
        int currentMark = 0;

        class Item
        {
            public int id;
            public ListViewItem viewItem;
            public InConnection conn;
            public int mark;
            public long lastPackets;
        }

        public void Render()
        {
            listView.BeginUpdate();
            try {
                ListView.ListViewItemCollection viewItems = listView.Items;
                var exist = currentMark++;
                var justcreated = currentMark++;
                lock (Controller.InConnectionsLock) {
                    foreach (var conn in Controller.InConnections.Values) {
                        if (shownItems.TryGetValue(conn.Id, out var item) == false) {
                            item = new Item { id = conn.Id, conn = conn, mark = justcreated };
                            queue.Add(item);
                            //shownItems.Add(item.id, item);
                        } else {
                            item.mark = exist;
                        }
                    }
                }
                queue.Sort((a, b) => a.id - b.id);
                foreach (var item in queue) {
                    var conn = item.conn;
                    shownItems.Add(item.id, item);
                    var vItem = item.viewItem = new ListViewItem();
                    for (int i = 0; i < listView.Columns.Count; i++) {
                        vItem.SubItems.Add(new ListViewItem.ListViewSubItem());
                    }
                    vItem.SubItems[0].Text = conn.Id.ToString();
                    viewItems.Add(vItem);
                }
                queue.Clear();
                foreach (var item in shownItems.Values) {
                    if (item.mark != exist && item.mark != justcreated) {
                        queue.Add(item);
                        continue;
                    }
                    var vItem = item.viewItem;
                    var conn = item.conn;
                    if (item.mark == justcreated) {
                        vItem.BackColor = Color.LightGreen;
                    } else {
                        BytesCounterValue ctr = conn.BytesCountersRW.TotalValue;
                        if (ctr.Packets != item.lastPackets) {
                            item.lastPackets = ctr.Packets;
                            vItem.BackColor = Color.LightSkyBlue;
                        } else if (vItem.BackColor != Color.Transparent) {
                            vItem.BackColor = Color.Transparent;
                        }
                    }

                    UpdateViewItem(vItem, conn);
                }
                foreach (var item in queue) {
                    shownItems.Remove(item.id);
                    viewItems.Remove(item.viewItem);
                }
                queue.Clear();
            } finally {
                listView.EndUpdate();
            }
        }

        private static void UpdateViewItem(ListViewItem vItem, InConnection conn)
        {
            var idx = 1;
            vItem.SubItems[idx++].Text = (WebSocket.CurrentTime - conn.CreateTime).ToString();
            vItem.SubItems[idx++].Text = conn.InAdapter?.Name ?? "-";
            vItem.SubItems[idx++].Text = conn.Dest.ToString();
            vItem.SubItems[idx++].Text = conn.RunningHandler?.Name ?? "-";
            if (conn is InConnectionTcp tcp) {
                vItem.SubItems[idx++].Text = tcp.ConnectResult?.Stream?.ToString() ?? "-";
            }
        }
    }

    public class ListViewDoubleBuffered : ListView
    {
        public ListViewDoubleBuffered()
        {
            this.DoubleBuffered = true;
        }
    }
}

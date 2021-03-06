﻿using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices.ComTypes;
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
            col.Add("Speed", 60);
            col.Add("Handler", 70);
            col.Add("Handler Stream", 220);
            col.Add("Sniffer", 60);

            MenuItem menuCloseConnections;
            listView.ContextMenu = new ContextMenu(new MenuItem[] {
                menuCloseConnections = new MenuItem("Close selected connections", (s, e) => {
                    foreach (ListViewItem vItem in listView.SelectedItems) {
                        var item = vItem.Tag as Item;
                        item.conn.Stop();
	                }
                })
            });
            listView.ContextMenu.Popup += (s, e) => {
                menuCloseConnections.Visible = listView.SelectedItems.Count > 0;
            };
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
            public long lastBytes;
            public int speed;
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
                    vItem.Tag = item;
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
                    var ctr = conn.BytesCountersRW.TotalValue;
                    if (item.mark == justcreated) {
                        vItem.BackColor = Color.FromArgb(unchecked((int)0xffccffcc));
                    } else {
                        if (ctr.Packets != item.lastPackets) {
                            vItem.BackColor = Color.FromArgb(unchecked((int)0xffbbddff));
                        } else if (vItem.BackColor != Color.Transparent) {
                            vItem.BackColor = Color.Transparent;
                        }
                    }
                    item.speed = (int)(ctr.Bytes - item.lastBytes);
                    item.lastPackets = ctr.Packets;
                    item.lastBytes = ctr.Bytes;

                    UpdateViewItem(item, conn);
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

        private static void UpdateViewItem(Item item, InConnection conn)
        {
            var vItem = item.viewItem;
            var idx = 1;
            vItem.SubItems[idx++].Text = (WebSocket.CurrentTime - conn.CreateTime).ToString();
            vItem.SubItems[idx++].Text = conn.InAdapter?.Name ?? "-";
            vItem.SubItems[idx++].Text = conn.Dest.ToString();
            vItem.SubItems[idx++].Text = item.speed == 0 ? "" : item.speed < 1024 ? "< 1 KB/s" : $"{item.speed / 1024:N0} KB/s";
            vItem.SubItems[idx++].Text = conn.RunningHandler?.Name ?? "-";
            vItem.SubItems[idx++].Text = (conn as InConnectionTcp)?.ConnectResult?.Stream?.ToString() ?? "-";
            vItem.SubItems[idx++].Text = conn.GetSniffingInfo();
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

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace NaiveSocks.WinForm
{
    class ControllerForm : Form
    {
        public static void RunGuiThread(Controller controller)
        {
            new System.Threading.Thread(() => {
                GuiThreadMain(controller);
            }) { Name = "GUI" }.Start();
        }

        private static void GuiThreadMain(Controller controller)
        {
            var form = new ControllerForm(controller);
            Application.EnableVisualStyles();
            Application.Run(form);
        }

        public ControllerForm(Controller controller)
        {
            Controller = controller;
            this.Size = new System.Drawing.Size(700, 400);
            this.DoubleBuffered = true;
            this.Text = BuildInfo.AppName;

            connectionsView = new ConnectionsView(controller) { Dock = DockStyle.Fill };
            this.Controls.Add(connectionsView);
        }

        public Controller Controller { get; }

        ConnectionsView connectionsView;
        Timer timer;

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Render();
            timer = new Timer() { Interval = 1000, Enabled = true };
            timer.Tick += (s, ee) => Render();
        }

        private void Render()
        {
            UpdateTitle();

            connectionsView.Render();
        }

        long lastPackets = 0, lastBytes = 0;

        private void UpdateTitle()
        {
            var controller = Controller;
            var p = MyStream.TotalCopiedPackets;
            var b = MyStream.TotalCopiedBytes;
            this.Text = $"{controller.RunningConnections}/{controller.TotalHandledConnections} current/total connections | relayed {p:N0} Δ{p - lastPackets:N0} packets / {b:N0} Δ{b - lastBytes:N0} bytes - {BuildInfo.AppName}";
            lastPackets = p;
            lastBytes = b;
        }

        protected override void Dispose(bool disposing)
        {
            timer.Dispose();
            base.Dispose(disposing);
        }
    }
}

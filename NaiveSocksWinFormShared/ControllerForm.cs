using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        public static void GuiThreadMain(Controller controller)
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

            this.Menu = new MainMenu(new MenuItem[] {
                new MenuItem("&Controller", new MenuItem[] {
                    new MenuItem("&Reload", (s, e) => {
                        Controller.Reload();
                    }, Shortcut.CtrlR),
                    new MenuItem("&Exit", (s, e) => {
                        Environment.Exit(0);
                    }, Shortcut.CtrlQ)
                }),
                new MenuItem("&View", new MenuItem[] {
                    new MenuItem("&Configration file location", (s, e) => {
                        OpenFolerAndShowFile(Controller.CurrentConfig.FilePath);
                    }),
                    new MenuItem("&Program file location", (s, e) => {
                        OpenFolerAndShowFile(Process.GetCurrentProcess().MainModule.FileName);
                    })
                })
            });

            AddTab(new ConnectionsView.Tab(controller));
            AddTab(new LogView.Tab());

            SwitchTab(tabs[0]);

            var about = new MenuItem("&About");
            about.MenuItems.Add(new MenuItem("&GitHub page", (s, e) => {
                Process.Start("https://github.com/hex11/Naive");
            }));
            about.MenuItems.Add(new MenuItem("-"));
            about.MenuItems.Add(new MenuItem("Version " + BuildInfo.CurrentVersion) { Enabled = false });
            if (BuildInfo.CurrentBuildText != null)
                about.MenuItems.Add(new MenuItem(BuildInfo.CurrentBuildText) { Enabled = false });
            Menu.MenuItems.Add(about);
        }

        void OpenFolerAndShowFile(string fileName) => Process.Start("explorer", $"/select, \"{fileName}\"");

        public Controller Controller { get; }

        Timer timer;

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            OnTick();
            timer = new Timer() { Interval = 1000, Enabled = true };
            timer.Tick += (s, ee) => OnTick();
        }

        private void OnTick()
        {
            UpdateTitle();
            foreach (var item in tabs) {
                item.OnTick();
            }
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

        private List<TabBase> tabs = new List<TabBase>();
        private TabBase currentTab;

        private void AddTab(TabBase tab)
        {
            tab.menuItem = this.Menu.MenuItems.Add("[" + tab.Name + "]", (s, e) => {
                SwitchTab(tab);
            });
            tabs.Add(tab);
        }

        private void SwitchTab(TabBase tab)
        {
            if (currentTab != null) {
                currentTab.SetShowing(false);
                this.Controls.RemoveAt(0);
            }
            currentTab = tab;
            tab.View.Dock = DockStyle.Fill;
            this.Controls.Add(tab.View);
            tab.SetShowing(true);
        }
    }

    public abstract class TabBase
    {
        internal MenuItem menuItem;

        public abstract Control View { get; }
        public abstract string Name { get; }

        public bool Showing { get; private set; }

        internal void SetShowing(bool show)
        {
            Showing = show;
            menuItem.Enabled = !show;
            if (show) OnShow();
            else OnHide();
        }

        protected virtual void OnShow()
        {
        }

        protected virtual void OnHide()
        {
        }

        public virtual void OnTick()
        {
        }
    }
}

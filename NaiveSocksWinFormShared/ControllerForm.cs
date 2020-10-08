using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace NaiveSocks.WinForm
{
    class ControllerForm : TabForm
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
                    new MenuItem("&Open configuration file", (s, e) => {
                        Process.Start(Controller.CurrentConfig.FilePath);
                    }),
                    new MenuItem("-"),
                    new MenuItem("Open &configuration file location", (s, e) => {
                        OpenFolerAndShowFile(Controller.CurrentConfig.FilePath);
                    }),
                    new MenuItem("Open &program file location", (s, e) => {
                        OpenFolerAndShowFile(Process.GetCurrentProcess().MainModule.FileName);
                    }),
                    new MenuItem("-"),
                    new MenuItem("&Reload", (s, e) => {
                        Controller.Reload();
                    }, Shortcut.CtrlR),
                    new MenuItem("-"),
                    new MenuItem("&Exit", (s, e) => {
                        Environment.Exit(0);
                    }, Shortcut.CtrlQ),
                }),
                InitHttpMenu(),
            });

            AddTab(new ConnectionsView.Tab(controller));
            AddTab(new LogView.Tab());

            SetTab(tabs[0]);

            Menu.MenuItems.Add(new MenuItem("C&onsole", (s, e) => {
                var con = new TabForm();
                con.SetTab(new ConsoleView.Tab());
                con.DestroyOnClose = true;
                con.Show();
            }));

            var about = new MenuItem("&About");
            about.MenuItems.Add(new MenuItem("&Project repo on GitHub", (s, e) => {
                Process.Start("https://github.com/hex11/Naive");
            }));
            about.MenuItems.Add(new MenuItem("-"));
            string version = "Version " + BuildInfo.CurrentVersion
                    + (NaiveSocksCli.SingleFile ? " (single file)" : "")
                    + (NaiveSocksCli.GuiMode ? " (GUI)" : "");
            about.MenuItems.Add(new MenuItem(version) { Enabled = false });
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

        private void AddTab(TabBase tab)
        {
            tab.menuItem = this.Menu.MenuItems.Add("[" + tab.Name + "]", (s, e) => {
                SetTab(tab);
            });
            tabs.Add(tab);
        }

        protected override void OnTabChanged(TabBase tab)
        {
        }

        private MenuItem InitHttpMenu()
        {
            var httpMenu = new MenuItem("&System proxy");
            var items = httpMenu.MenuItems;
            var none = items.Add("None", (s, e) => SystemProxy.Set(null));
            var cur = items.Add("");
            cur.Enabled = false;
            var sep = items.Add("-");

            httpMenu.Popup += (s, e) => {
                for (int i = items.Count - 1; i >= 0; i--) {
                    items.RemoveAt(i);
                }
                var curProxy = SystemProxy.Get();
                int usingPort = -1;
                items.Add(none);
                none.Checked = curProxy.IsDefault;
                if (curProxy.IsDefault == false) {
                    cur.Checked = true;
                    cur.Text = ("Current: " + curProxy);
                    items.Add(cur);
                    if (curProxy.Host == "127.0.0.1") { // This is not perfect
                        usingPort = curProxy.Port;
                    }
                }
                items.Add(sep);
                bool hasNonProxyHttp = false;
                foreach (var adapter in Controller.Adapters) {
                    if (adapter is HttpInAdapter http) {
                        if (http.@out == null) {
                            hasNonProxyHttp = true;
                            continue;
                        }
                        var menuItem = items.Add($"'{http.Name}' - {http.listen}");
                        if (http.listen.Port > 0) {
                            if (http.listen.Port == usingPort) {
                                menuItem.Checked = true;
                                cur.Checked = false;
                            }
                            menuItem.Click += (s2, e2) => {
                                SystemProxy.Set("127.0.0.1:" + http.listen.Port);
                            };
                        } else {
                            menuItem.Enabled = false;
                        }
                    }
                }
                if (hasNonProxyHttp) {
                    items.Add("(Some adapters missing 'listen' or 'out' are not listed)").Enabled = false;
                }
            };
            return httpMenu;
        }
    }
}

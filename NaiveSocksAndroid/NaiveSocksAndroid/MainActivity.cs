//using Android.App;
using Android.Widget;
using Android.OS;
using System.Threading.Tasks;
using NaiveSocks;
using Naive.HttpSvr;
using Naive.Console;
using System.IO;
using Android.Content;
using Android.Net;
using Android.Views;
using Android.Support.V7.App;
using Toolbar = Android.Support.V7.Widget.Toolbar;
using Android.Graphics;
using System;
using Android.Support.V4.Widget;
using Android.Content.PM;
using R = NaiveSocksAndroid.Resource;
using Android.Support.V7.Widget;
using System.Linq;
using Android.Support.Design.Widget;
using Android.Support.V4.Content;
using Android;
using Android.Support.V4.App;
using Android.Runtime;
using System.Text;
using Android.Support.V4.View;
using System.Threading;
using Android.Text;
using Android.Text.Style;

namespace NaiveSocksAndroid
{
    [Android.App.Activity(
        Label = "@string/app_name",
        Name = "naive.NaiveSocksAndroid.MainActivity",
        MainLauncher = true,
        Exported = true,
        LaunchMode = LaunchMode.SingleTask,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout,
        WindowSoftInputMode = SoftInput.StateUnchanged | SoftInput.AdjustResize
    )]
    [Android.App.MetaData("android.app.shortcuts", Resource = "@xml/shortcuts")]
    [Android.App.IntentFilter(new[] { Android.Service.QuickSettings.TileService.ActionQsTilePreferences })]
    public class MainActivity : ActivityWithToolBar
    {
        private NavigationView navigationView;
        private DrawerLayout drawer;
        private Intent serviceStartIntent;

        private bool isConnected => bgServiceConn?.IsConnected ?? false;
        private bool isServiceForegroundRunning => isConnected && service.IsForegroundRunning;
        private bool isServiceInOperation => isConnected && service.IsInOperation;
        private BgService service => bgServiceConn?.Value;
        private ServiceConnection<BgService> bgServiceConn;

        public BgService Service => bgServiceConn?.Value;

        static readonly string[] drawerStrings = { "home", "logs", "connections", "adapters", "console" };
        static readonly int[] drawerIds = { R.Id.nav_home, R.Id.nav_logs, R.Id.nav_connections, R.Id.nav_adapters, R.Id.nav_console };

        private string AppName;
        private Java.Lang.ICharSequence JavaAppName;

        private const int REQ_VPN = 1;

        //private ServiceConnection<ConfigService> cfgService;

        static IncrNumberGenerator idGen = new IncrNumberGenerator();
        int id = idGen.Get();

        void DebugEvent(string text)
        {
            //Logging.debugForce("Activity#" + id + ": " + text);
        }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            DebugEvent("OnCreate");
            DebugEvent("Intent: " + Intent?.ToUri(IntentUriType.None) ?? "(null)");
            //if (savedInstanceState != null) {
            //    DebugEvent("Dumping savedInstanceState");
            //    foreach (var item in savedInstanceState.KeySet()) {
            //        DebugEvent(item + ": " + savedInstanceState.Get(item));
            //    }
            //    DebugEvent("Dumping savedInstanceState End");
            //}

            AppName = Resources.GetString(R.String.app_name);
            JavaAppName = new Java.Lang.String(AppName);

            base.OnCreate(savedInstanceState);

            serviceStartIntent = new Intent(this, typeof(BgService));

            if (this.Intent.Action == "PREP_VPN") {
                VpnServicePrepare();
            }

            if (this.Intent.DataString == "toggle") {
                StartServiceWithAction(BgService.Actions.TOGGLE);
                if (Build.VERSION.SdkInt >= BuildVersionCodes.NMr1) {
                    var sm = GetSystemService(ShortcutService) as ShortcutManager;
                    sm?.ReportShortcutUsed("toggle");
                }
                this.Finish();
                return;
            }

            bgServiceConn = new ServiceConnection<BgService>(
                connected: (ComponentName name, IBinder service) => {
                    if (Service.ShowingActivity != null) {
                        Logging.error("BUG?: Service.ShowingActivity != null");
                    }
                    Service.ShowingActivity = this;
                    Service.ForegroundStateChanged += Service_ForegroundStateChanged;
                    InvalidateOptionsMenu();
                    curFrag?.OnConnected();
                },
                disconnected: (ComponentName name) => {
                    if (Service.ShowingActivity != this) {
                        Logging.error("BUG?: Service.ShowingActivity != this");
                    }
                    Service.ShowingActivity = null;
                    Service.ForegroundStateChanged -= Service_ForegroundStateChanged;
                    InvalidateOptionsMenu();
                });

            // Set our view from the "main" layout resource
            SetRealContentView(R.Layout.Main);

            toolbar.TitleFormatted = JavaAppName;

            navigationView = FindViewById<NavigationView>(R.Id.nvView);
            drawer = FindViewById<DrawerLayout>(R.Id.drawer_layout);

            var drawerToggle = new Android.Support.V7.App.ActionBarDrawerToggle
                (this, drawer, toolbar, R.String.drawer_open, R.String.drawer_close);
            drawer.AddDrawerListener(drawerToggle);
            drawerToggle.SyncState();

            if (navigationView.GetHeaderView(0) is LinearLayout la) {
                if (la.GetChildAt(0) is TextView tv) {
                    var sb = new StringBuilder(64);
                    var pkgInfo = PackageManager.GetPackageInfo(PackageName, 0);
                    sb.Append(tv.Text).Append(" ").Append(BuildInfo.CurrentVersion);
                    sb.Append("\n").Append(GetString(R.String.naivesocksandroid)).Append(" ").Append(pkgInfo.VersionName).Append(" (").Append(pkgInfo.VersionCode).Append(")");
                    var ssb = new SpannableStringBuilder();
                    ssb.Append(sb.ToString());
                    if (BuildInfo.CurrentBuildText != null) {
                        var start = ssb.Length();
                        ssb.Append('\n');
                        ssb.Append(BuildInfo.CurrentBuildText);
                        var end = ssb.Length();
                        ssb.SetSpan(new RelativeSizeSpan(0.7f), start, end, SpanTypes.ExclusiveExclusive);
                        ssb.SetSpan(new ForegroundColorSpan(new Color(unchecked((int)0xbbffffff))), start, end, SpanTypes.ExclusiveExclusive);
                    }
                    tv.TextFormatted = ssb;
                }
            }

            navigationView.NavigationItemSelected += NavigationView_NavigationItemSelected;

            if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.ReadExternalStorage)
                != Permission.Granted) {
                Logging.info("requesting storage read/write permissions...");
                ActivityCompat.RequestPermissions(this, new[] {
                    Manifest.Permission.ReadExternalStorage,
                    Manifest.Permission.WriteExternalStorage
                }, 1);
            }

            //drawer.OpenDrawer(GravityCompat.Start);

            int initNavIndex = Array.IndexOf(drawerStrings, Intent.DataString);
            if (initNavIndex == -1)
                initNavIndex = 0;

            topView.Post(() => {
                // User may have exited from this activity.
                if (isActivityRunning) {
                    int menuItemId = drawerIds[initNavIndex];
                    onNavigationItemSelected(navigationView.Menu.FindItem(menuItemId));
                }
            });
        }

        public void VpnServicePrepare()
        {
            var r = VpnService.Prepare(this);
            if (r == null) {
                StartServiceWithAction(BgService.Actions.START_VPN);
            } else {
                StartActivityForResult(r, REQ_VPN);
            }
        }

        private void Service_ForegroundStateChanged(BgService obj)
        {
            DebugEvent("Service_ForegroundStateChanged");
            InvalidateOptionsMenu();
        }

        bool isActivityRunning;

        protected override void OnStart()
        {
            DebugEvent("OnStart");
            isActivityRunning = true;
            base.OnStart();
            if (!isConnected)
                BindBgService();
        }

        protected override void OnActivityResult(int requestCode, [GeneratedEnum] Android.App.Result resultCode, Intent data)
        {
            if (requestCode == REQ_VPN) {
                Logging.info("VPN grant: " + resultCode);
                if (resultCode == Android.App.Result.Ok) {
                    StartServiceWithAction(BgService.Actions.START_VPN);
                }
            } else {
                Logging.warning("Unknown requestCode: " + requestCode);
            }
        }

        private void BindBgService()
        {
            DebugEvent("BindBgService");
            this.BindService(new Intent(this, typeof(BgService)), bgServiceConn, Bind.AutoCreate);
        }

        protected override void OnStop()
        {
            DebugEvent("OnStop");
            isActivityRunning = false;
            base.OnStop();
            if (isConnected)
                UnbindService();
        }

        private void UnbindService()
        {
            DebugEvent("UnbindService");
            bgServiceConn.OnServiceDisconnected(null);
            this.UnbindService(bgServiceConn);
        }

        public override void OnBackPressed()
        {
            if (drawer.IsDrawerVisible(GravityCompat.Start)) {
                drawer.CloseDrawers();
            } else {
                base.OnBackPressed();
            }
        }

        MyBaseFragment curFrag = null;

        private void NavigationView_NavigationItemSelected(object sender, NavigationView.NavigationItemSelectedEventArgs e)
        {
            IMenuItem menuItem = e.MenuItem;
            onNavigationItemSelected(menuItem);
        }

        private void onNavigationItemSelected(IMenuItem menuItem)
        {
            if (menuItem.IsChecked) {
                drawer.CloseDrawers();
                return;
            }
            MyBaseFragment frag = null;
            int itemId = menuItem.ItemId;
            switch (itemId) {
                case R.Id.nav_home:
                    frag = new FragmentHome();
                    break;
                case R.Id.nav_logs:
                    frag = new FragmentLogs();
                    break;
                case R.Id.nav_connections:
                    frag = new FragmentConnections();
                    break;
                case R.Id.nav_adapters:
                    frag = new FragmentAdapters();
                    break;
                case R.Id.nav_console:
                    frag = new FragmentConsole();
                    break;
                case R.Id.nav_start:
                    startService();
                    break;
                case R.Id.nav_stop:
                    stopService();
                    break;
                case R.Id.nav_reload:
                    reloadService();
                    break;
            }
            if (frag != null) {
                var title = itemId == R.Id.nav_home ? JavaAppName : menuItem.TitleFormatted;
                string titleClrString = null;
                SetTitle(title);
                frag.InfoStrChanged += (str) => {
                    if (str == null) {
                        SetTitle(title);
                    } else {
                        if (titleClrString == null)
                            titleClrString = title.ToString();
                        SetTitle(titleClrString + " " + str);
                    }
                };

                ReplaceFragment(frag);

                menuItem.SetChecked(true);
            }
            drawer.CloseDrawers();
        }

        private void SetTitle(string title)
        {
            SetTitle(new Java.Lang.String(title));
        }

        private void SetTitle(Java.Lang.ICharSequence title)
        {
            TitleFormatted = title;
        }

        private void ReplaceFragment(MyBaseFragment frag)
        {
            DebugEvent("ReplaceFragment");
            var fm = SupportFragmentManager;
            fm.BeginTransaction().Replace(R.Id.flContent, frag).Commit();
            curFrag = frag;
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
        {
            for (int i = 0; i < permissions.Length; i++) {
                bool granted = grantResults[i] == Permission.Granted;
                Logging.log($"permission {(granted ? "granted" : "denied")}: {permissions[i]}",
                    level: granted ? Logging.Level.Info : Logging.Level.Warning);
            }
        }

        private void StartServiceWithAction(string action)
        {
            lock (serviceStartIntent) {
                try {
                    serviceStartIntent.SetAction(action);
                    StartService(serviceStartIntent);
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "StartService() with action=" + action);
                }
            }
        }

        private Task startService()
        {
            if (Service.IsForegroundRunning == false) {
                return Task.Run(() => {
                    Logging.info("starting controller...");
                    StartServiceWithAction(BgService.Actions.START);
                });
            } else {
                Logging.info("cannot start controller: the controller is already running.");
                return NaiveUtils.CompletedTask;
            }
        }

        private Task stopService()
        {
            return Task.Run(() => {
                Logging.info("stopping controller...");
                StartServiceWithAction(BgService.Actions.STOP);
            });
        }

        private void reloadService()
        {
            if (isServiceForegroundRunning) {
                try {
                    service.Reload();
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "reloading controller");
                }
            } else {
                Logging.info("cannot reload: the service/controller is not running");
            }
        }

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            MenuInflater.Inflate(R.Menu.menu_control, menu);

            if (curFrag is ICanHandleMenu ichm) {
                ichm.OnCreateMenu(menu);
            }

            bool running = isServiceForegroundRunning;
            bool inOperation = isServiceInOperation;

            menu.FindItem(R.Id.menu_operating).SetVisible(inOperation);

            if (inOperation || running) {
                menu.FindItem(R.Id.menu_start).SetVisible(false);
            }
            if (inOperation || !running) {
                menu.FindItem(R.Id.menu_stop).SetVisible(false);
                menu.FindItem(R.Id.menu_reload).SetVisible(false);
            }

            navigationView.Menu.FindItem(R.Id.nav_operating).SetVisible(inOperation);
            navigationView.Menu.FindItem(R.Id.nav_start).SetVisible(!inOperation && !running);
            navigationView.Menu.FindItem(R.Id.nav_stop).SetVisible(!inOperation && running);
            navigationView.Menu.FindItem(R.Id.nav_reload).SetVisible(!inOperation && running);

            const int group = 0;
            const int order = 150;

            var subMenu = menu.AddSubMenu(group, 0, order, R.String.submenu_restart_kill);
            subMenu.Add(0, 0, 0, R.String.restart)
                .SetShowAsActionFlags(ShowAsAction.Never);
            subMenu.Add(0, 0, 0, R.String.kill)
                .SetShowAsActionFlags(ShowAsAction.Never);

            menu.Add(group, 0, order, R.String.autostart)
                .SetCheckable(true)
                .SetChecked(AppConfig.Current.Autostart)
                .SetShowAsActionFlags(ShowAsAction.Never);
            menu.Add(group, 0, order, R.String.editconfig)
                .SetShowAsActionFlags(ShowAsAction.Never);

            return true;
        }

        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            var id = item.ItemId;
            if (id == Android.Resource.Id.Home) {
                drawer.OpenDrawer(GravityCompat.Start);
                return true;
            } else if (id == R.Id.menu_start) {
                startService();
            } else if (id == R.Id.menu_stop) {
                stopService();
            } else if (id == R.Id.menu_reload) {
                reloadService();
            } else {
                var title = item.TitleFormatted.ToString();
                bool EqualsId(string str, int strId) => str == GetString(strId);
                if (EqualsId(title, R.String.autostart)) {
                    setAutostart(!item.IsChecked);
                } else if (EqualsId(title, R.String.editconfig)) {
                    StartActivity(new Intent(this, typeof(EditorActivity)).AddFlags(ActivityFlags.NewTask));
                } else if (EqualsId(title, R.String.submenu_restart_kill)) {
                    // nothing to do
                } else if (EqualsId(title, R.String.restart)) {
                    NaiveUtils.RunAsyncTask(async () => {
                        await stopService();
                        await startService();
                    });
                } else if (EqualsId(title, R.String.kill)) {
                    try {
                        if (isServiceForegroundRunning) {
                            StartServiceWithAction(BgService.Actions.KILL);
                        } else {
                            System.Diagnostics.Process.GetCurrentProcess().Kill();
                        }
                    } catch (Exception e) {
                        Logging.exception(e, Logging.Level.Error, "Failed to kill myself!");
                    }
                } else {
                    if (curFrag is ICanHandleMenu ichm) {
                        ichm.OnMenuItemSelected(item);
                    }
                }
            }
            return base.OnOptionsItemSelected(item);
        }

        private void setAutostart(bool enabled)
        {
            AppConfig.Current.Autostart = enabled;
            var pm = this.PackageManager;
            var componentName = new ComponentName(this, Java.Lang.Class.FromType(typeof(BootReceiver)));
            var enabledState = (enabled) ? ComponentEnabledState.Enabled : ComponentEnabledState.Disabled;
            pm.SetComponentEnabledSetting(componentName, enabledState, ComponentEnableOption.DontKillApp);
            this.InvalidateOptionsMenu();
            MakeSnackbar(FormatSwitchString(R.String.autostart, enabled), Snackbar.LengthLong).Show();
        }

        private string FormatSwitchString(int what, bool enabled)
        {
            return FormatSwitchString(this, what, enabled);
        }

        public static string FormatSwitchString(Context context, int what, bool enabled)
        {
            return String.Format(context.GetString(enabled ? R.String.format_enabled : R.String.format_disable), context.GetString(what));
        }
    }

    interface ICanHandleMenu
    {
        void OnCreateMenu(IMenu menu);
        void OnMenuItemSelected(IMenuItem item);
    }
}

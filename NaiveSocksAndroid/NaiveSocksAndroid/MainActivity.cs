﻿//using Android.App;
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

namespace NaiveSocksAndroid
{
    [Android.App.Activity(
        Label = "NaiveSocks",
        Name = "naive.NaiveSocksAndroid.MainActivity",
        MainLauncher = true,
        Exported = true,
        LaunchMode = LaunchMode.SingleTask,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
    [Android.App.MetaData("android.app.shortcuts", Resource = "@xml/shortcuts")]
    public class MainActivity : AppCompatActivity
    {
        private CoordinatorLayout topView;
        private NavigationView navigationView;
        private DrawerLayout drawer;
        private Intent serviceStartIntent;
        private Intent serviceBindIntent;

        private bool isConnected => bgServiceConn?.IsConnected ?? false;
        private bool isServiceForegroundRunning => isConnected && service.IsForegroundRunning;
        private BgService service => bgServiceConn?.Value;
        private ServiceConnection<BgService> bgServiceConn;

        public BgService Service => bgServiceConn?.Value;

        public Toolbar toolbar;
        private string AppName;
        private Java.Lang.ICharSequence JavaAppName;

        //private ServiceConnection<ConfigService> cfgService;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            CrashHandler.CheckInit();
            AppConfig.Init(ApplicationContext);

            AppName = Resources.GetString(R.String.app_name);
            JavaAppName = new Java.Lang.String(AppName);

            base.OnCreate(savedInstanceState);

            serviceStartIntent = new Intent(this, typeof(BgService));
            serviceBindIntent = new Intent(this, typeof(BgService));

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
                    Service.ForegroundStateChanged += Service_ForegroundStateChanged;
                    InvalidateOptionsMenu();
                },
                disconnected: (ComponentName name) => {
                    Service.ForegroundStateChanged -= Service_ForegroundStateChanged;
                    InvalidateOptionsMenu();
                });

            // Set our view from the "main" layout resource
            SetContentView(R.Layout.Main);

            topView = FindViewById<CoordinatorLayout>(R.Id.topview);

            toolbar = FindViewById<Toolbar>(R.Id.toolbar);
            SetSupportActionBar(toolbar);
            SupportActionBar.SetHomeButtonEnabled(true);
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
                    sb.Append("\n").Append("NaiveSocksAndroid ").Append(pkgInfo.VersionName).Append(" (").Append(pkgInfo.VersionCode).Append(")");
                    if (BuildInfo.CurrentBuildText != null) {
                        sb.Append("\n").Append(BuildInfo.CurrentBuildText);
                    }
                    tv.Text = sb.ToString();
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

            int initNavIndex = 0;

            if (this.Intent.DataString == "logs") {
                initNavIndex = 1;
            } else if (this.Intent.DataString == "connections") {
                initNavIndex = 2;
            } else if (this.Intent.DataString == "adapters") {
                initNavIndex = 3;
            }

            this.BindService(serviceBindIntent, bgServiceConn, Bind.AutoCreate);

            topView.Post(() => onNavigationItemSelected(navigationView.Menu.GetItem(initNavIndex)));
        }

        private void Service_ForegroundStateChanged(BgService obj)
        {
            InvalidateOptionsMenu();
        }

        protected override void OnStart()
        {
            base.OnStart();
            if (!isConnected)
                this.BindService(serviceBindIntent, bgServiceConn, Bind.AutoCreate);
        }

        protected override void OnStop()
        {
            base.OnStop();
            if (isConnected)
                UnbindService();
            AsyncHelper.SetTimeout(100, () => GC.Collect(0));
        }

        private void UnbindService()
        {
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

        Fragment curFrag = null;

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
            Fragment frag = null;
            int itemId = menuItem.ItemId;
            switch (itemId) {
            case R.Id.nav_home:
                frag = new FragmentHome(this);
                break;
            case R.Id.nav_logs:
                frag = new FragmentLogs(this);
                break;
            case R.Id.nav_connections:
                frag = new FragmentConnections(this);
                break;
            case R.Id.nav_adapters:
                frag = new FragmentAdapters(this);
                break;
            }
            if (frag == null)
                return;
            ReplaceFragment(frag);

            menuItem.SetChecked(true);
            TitleFormatted = itemId == R.Id.nav_home ? JavaAppName : menuItem.TitleFormatted;
            drawer.CloseDrawers();
        }

        private void ReplaceFragment(Fragment frag)
        {
            var fm = SupportFragmentManager;
            fm.BeginTransaction().Replace(R.Id.flContent, frag).Commit();
            curFrag = frag;
            InvalidateOptionsMenu();
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
                serviceStartIntent.SetAction(action);
                StartService(serviceStartIntent);
            }
        }

        private Task startService()
        {
            return Task.Run(() => {
                if (Service.IsForegroundRunning == false) {
                    Logging.info("starting controller...");
                    StartServiceWithAction(BgService.Actions.START);
                } else {
                    Logging.info("cannot start controller: the controller is already running.");
                }
            });
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
            Task.Run(() => {
                if (isServiceForegroundRunning) {
                    try {
                        service.Controller.Reload();
                    } catch (Exception e) {
                        Logging.exception(e, Logging.Level.Error, "reloading controller");
                    }
                } else {
                    Logging.info("cannot reload: the service/controller is not running");
                }
            });
        }

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            MenuInflater.Inflate(R.Menu.menu_control, menu);

            if (curFrag is ICanHandleMenu ichm) {
                ichm.OnCreateMenu(menu);
            }

            if (isServiceForegroundRunning) {
                menu.FindItem(R.Id.menu_start).SetVisible(false);
            } else {
                menu.FindItem(R.Id.menu_stop).SetVisible(false);
                menu.FindItem(R.Id.menu_reload).SetVisible(false);
            }

            var subMenu = menu.AddSubMenu(R.String.submenu_restart_kill);
            subMenu.Add(R.String.restart)
                .SetShowAsActionFlags(ShowAsAction.Never);
            subMenu.Add(R.String.kill)
                .SetShowAsActionFlags(ShowAsAction.Never);

            menu.Add(R.String.logs_in_notif)
                .SetCheckable(true)
                .SetChecked(AppConfig.Current.ShowLogs)
                .SetShowAsActionFlags(ShowAsAction.Never);
            menu.Add(R.String.autostart)
                .SetCheckable(true)
                .SetChecked(AppConfig.Current.Autostart)
                .SetShowAsActionFlags(ShowAsAction.Never);
            menu.Add(R.String.openconfig)
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
                if (EqualsId(title, R.String.logs_in_notif)) {
                    setShowLogs(!item.IsChecked);
                } else if (EqualsId(title, R.String.autostart)) {
                    setAutostart(!item.IsChecked);
                } else if (EqualsId(title, R.String.openconfig)) {
                    var paths = AppConfig.GetNaiveSocksConfigPaths(this);
                    var found = paths.FirstOrDefault(x => File.Exists(x));
                    if (found == null) {
                        MakeSnackbar(GetString(R.String.no_config), Snackbar.LengthLong).Show();
                    } else {
                        var intent = new Intent(Intent.ActionEdit);
                        Android.Net.Uri fileUri;
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.N) {
                            fileUri = FileProvider.GetUriForFile(this, "naive.NaiveSocksAndroid.fp", new Java.IO.File(found));
                            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                            intent.AddFlags(ActivityFlags.GrantWriteUriPermission);
                        } else {
                            fileUri = Android.Net.Uri.FromFile(new Java.IO.File(found));
                        }
                        intent.SetDataAndType(fileUri, "text/plain");
                        intent.AddFlags(ActivityFlags.NewTask);
                        try {
                            StartActivity(intent);
                        } catch (ActivityNotFoundException) {
                            MakeSnackbar("No activity to handle", Snackbar.LengthLong).Show();
                        }
                    }
                } else if (EqualsId(title, R.String.submenu_restart_kill)) {
                    // nothing to do
                } else if (EqualsId(title, R.String.restart)) {
                    NaiveUtils.RunAsyncTask(async () => {
                        await stopService();
                        await startService();
                    });
                } else if (EqualsId(title, R.String.kill)) {
                    try {
                        System.Diagnostics.Process.GetCurrentProcess().Kill();
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

        private void setShowLogs(bool show)
        {
            AppConfig.Current.ShowLogs = show;
            this.InvalidateOptionsMenu();
            if (isConnected) {
                service.SetShowLogs(show);
            }
            MakeSnackbar(FormatSwitchString(R.String.logs_in_notif, show), Snackbar.LengthLong).Show();
        }

        public Snackbar MakeSnackbar(string text, int duration)
        {
            return Snackbar.Make(topView, text, duration);
        }
    }

    interface ICanHandleMenu
    {
        void OnCreateMenu(IMenu menu);
        void OnMenuItemSelected(IMenuItem item);
    }
}

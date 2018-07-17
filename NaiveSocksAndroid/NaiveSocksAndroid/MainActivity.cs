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

namespace NaiveSocksAndroid
{
    [Android.App.Activity(
        Label = "NaiveSocks",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTask,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
    public class MainActivity : AppCompatActivity
    {
        private CoordinatorLayout topView;
        private NavigationView navigationView;
        private DrawerLayout drawer;
        private Intent serviceIntent;

        private bool isConnected => bgServiceConn?.IsConnected ?? false;
        private BgService service => bgServiceConn?.Value;
        private ServiceConnection<BgService> bgServiceConn;

        public BgService Service => bgServiceConn?.Value;

        private Toolbar toolbar;
        private const string TOOLBAR_TITLE = "NaiveSocks";

        //private ServiceConnection<ConfigService> cfgService;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            CrashHandler.CheckInit();
            AppConfig.Init(ApplicationContext);

            base.OnCreate(savedInstanceState);

            //BindService(new Intent(this, typeof(ConfigService)), cfgService = new ServiceConnection<ConfigService>(), Bind.AutoCreate);

            serviceIntent = new Intent(this, typeof(BgService));
            serviceIntent.SetAction("start!");

            bgServiceConn = new ServiceConnection<BgService>(
                connected: (ComponentName name, IBinder service) => {
                    //toolbar.Title = TOOLBAR_TITLE + " - running";
                    InvalidateOptionsMenu();
                },
                disconnected: (ComponentName name) => {
                    //toolbar.Title = TOOLBAR_TITLE;
                    InvalidateOptionsMenu();
                });

            // Set our view from the "main" layout resource
            SetContentView(R.Layout.Main);

            topView = FindViewById<CoordinatorLayout>(R.Id.topview);

            toolbar = FindViewById<Toolbar>(R.Id.toolbar);
            SetSupportActionBar(toolbar);
            SupportActionBar.SetHomeButtonEnabled(true);
            toolbar.Title = TOOLBAR_TITLE;

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

            //if (savedInstanceState == null || !savedInstanceState.ContainsKey("curFragment")) {
            drawer.OpenDrawer(GravityCompat.Start);
            //} else {
            //    ReplaceFragment(SupportFragmentManager.GetFragment(savedInstanceState, "curFragment"));
            //}

        }

        //protected override void OnSaveInstanceState(Bundle outState)
        //{
        //    base.OnSaveInstanceState(outState);
        //    if (curFrag != null)
        //        SupportFragmentManager.PutFragment(outState, "curFragment", curFrag);
        //}

        protected override void OnStart()
        {
            base.OnStart();
            this.BindService(serviceIntent, bgServiceConn, Bind.None);
        }

        protected override void OnStop()
        {
            if (bgServiceConn?.IsConnected == true)
                this.UnbindService(bgServiceConn);
            Task.Delay(100).ContinueWith((t) => GC.Collect(0));
            base.OnStop();
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
            if (menuItem.IsChecked) {
                drawer.CloseDrawers();
                return;
            }
            Fragment frag = null;
            switch (menuItem.ItemId) {
            case R.Id.nav_home:
                frag = new FragmentHome();
                break;
            case R.Id.nav_logs:
                frag = new FragmentLogs();
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
            TitleFormatted = menuItem.TitleFormatted;
            drawer.CloseDrawers();
        }

        private void ReplaceFragment(Fragment frag)
        {
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

        private Task startService()
        {
            return Task.Run(() => {
                if (!isConnected) {
                    Logging.info("starting/binding service...");
                    StartService(serviceIntent);
                    this.BindService(serviceIntent, bgServiceConn, Bind.None);
                } else {
                    Logging.info("cannot start service: service is already running.");
                }
            });
        }

        private Task stopService()
        {
            return Task.Run(() => {
                Logging.info("requesting to stop service.");
                if (!StopService(serviceIntent)) {
                    Logging.info("cannot stop service: service is not connected.");
                }
            });
        }

        private void reloadService()
        {
            Task.Run(() => {
                if (isConnected) {
                    try {
                        service.Controller.Reload();
                    } catch (Exception e) {
                        Logging.exception(e, Logging.Level.Error, "reloading controller");
                    }
                } else {
                    Logging.info("connot reload: service is not connected");
                }
            });
        }

        private const string menu_showLogs = "Show logs in notification";
        private const string menu_autostart = "Autostart";
        private const string menu_openConfig = "Open configuation file...";
        private const string menu_restart = "Restart";
        private const string menu_kill = "Kill!";

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            MenuInflater.Inflate(R.Menu.menu_control, menu);

            if (isConnected) {
                menu.FindItem(R.Id.menu_start).SetVisible(false);
            } else {
                menu.FindItem(R.Id.menu_stop).SetVisible(false);
                menu.FindItem(R.Id.menu_reload).SetVisible(false);
            }

            var subMenu = menu.AddSubMenu("Restart/Kill");
            subMenu.Add(menu_restart)
                .SetShowAsActionFlags(ShowAsAction.Never);
            subMenu.Add(menu_kill)
                .SetShowAsActionFlags(ShowAsAction.Never);

            menu.Add(menu_showLogs)
                .SetCheckable(true)
                .SetChecked(AppConfig.Current.ShowLogs)
                .SetShowAsActionFlags(ShowAsAction.Never);
            menu.Add(menu_autostart)
                .SetCheckable(true)
                .SetChecked(AppConfig.Current.Autostart)
                .SetShowAsActionFlags(ShowAsAction.Never);
            menu.Add(menu_openConfig)
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
                if (title == menu_showLogs) {
                    setShowLogs(!item.IsChecked);
                } else if (title == menu_autostart) {
                    setAutostart(!item.IsChecked);
                } else if (title == menu_openConfig) {
                    var paths = AppConfig.GetNaiveSocksConfigPaths(this);
                    var found = paths.FirstOrDefault(x => File.Exists(x));
                    if (found == null) {
                        MakeSnackbar("No configuation file.", Snackbar.LengthLong).Show();
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
                } else if (title == menu_restart) {
                    NaiveUtils.RunAsyncTask(async () => {
                        await stopService();
                        await startService();
                    });
                } else if (title == menu_kill) {
                    try {
                        System.Diagnostics.Process.GetCurrentProcess().Kill();
                    } catch (Exception e) {
                        Logging.exception(e, Logging.Level.Error, "Failed to kill myself!");
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
            MakeSnackbar($"Autostart is {(enabled ? "enabled" : "disabled")}.", Snackbar.LengthLong).Show();
        }

        private void setShowLogs(bool show)
        {
            AppConfig.Current.ShowLogs = show;
            this.InvalidateOptionsMenu();
            if (isConnected) {
                service.SetShowLogs(show);
            }
            MakeSnackbar($"Logger output will{(show ? "" : " not")} be shown in notification.", Snackbar.LengthLong).Show();
        }

        private Snackbar MakeSnackbar(string text, int duration)
        {
            return Snackbar.Make(topView, text, duration);
        }
    }
}

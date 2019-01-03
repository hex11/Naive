using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Service.QuickSettings;
using Android.Views;
using Android.Widget;

namespace NaiveSocksAndroid
{
    [Service(
        Name = "naive.NaiveSocksAndroid.TileService",
        Icon = "@drawable/N",
        Label = "@string/toggle",
        Permission = "android.permission.BIND_QUICK_SETTINGS_TILE"
        )]
    [IntentFilter(new[] { TileService.ActionQsTile })]
    class ToggleTileService : TileService
    {
        public override void OnStartListening()
        {
            AppConfig.Init(this);
            UpdateTile();
            BgServiceRunningState.StateChanged += UpdateTile;
        }

        private void UpdateTile()
        {
            var tile = QsTile;
            bool operating = BgServiceRunningState.IsInOperation;
            bool running = BgServiceRunningState.IsRunning;
            tile.Label = base.Resources.GetString(operating ? Resource.String.in_operation : Resource.String.app_name);
            tile.State = operating ? TileState.Unavailable : running ? TileState.Active : TileState.Inactive;
            tile.UpdateTile();
        }

        public override void OnClick()
        {
            Intent serviceIntent = new Intent(this, typeof(BgService));
            serviceIntent.SetAction(BgService.Actions.TOGGLE);
            Android.Support.V4.Content.ContextCompat.StartForegroundService(this, serviceIntent);
        }

        public override void OnStopListening()
        {
            BgServiceRunningState.StateChanged -= UpdateTile;
        }
    }
}
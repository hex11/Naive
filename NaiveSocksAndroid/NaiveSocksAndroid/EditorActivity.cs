using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Support.Design.Widget;
using Android.Views;
using Android.Widget;
using Naive.HttpSvr;
using R = NaiveSocksAndroid.Resource;

namespace NaiveSocksAndroid
{
    [Activity(Label = "@string/config_editor", LaunchMode = Android.Content.PM.LaunchMode.SingleTask,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout,
        WindowSoftInputMode = SoftInput.StateUnchanged | SoftInput.AdjustResize)]
    public class EditorActivity : ActivityWithToolBar
    {
        string currentFilePath;
        private EditText editText;
        private ScrollView scrollView;
        bool unsavedChanges = false;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            SupportActionBar.SetDisplayShowHomeEnabled(true);
            SupportActionBar.SetDisplayHomeAsUpEnabled(true);

            toolbar.SetTitle(Resource.String.config_editor);
            // Create your application here

            const int match_content = ViewGroup.LayoutParams.MatchParent;
            const int wrap_content = ViewGroup.LayoutParams.WrapContent;

            scrollView = new ScrollView(this) {
                LayoutParameters = new ViewGroup.LayoutParams(match_content, match_content),
                FillViewport = true
            };

            editText = new EditText(this) {
                LayoutParameters = new ViewGroup.LayoutParams(match_content, wrap_content),
                Gravity = GravityFlags.Top,
                Typeface = Typeface.Monospace,
                TextSize = 12,
                Background = null
            };

            scrollView.AddView(editText);

            SetRealContentView(scrollView);

            string[] paths = AppConfig.GetNaiveSocksConfigPaths(this);
            currentFilePath = paths.FirstOrDefault(x => File.Exists(x));
            if (currentFilePath != null) {
                try {
                    var fileContent = File.ReadAllText(currentFilePath, Encoding.UTF8);
                    editText.Text = fileContent;
                } catch (Exception e) {
                    MakeSnackbar(Resources.GetString(R.String.saving_error) + e.Message, Snackbar.LengthLong).Show();
                }
            } else {
                currentFilePath = paths[0];
                MakeSnackbar(R.String.no_config, Snackbar.LengthLong).Show();
            }
            editText.TextChanged += EditText_TextChanged;
        }

        private void EditText_TextChanged(object sender, Android.Text.TextChangedEventArgs e)
        {
            unsavedChanges = true;
        }

        const int id_save = 2331;

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            menu.Add(Menu.None, id_save, Menu.None, R.String.save).SetShowAsAction(ShowAsAction.IfRoom);
            return true;
        }

        public override bool OnSupportNavigateUp()
        {
            OnBackPressed();
            return true;
        }

        Snackbar lastSnackbar;

        public override void OnBackPressed()
        {
            if (unsavedChanges && lastSnackbar?.IsShown != true) {
                lastSnackbar = MakeSnackbar(R.String.press_again_to_quit, Snackbar.LengthShort);
                lastSnackbar.SetAction(R.String.save_and_quit, (x) => { if (Save()) Finish(); })
                    .Show();
                return;
            }
            base.OnBackPressed();
        }

        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            if (item.ItemId == id_save) {
                Save();
            }
            return base.OnOptionsItemSelected(item);
        }

        private bool Save()
        {
            try {
                File.WriteAllText(currentFilePath, editText.Text, NaiveUtils.UTF8Encoding);
            } catch (Exception e) {
                MakeSnackbar(Resources.GetString(R.String.saving_error) + e.Message, Snackbar.LengthShort).Show();
                return false;
            }
            unsavedChanges = false;
            MakeSnackbar(R.String.file_saved, Snackbar.LengthLong).Show();
            return true;
        }
    }
}
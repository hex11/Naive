using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
            var path = paths.FirstOrDefault(x => File.Exists(x));
            if (path == null) currentFilePath = paths[0];
            OpenFile(path);
            editText.TextChanged += EditText_TextChanged;
        }

        private void OpenFile(string path)
        {
            currentFilePath = path;
            if (File.Exists(path) == false) {
                MakeSnackbar(R.String.no_config, Snackbar.LengthLong).Show();
                return;
            }
            try {
                editText.Text = File.ReadAllText(path, Encoding.UTF8);
                MakeSnackbar(string.Format(Resources.GetString(R.String.opened_config), path), Snackbar.LengthLong).Show();
            } catch (Exception e) {
                MakeSnackbar(Resources.GetString(R.String.saving_error) + e.Message, Snackbar.LengthLong).Show();
            }
        }

        private void EditText_TextChanged(object sender, Android.Text.TextChangedEventArgs e)
        {
            unsavedChanges = true;
        }

        const int id_save = 2331;
        const int id_encode = 2332;

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            menu.Add(Menu.None, id_save, Menu.None, R.String.save).SetShowAsAction(ShowAsAction.IfRoom);
            menu.Add(Menu.None, id_encode, Menu.None, R.String.copy_encoded_text).SetShowAsAction(ShowAsAction.Never);
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
                try {
                    TryDecode(out _);
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "OnBackPressed() decoding");
                    MakeSnackbar(Resources.GetString(R.String.saving_error) + e.Message, Snackbar.LengthShort).Show();
                }
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
                return true;
            } else if (item.ItemId == id_encode) {
                try {
                    TryDecode(out var text);
                    CopyEncoded(text);
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "config editor decoding/encoding");
                    MakeSnackbar(Resources.GetString(R.String.saving_error) + e.Message, Snackbar.LengthShort).Show();
                }
                return true;
            }
            return base.OnOptionsItemSelected(item);
        }

        private bool Save()
        {
            try {
                TryDecode(out var text);
                File.WriteAllText(currentFilePath, text, NaiveUtils.UTF8Encoding);
            } catch (Exception e) {
                Logging.exception(e, Logging.Level.Error, "config editor saving");
                MakeSnackbar(Resources.GetString(R.String.saving_error) + e.Message, Snackbar.LengthShort).Show();
                return false;
            }
            unsavedChanges = false;
            MakeSnackbar(R.String.file_saved, Snackbar.LengthLong).Show();
            return true;
        }

        const string Base64GzTag = "#base64gz\n";

        private bool TryDecode(out string result)
        {
            result = editText.Text;
            if (result.StartsWith(Base64GzTag)) {
                byte[] bytes = Convert.FromBase64String(result.Substring(Base64GzTag.Length));
                using (var gz = new GZipStream(new MemoryStream(bytes, false), CompressionMode.Decompress)) {
                    result = gz.ReadAllText();
                }
                editText.Text = result;
                return true;
            }
            return false;
        }

        private void CopyEncoded(string text)
        {
            var srcBytes = NaiveUtils.GetUTF8Bytes_AllocFromPool(text);
            byte[] gzBytes;
            int len;
            using (var ms = new MemoryStream()) {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) {
                    gz.Write(srcBytes);
                    BufferPool.GlobalPut(srcBytes.Bytes);
                }
                gzBytes = ms.GetBuffer();
                len = (int)ms.Length;
            }
            var encoded = Base64GzTag + Convert.ToBase64String(gzBytes, 0, len);
            var cs = this.GetSystemService(Context.ClipboardService) as ClipboardManager;
            cs.PrimaryClip = ClipData.NewPlainText("text", encoded);
            Toast.MakeText(this, R.String.copied, ToastLength.Short).Show();
        }
    }
}
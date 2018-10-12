using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Android.Support.V4.App;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Java.Lang;
using NaiveSocks;
using Naive.HttpSvr;
using Android.Content;
using Android.Views.InputMethods;
using Android.Graphics;

namespace NaiveSocksAndroid
{
    public class FragmentConsole : MyBaseFragment, TextView.IOnEditorActionListener
    {
        private LinearLayout linearLayout;
        private ScrollView scrollView;
        private TextView textView;
        private EditText editText;

        public FragmentConsole()
        {
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            linearLayout = new LinearLayout(this.Context) {
                Orientation = Orientation.Vertical
            };

            scrollView = new ScrollView(this.Context) {
                LayoutParameters = new LinearLayout.LayoutParams(-1, -1, 1.0f),
                FillViewport = true
            };

            textView = new TextView(this.Context) {
                Text = "(Console WIP)\n",
                Gravity = GravityFlags.Top,
                LayoutParameters = new ViewGroup.LayoutParams(-1, -2),
                Typeface = Typeface.Monospace,
                TextSize = 12,
            };
            textView.SetPadding(16, 16, 16, 16);

            editText = new EditText(this.Context) {
                Background = null,
                LayoutParameters = new LinearLayout.LayoutParams(-1, -2, 0f),
                Hint = "Input here",
                InputType = Android.Text.InputTypes.ClassText
            };
            editText.SetSingleLine();
            editText.ImeOptions = ImeAction.Send;
            editText.SetOnEditorActionListener(this);
            editText.FocusedByDefault = true;

            scrollView.AddView(textView);
            linearLayout.AddView(scrollView);
            linearLayout.AddView(editText);
            return linearLayout;
        }

        bool scrollingPending;

        void appendText(string text)
        {
            textView.Append(text);
            if (!scrollingPending) {
                scrollingPending = true;
                scrollView.Post(() => {
                    scrollingPending = false;
                    scrollView.FullScroll(FocusSearchDirection.Down);
                });
            }
        }

        public bool OnEditorAction(TextView v, [GeneratedEnum] ImeAction actionId, KeyEvent e)
        {
            if (actionId == ImeAction.Send) {
                appendText("input: " + v.Text + "\n");
                v.Text = "";
                return true;
            }
            return false;
        }
    }
}
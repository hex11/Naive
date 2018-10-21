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
using System.Collections.Concurrent;
using Naive.Console;

namespace NaiveSocksAndroid
{
    public class FragmentConsole : MyBaseFragment, TextView.IOnEditorActionListener
    {
        private LinearLayout linearLayout;
        private ScrollView scrollView;
        private TextView textView;
        private EditText editText;

        ConsoleHub consoleHub;

        BlockingCollection<string> inputLinesBuffer;

        ConsoleProxy proxy;

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
                Text = "",
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

            StartConsole();

            return linearLayout;
        }

        private void StartConsole()
        {
            inputLinesBuffer = new BlockingCollection<string>();
            proxy = new ConsoleProxy(this);

            consoleHub = new ConsoleHub();
            Commands.AddCommands(consoleHub.CommandHub, MainActivity.Service.Controller, "");
            new System.Threading.Thread(() => {
                try {
                    consoleHub.CommandHub.CmdLoop(proxy);
                } catch (System.Exception e) {
                    Logging.exception(e, Logging.Level.Warning, "ConsoleCmdLoop thread");
                }
            }) { Name = "ConsoleCmdLoop" }.Start();
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
                inputLinesBuffer.Add(v.Text);
                v.Text = "";
                return true;
            }
            return false;
        }

        public override void OnDestroy()
        {
            inputLinesBuffer.Add(null);
            base.OnDestroy();
        }

        class ConsoleProxy : Naive.Console.CmdConsole
        {
            bool readEOF = false;

            public ConsoleProxy(FragmentConsole con)
            {
                Con = con;
            }

            public FragmentConsole Con { get; }

            public override string ReadLine()
            {
                if (readEOF) {
                    throw new System.Exception("EOF");
                }
                var r = Con.inputLinesBuffer.Take();
                if (r == null) {
                    readEOF = true;
                } else {
                    WriteLineImpl(r);
                }
                return r;
            }

            protected override void WriteImpl(string text)
            {
                Con.textView.Post(() => {
                    if (!Con.IsDetached)
                        Con.appendText(text);
                });
            }

            protected override void WriteLineImpl(string text)
            {
                WriteImpl(text + "\n");
            }
        }
    }
}
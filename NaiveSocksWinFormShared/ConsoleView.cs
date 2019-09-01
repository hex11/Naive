using Naive.Console;
using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NaiveSocks.WinForm
{
    public class ConsoleView : UserControl
    {
        public class Tab : TabBase
        {
            public Tab()
            {
                ConsoleView = new ConsoleView();
            }

            public override string Name => "Console";
            public ConsoleView ConsoleView { get; }
            public override Control View => ConsoleView;

            public override void Destroy()
            {
                base.Destroy();
                ConsoleView.CloseConsole();
            }
        }

        public ConsoleView()
        {
            this.Size = new Size(500, 400);
            initControls();
            console = new Console(this);
            outputBox.HandleCreated += (s, e) => {
                new Thread(() => {
                    NaiveSocksCli.CommandHub.CmdLoop(console);
                }) { IsBackground = true, Name = "GUI command loop" }.Start();
            };
        }

        private TableLayoutPanel layout;
        private TextBox outputBox;
        private TextBox inputbox;

        private Console console;

        private void initControls()
        {
            this.SuspendLayout();
            this.Controls.Add(layout = new TableLayoutPanel() {
                RowCount = 2,
                ColumnCount = 1,
                Dock = DockStyle.Fill
            });
            outputBox = new TextBox() {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Multiline = true,
                BackColor = Color.Black,
                ForeColor = Color.LightGray,
                TabIndex = 1,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(new FontFamily("Consolas"), 10)
            };
            inputbox = new TextBox() {
                Dock = DockStyle.Fill,
                TabIndex = 0
            };
            inputbox.KeyPress += Inputbox_KeyPress;
            inputbox.KeyDown += Inputbox_KeyDown;
            layout.Controls.Add(outputBox, 0, 0);
            layout.Controls.Add(inputbox, 0, 1);
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0));
            this.ResumeLayout();
        }

        private void Inputbox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Modifiers == Keys.Control && e.KeyCode == Keys.D) {
                inputbox.Text = "";
                this.console.Input(null);
                outputBox.AppendText("^D\r\n");
            } else if (e.KeyCode == Keys.Up) {
                if (string.IsNullOrEmpty(lastInput) == false) {
                    inputbox.Text = lastInput;
                    inputbox.SelectionLength = 0;
                    inputbox.SelectionStart = lastInput.Length + 1;
                }
            }
        }

        private void Inputbox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r') {
                enter();
                e.Handled = true;
            }
        }

        string lastInput;

        private void enter()
        {
            var text = inputbox.Text;
            this.console.Input(text);
            inputbox.Text = "";
            if (text != "") {
                lastInput = text;
            }
        }

        public void Write(string text)
        {
            if (this.outputBox.IsDisposed || !this.outputBox.IsHandleCreated) return;
            try {
                this.outputBox.Invoke(new Action(() => this.outputBox.AppendText(text.Replace("\n", "\r\n"))));
            } catch (ObjectDisposedException) {
            }
        }

        public void CloseConsole()
        {
            this.console.Input(null, true);
        }

        class Console : CmdConsole
        {
            public Console(ConsoleView consoleView)
            {
                _consoleView = consoleView;
            }

            private BlockingCollection<string> inputBuffer = new BlockingCollection<string>();
            private bool closed;
            private bool closedAck;
            private readonly ConsoleView _consoleView;

            public override string ReadLine()
            {
                if (closedAck) throw new Exception("Console closed");
                var r = inputBuffer.Take();
                _consoleView.Write(r == null ? "[EOF]\n" : r + "\n");
                if (r == null && closed) closedAck = true;
                return r;
            }

            protected override void WriteImpl(string text)
            {
                _consoleView.Write(text);
            }

            protected override void WriteLineImpl(string text)
            {
                _consoleView.Write(text + "\r\n");
            }

            public void Input(string text, bool closed = false)
            {
                inputBuffer.Add(text);
                this.closed = closed;
            }
        }
    }
}

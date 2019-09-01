using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Naive.Console
{
    public class Command
    {
        public string fullcmd;
        public string name;
        public string arg;
        public string[] args;
        public int statusCode = 0;
        public CmdConsole Console { get; set; }
        public void Write(string text) => Console.Write(text);
        public void Write(string text, ConsoleColor color) => Console.Write(text, color);
        public void Write(string text, Color32 color) => Console.Write(text, color);
        public void WriteLine() => Console.WriteLine("");
        public void WriteLine(string text) => Console.WriteLine(text);
        public string ReadLine() => Console.ReadLine();
        public string ReadLine(string prompt) => Console.ReadLine(prompt);

        public string ArgOrNull(int index)
        {
            if (index < args.Length)
                return args[index];
            return null;
        }

        public string ReadLine(string prompt, string Default)
        {
            var line = ReadLine(prompt + $" [{Default}]:")?.Trim();
            if (line?.Length == 0)
                line = Default;
            return line;
        }

        private const string defaultPrompt = "Select: ";

        public string Select(string title, IDictionary<string, string> selections, string prompt = defaultPrompt, bool loopUntilSelect = true)
        {
            do {
                if (title != null)
                    Console.Write(title + "\n", ConsoleColor.Cyan);
                foreach (var item in selections) {
                    Console.Write($"    {item.Key + ")",3} ", ConsoleColor.White);
                    Console.WriteLine(item.Value);
                }
                var line = ReadLine(prompt);
                if (line == null)
                    throw new Exception("readline null.");
                if (selections.ContainsKey(line)) {
                    return line;
                }
            }
            while (loopUntilSelect);
            return null;
        }

        public int Select(string title, IList<string> selections, string prompt = defaultPrompt, bool loopUntilSelect = true)
        {
            var dict = new Dictionary<string, string>();
            int i = 0;
            foreach (var item in selections) {
                dict.Add((++i).ToString(), item);
            }
            var key = Select(title, dict, prompt, loopUntilSelect);
            if (key == null)
                return -1;
            return int.Parse(key) - 1;
        }

        public string SelectString(string title, IList<string> selections, string prompt = defaultPrompt, bool loopUntilSelect = true)
        {
            var index = Select(title, selections, prompt, loopUntilSelect);
            if (index < 0)
                return null;
            return selections[index];
        }

        public T Select<T>(string title, IDictionary<string, T> selections, string prompt = defaultPrompt, bool loopUntilSelect = true)
        {
            var dict = new Dictionary<string, string>();
            var key = SelectString(title, (from x in selections select x.Key).ToList(), prompt, loopUntilSelect);
            if (key == null)
                return default(T);
            return selections[key];
        }

        public bool YesOrNo(string prompt, bool? Default)
        {
            while (true) {
                var line = ReadLine(prompt + (Default == null ? " (y/n): " : Default.Value ? " (Y/n): " : " (y/N): "))?.Trim();
                if (line == null)
                    throw new Exception();
                if (line.Length == 0) {
                    if (Default != null)
                        return Default.Value;
                    else
                        continue;
                }
                bool? v = "yes".StartsWith(line, StringComparison.OrdinalIgnoreCase) ? true :
                        "no".StartsWith(line, StringComparison.OrdinalIgnoreCase) ? false :
                        (bool?)null;
                if (v == null)
                    continue;
                return v.Value;
            }
        }

        public Command()
        {
        }

        public Command(string name, string arg)
            : this(name, arg, SplitArguments(arg))
        {
        }

        public Command(string name, string arg, string[] args)
        {
            this.name = name;
            this.arg = arg;
            this.args = args;
            if (arg != null) {
                this.fullcmd = name + " " + arg;
            } else {
                this.fullcmd = name;
            }
        }

        public static Command FromCmdline(string str)
        {
            Command cmd = FromArray(SplitArguments(str));
            cmd.fullcmd = str;
            return cmd;
        }

        public static Command FromArray(string[] splits)
        {
            var cmd = new Command();
            cmd.name = splits[0];
            if (splits.Length > 0) {
                cmd.args = splits.Skip(1).ToArray();
            }
            // TODO: cmd.fullcmd & cmd.arg
            return cmd;
        }

        public static string[] SplitArguments(string input)
        {
            List<string> args = new List<string>();
            var sb = new StringBuilder();
            int len = input.Length;
            bool escaped = false;
            bool quoted = false;
            for (int i = 0; i < len; i++) {
                var ch = input[i];
                if (escaped) {
                    if (ch == '\\') {
                        sb.Append(ch);
                    } else if (ch == '"') {
                        sb.Append(ch);
                    } else {
                        sb.Append('\\');
                        sb.Append(ch);
                    }
                    escaped = false;
                    continue;
                }
                if (ch == '\\') {
                    escaped = true;
                } else if (ch == '"') {
                    if (quoted) {
                        quoted = false;
                    } else {
                        quoted = true;
                        continue;
                    }
                } else if (ch == ' ') {
                    if (quoted) {
                        sb.Append(ch);
                        continue;
                    } else {
                        if (sb.Length > 0) {
                            args.Add(sb.ToString());
                            sb.Clear();
                        }
                    }
                } else {
                    sb.Append(ch);
                }
            }
            if (sb.Length > 0)
                args.Add(sb.ToString());
            return args.ToArray();
        }
    }

    public struct Color32
    {
        public byte A, R, G, B;

        private static readonly Color32[] consoleColors = new Color32[] {
            new Color32{A=255, R=12, G=12, B=12},
            new Color32{A=255, R=0, G=55, B=218},
            new Color32{A=255, R=19, G=161, B=14},
            new Color32{A=255, R=58, G=150, B=221},
            new Color32{A=255, R=197, G=15, B=31},
            new Color32{A=255, R=136, G=23, B=152},
            new Color32{A=255, R=193, G=156, B=0},
            new Color32{A=255, R=204, G=204, B=204},
            new Color32{A=255, R=118, G=118, B=118},
            new Color32{A=255, R=59, G=120, B=255},
            new Color32{A=255, R=22, G=198, B=12},
            new Color32{A=255, R=97, G=214, B=214},
            new Color32{A=255, R=231, G=72, B=86},
            new Color32{A=255, R=180, G=0, B=158},
            new Color32{A=255, R=249, G=241, B=165},
            new Color32{A=255, R=242, G=242, B=242}
        };
        // https://blogs.msdn.microsoft.com/commandline/2017/08/02/updating-the-windows-console-colors/

        public ConsoleColor ToConsoleColor()
        {
            var c = this;
            for (int i = 0; i < consoleColors.Length; i++) {
                if (c == consoleColors[i])
                    return (ConsoleColor)i;
            }
            int index = (c.R > 128 | c.G > 128 | c.B > 128) ? 8 : 0; // Bright bit
            index |= (c.R > 64) ? 4 : 0; // Red bit
            index |= (c.G > 64) ? 2 : 0; // Green bit
            index |= (c.B > 64) ? 1 : 0; // Blue bit
            return (ConsoleColor)index;
        }

        public static Color32 FromConsoleColor(ConsoleColor c)
        {
            if ((int)c > consoleColors.Length || (int)c < 0)
                c = ConsoleColor.White;
            return consoleColors[(int)c];
        }

        public override bool Equals(object obj)
        {
            if (!(obj is Color32 other)) {
                return false;
            }
            return this == other;
        }

        public override int GetHashCode()
        {
            return A + (R << 8) + (G << 16) + (B << 24);
        }

        public static bool operator ==(Color32 a, Color32 b)
        {
            return a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;
        }

        public static bool operator !=(Color32 a, Color32 b)
        {
            return a.A != b.A || a.R != b.R || a.G != b.G || a.B != b.B;
        }
    }

    public abstract class CmdConsole
    {
        public CmdConsole()
        {
            ResetColor();
        }

        public static ConsoleOnStdIO StdIO { get; } = new ConsoleOnStdIO();
        public static CmdConsole Null { get; } = new nullConsole();
        protected abstract void WriteImpl(string text);

        public abstract string ReadLine();

        public bool LastCharIsNewline;

        public bool PromptColorEnabled { get; set; }
        public Color32 PromptColor32 { get; set; }

        public bool CustomColorEnabled;
        private Color32 _foregroundColor32;

        public Color32 ForegroundColor32
        {
            get { return _foregroundColor32; }
            set {
                _foregroundColor32 = value;
                CustomColorEnabled = true;
            }
        }

        public ConsoleColor ForegroundColor
        {
            get { return ForegroundColor32.ToConsoleColor(); }
            set { ForegroundColor32 = Color32.FromConsoleColor(value); }
        }

        public void ResetColor()
        {
            PromptColorEnabled = true;
            PromptColor32 = Color32.FromConsoleColor(ConsoleColor.Green);
            CustomColorEnabled = false;
            _foregroundColor32 = default(Color32);
        }

        public void Write(string text, ConsoleColor conColor)
        {
            Write(text, Color32.FromConsoleColor(conColor));
        }

        public void Write(string text, Color32 color)
        {
            // save color state:
            var _custom = CustomColorEnabled;
            var _color = _foregroundColor32;
            ForegroundColor32 = color;
            Write(text);
            // restore state:
            CustomColorEnabled = _custom;
            _foregroundColor32 = _color;
        }

        public void Write(string text)
        {
            CheckOutput(text);
            WriteImpl(text);
        }

        public void WriteLine(string text)
        {
            LastCharIsNewline = true;
            WriteLineImpl(text);
        }

        private void CheckOutput(string text)
        {
            if (text?.Length > 0) {
                LastCharIsNewline = text[text.Length - 1] == '\n';
            }
        }

        public string ReadLine(string prompt)
        {
            CheckOutput(prompt);
            return ReadLineImpl(prompt);
        }

        protected virtual void WriteLineImpl(string text)
        {
            WriteImpl(text + "\n");
        }

        protected virtual string ReadLineImpl(string prompt)
        {
            if (PromptColorEnabled) {
                Write(prompt, PromptColor32);
            } else {
                Write(prompt);
            }
            return ReadLine();
        }

        public void RunCommand(Command cmd, CommandHandler handler)
        {
            cmd.Console = this;
            try {
                handler(cmd);
            } catch (Exception e) {
                cmd.statusCode = -2;
                Write("cmd '" + cmd.fullcmd + "' exception:\n", ConsoleColor.Red);
                WriteLine(e.ToString());
            }
        }

        public class ConsoleOnStdIO : CmdConsole
        {
            public static object Lock = new object();
            public override string ReadLine() => System.Console.ReadLine();
            protected override void WriteImpl(string text)
            {
                lock (Lock)
                    if (!CustomColorEnabled) {
                        System.Console.Write(text);
                    } else {
                        System.Console.ForegroundColor = ForegroundColor;
                        System.Console.Write(text);
                        System.Console.ResetColor();
                    }
            }

            protected override void WriteLineImpl(string text)
            {
                lock (Lock)
                    if (!CustomColorEnabled) {
                        System.Console.WriteLine(text);
                    } else {
                        System.Console.ForegroundColor = ForegroundColor;
                        System.Console.WriteLine(text);
                        System.Console.ResetColor();
                    }
            }
        }

        private class nullConsole : CmdConsole
        {
            public override string ReadLine() => null;

            protected override void WriteImpl(string text)
            {
            }

            protected override void WriteLineImpl(string text)
            {
            }
        }
    }

    public delegate void CommandHandler(Command c);

    public class CommandInfo
    {
        public CommandInfo(string name, CommandHandler handler)
        {
            this.Name = name;
            this.Handler = handler;
        }

        public CommandHandler Handler;
        public string Name;
        public string Help;
    }
}

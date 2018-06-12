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

        public static Command FromString(string str)
        {
            str = str.Trim();
            var splits = str.Split(new char[] { ' ' }, 2);
            var cmd = new Command();
            cmd.fullcmd = str;
            cmd.name = splits[0];
            if (splits.Length > 1) {
                cmd.arg = splits[1];
                cmd.args = SplitArguments(splits[1]);
            } else {
                cmd.arg = "";
                cmd.args = new string[0];
            }
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

        static Color32[] consoleColors = new Color32[] {
            new Color32{A=0xFF, R=0x00, G=0x00, B=0x00},
            new Color32{A=0xFF, R=0x00, G=0x00, B=0x8B},
            new Color32{A=0xFF, R=0x00, G=0x64, B=0x00},
            new Color32{A=0xFF, R=0x00, G=0x8B, B=0x8B},
            new Color32{A=0xFF, R=0x8B, G=0x00, B=0x00},
            new Color32{A=0xFF, R=0x8B, G=0x00, B=0x8B},
            new Color32{A=0xFF, R=0xD7, G=0xC3, B=0x2A},
            new Color32{A=0xFF, R=0x80, G=0x80, B=0x80},
            new Color32{A=0xFF, R=0xA9, G=0xA9, B=0xA9},
            new Color32{A=0xFF, R=0x00, G=0x00, B=0xFF},
            new Color32{A=0xFF, R=0x00, G=0x80, B=0x00},
            new Color32{A=0xFF, R=0x00, G=0xFF, B=0xFF},
            new Color32{A=0xFF, R=0xFF, G=0x00, B=0x00},
            new Color32{A=0xFF, R=0xFF, G=0x00, B=0xFF},
            new Color32{A=0xFF, R=0xFF, G=0xFF, B=0x00},
            new Color32{A=0xFF, R=0xFF, G=0xFF, B=0xFF}
        };

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
                return consoleColors[0];
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
        public abstract void Write(string text);
        public abstract void WriteLine(string text);
        public abstract string ReadLine();

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

        public virtual string ReadLine(string prompt)
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
            public override void Write(string text)
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

            public override void WriteLine(string text)
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

            public override void Write(string text)
            {
            }

            public override void WriteLine(string text)
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

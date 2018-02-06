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
                    WriteLine(title);
                foreach (var item in selections) {
                    WriteLine($"    {item.Key}: {item.Value}");
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

        public string Select(string title, IList<string> selections, string prompt = defaultPrompt, bool loopUntilSelect = true)
        {
            var dict = new Dictionary<string, string>();
            int i = 0;
            foreach (var item in selections) {
                dict.Add((++i).ToString(), item);
            }
            var key = Select(title, dict, prompt, loopUntilSelect);
            if (key == null)
                return null;
            return selections[int.Parse(key) - 1];
        }

        public T Select<T>(string title, IDictionary<string, T> selections, string prompt = defaultPrompt, bool loopUntilSelect = true)
        {
            var dict = new Dictionary<string, string>();
            var key = Select(title, (from x in selections select x.Key).ToList(), prompt, loopUntilSelect);
            if (key == null)
                return default(T);
            return selections[key];
        }

        public bool YesOrNo(string prompt, bool Default)
        {
            while (true) {
                var line = ReadLine(prompt + (Default ? " (Y/n): " : " (y/N): "))?.Trim();
                if (line == null || line?.Length == 0)
                    return Default;
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

    public abstract class CmdConsole
    {
        public static ConsoleOnStdIO StdIO { get; } = new ConsoleOnStdIO();
        public static CmdConsole Null { get; } = new nullConsole();
        public abstract void Write(string text);
        public abstract void WriteLine(string text);
        public abstract string ReadLine();
        public virtual string ReadLine(string prompt)
        {
            Write(prompt);
            return ReadLine();
        }

        public void RunCommand(Command cmd, CommandHandler handler)
        {
            cmd.Console = this;
            try {
                handler(cmd);
            } catch (Exception e) {
                cmd.statusCode = int.MinValue + 1;
                WriteLine("cmd '" + cmd.fullcmd + "' exception:");
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
                    System.Console.Write(text);
            }

            public override void WriteLine(string text)
            {
                lock (Lock)
                    System.Console.WriteLine(text);
            }

            public override string ReadLine(string prompt)
            {
                lock (Lock) {
                    System.Console.ForegroundColor = ConsoleColor.Green;
                    Write(prompt);
                    System.Console.ResetColor();
                }
                return ReadLine();
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

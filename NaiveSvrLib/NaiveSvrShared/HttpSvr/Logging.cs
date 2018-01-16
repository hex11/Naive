using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Naive.HttpSvr
{
    public delegate void LogEventHandler(Logging.Log log);

    public static class Logging
    {
        private static object lockLogsHistory = new object();

        public static event LogEventHandler Logged;

        public static bool HistroyEnabled = true;
        public static uint HistroyMax = 50;
        private static LinkedList<Log> logsHistory = new LinkedList<Log>();

        public static bool WriteLogToConsole = false;
        public static bool WriteLogToConsoleWithTime = true;
        public static bool WriteLogToConsoleIndentation = true;

        public struct Log
        {
            public Level level;
            public long runningTime;
            private string _timestamp;
            public string timestamp
            {
                get {
                    if (_timestamp == null) {
                        var sec = (runningTime / 1000).ToString();
                        var ms = runningTime % 1000;
                        _timestamp = $"[{time.ToString("yy/MM/dd HH:mm:ss")} ({sec}.{ms.ToString("000")}) {levelStr}]:";
                    }
                    return _timestamp;
                }
            }

            public string levelStr
            {
                get => level == Level.Warning ? "Warn" : level.ToString();
            }

            public string text;
            public DateTime time;
        }

        private static void log(Log log)
        {
            if (HistroyEnabled) {
                lock (lockLogsHistory) {
                    if (HistroyMax > 0)
                        logsHistory.AddLast(log);
                    while (logsHistory.Count > HistroyMax) {
                        logsHistory.RemoveFirst();
                    }
                }
            }
            if (WriteLogToConsole)
                writeToConsole(log);
            Logged?.Invoke(log);
        }

        public static object ConsoleLock = new object();
        private static void writeToConsole(Logging.Log log)
        {
            lock (ConsoleLock) {
                switch (log.level) {
                case Level.None:
                case Level.Info:
                    System.Console.ForegroundColor = ConsoleColor.White;
                    break;
                case Level.Warning:
                    System.Console.ForegroundColor = ConsoleColor.Yellow;
                    break;
                case Level.Error:
                    System.Console.ForegroundColor = ConsoleColor.Red;
                    break;
                }
                string stamp;
                stamp = (WriteLogToConsoleWithTime) ? log.timestamp : "[" + log.levelStr + "] ";
                System.Console.Write(stamp);
                System.Console.ResetColor();
                System.Console.WriteLine(processText(log.text, stamp.Length));
            }
        }

        static unsafe string processText(string text, int indent)
        {
            if (WriteLogToConsoleIndentation && text.Length < 16 * 1024 && text.Contains("\n")) {
                int indentCount = 0;
                for (int i = 0; i < text.Length; i++) {
                    var ch = text[i];
                    if (ch == '\n' && i != text.Length - 1)
                        indentCount++;
                }
                int size = text.Length + indentCount * indent;
                if (size < 4096) {
                    var buf = stackalloc char[size + 1];
                    _processText2(text, indent, buf);
                    text = new string(buf);
                } else {
                    fixed (char* buf = new char[size + 1]) {
                        _processText2(text, indent, buf);
                        text = new string(buf);
                    }
                }
            }
            return text;
        }

        private static unsafe void _processText2(string text, int indent, char* buf)
        {
            int c = 0;
            for (int i = 0; i < text.Length; i++) {
                var ch = text[i];
                if (ch == '\n' && i != text.Length - 1) {
                    putIndent(buf, ref c, indent);
                } else {
                    buf[c++] = ch;
                }
            }
            buf[c++] = '\0';
        }

        static unsafe void putIndent(char* buf, ref int c, int indentLength)
        {
            buf[c++] = '\n';
            for (int i = 0; i < indentLength - 1; i++)
                buf[c++] = ' ';
            buf[c++] = '|';
        }

        public static void clearLogsHistory()
        {
            lock (lockLogsHistory) {
                logsHistory.Clear();
            }
        }

        public static ICollection<Log> getLogsHistroyCollection()
        {
            return logsHistory;
        }

        public static Log[] getLogsHistoryArray()
        {
            lock (lockLogsHistory) {
                var logs = new Log[logsHistory.Count];
                int i = 0;
                var cur = logsHistory.First;
                while (cur != null && i < logs.Length) {
                    logs[i++] = cur.Value;
                    cur = cur.Next;
                }
                return logs;
            }
        }

        public static void log(string text)
        {
            log(text, Level.None);
        }

        public static void log(string text, Level level)
        {
            try {
                var runningTime = getRuntime();
                log(new Log() { time = DateTime.Now, runningTime = runningTime, text = text, level = level });
            } catch (Exception) { }
        }

        public static void logWithStackTrace(string text, Level level)
        {
            log(text + "\nStackTrace:\n" + new StackTrace(1), level);
        }

        [Conditional("DEBUG")]
        public static void debug(string text)
        {
            log(text, Level.Debug);
        }

        public static void info(string text)
        {
            log(text, Level.Info);
        }

        public static void warning(string text)
        {
            log(text, Level.Warning);
        }

        public static void error(string text)
        {
            log(text, Level.Error);
        }

        public static void exception(Exception ex, Level level = Level.None, string text = null)
        {
            try {
                if (ex == null)
                    log(text + " (Exception object is null!!!)\r\nStackTrace:\r\n" + new StackTrace(true).ToString(), level);
                else
                    log(getExceptionText(ex, text), level);
            } catch (Exception) { }
        }

        public static string getExceptionText(Exception ex, string text)
        {
            var sb = new StringBuilder(256);
            sb.Append(text);
            var threadName = Thread.CurrentThread.Name;
            //if (string.IsNullOrEmpty(threadName) == false)
            //    sb.AppendFormat("\r\n(ThreadName: {0})", threadName);
            sb.Append("\r\n(").Append(ex.GetType().FullName).Append(") ").AppendLine(ex.Message);
            getStackTraceString(ex, sb);
            sb.AppendLine();
            int i = 0;
            while (ex.InnerException != null && i++ < 5) {
                ex = ex.InnerException;
                sb.AppendLine("   ----- Inner Exception -----");
                sb.Append($"({ex.GetType().FullName}): ");
                sb.AppendLine(ex.Message);
                getStackTraceString(ex, sb);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static HashSet<int> stackTraceHashes = new HashSet<int>();

        public static void getStackTraceString(Exception ex, StringBuilder sb)
        {
            var st = ex.StackTrace;
            if (st == null) {
                sb.Append("(No StackTrace)");
                return;
            }
            var hash = st.GetHashCode(); // TODO
            if (stackTraceHashes.Contains(hash)) {
                sb.Append("StackTrace #").Append(hash.ToString("X"));
            } else {
                stackTraceHashes.Add(hash);
                sb.Append("StackTrace (new) #").Append(hash.ToString("X"));
                sb.AppendLine();
                sb.Append(st);
            }
        }

        public static void flush()
        {
        }

        public enum Level
        {
            None,
            Debug,
            Info,
            Warning,
            Error
        }

        public static string toDetailString(this Exception ex)
        {
            var sb = new StringBuilder(1024);
            toDetailString(ex, sb);
            return sb.ToString();
        }

        public static void toDetailString(this Exception ex, StringBuilder sb)
        {
            sb.Append("\r\n");
            sb.AppendLine(ex.ToString());
            sb.AppendFormat("(ThreadName:{0})", Thread.CurrentThread.Name);
        }

        public static long getRuntime()
        {
            return (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        }
    }

    // TODO
    public class Logger
    {
        public event LogEventHandler Logged;

        public Logger BaseLogger;

        private void log(Logging.Log log)
        {
            throw new NotImplementedException();
        }

        public void log(string text, Logging.Level level)
        {
            throw new NotImplementedException();
        }

        [Conditional("DEBUG")]
        public void debug(string text)
        {
            log(text, Logging.Level.Debug);
        }

        public void info(string text)
        {
            log(text, Logging.Level.Info);
        }

        public void warning(string text)
        {
            log(text, Logging.Level.Warning);
        }

        public void error(string text)
        {
            log(text, Logging.Level.Error);
        }
    }
}

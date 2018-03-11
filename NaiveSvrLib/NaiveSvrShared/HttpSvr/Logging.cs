using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        //private static LinkedList<Log> logsHistory = new LinkedList<Log>();

        static LogBuffer logBuffer = new LogBuffer(32 * 1024);

        public static bool WriteLogToConsole = false;
        public static bool WriteLogToConsoleWithTime = true;
        public static bool WriteLogToConsoleIndentation = false;

        public static Logger RootLogger { get; } = new Logger(null, (c) => log(c));

        public struct Log
        {
            public Level level;
            public DateTime time;
            public long runningTime;

            public string text;

            public string _timestamp;
            public string timestamp
            {
                get {
                    if (_timestamp == null) {
                        var sec = (runningTime / 1000).ToString();
                        var ms = runningTime % 1000;
                        _timestamp = $"[{time.ToString("MM/dd HH:mm:ss")} ({sec}.{ms.ToString("000")}) {levelStr}]:";
                    }
                    return _timestamp;
                }
            }

            public string levelStr
            {
                get => level == Level.Warning ? "Warn" : level.ToString();
            }
        }

        private static void log(Log log)
        {
            if (HistroyEnabled) {
                //lock (lockLogsHistory) {
                //    if (HistroyMax > 0)
                //        logsHistory.AddLast(log);
                //    while (logsHistory.Count > HistroyMax) {
                //        logsHistory.RemoveFirst();
                //    }
                //}
                logBuffer.Add(log);
            }
            if (WriteLogToConsole)
                writeToConsole(log);
            Logged?.Invoke(log);
        }

        public static object ConsoleLock = new object();
        private static void writeToConsole(Logging.Log log)
        {
            lock (ConsoleLock) {
                System.Console.ForegroundColor = ConsoleColor.Black;
                switch (log.level) {
                default:
                case Level.None:
                    System.Console.BackgroundColor = ConsoleColor.DarkGray;
                    break;
                case Level.Info:
                    System.Console.BackgroundColor = ConsoleColor.Gray;
                    break;
                case Level.Warning:
                    System.Console.BackgroundColor = ConsoleColor.Yellow;
                    break;
                case Level.Error:
                    System.Console.BackgroundColor = ConsoleColor.Red;
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
            logBuffer.Clear();
        }

        public static ICollection<Log> getLogsHistroyCollection()
        {
            return logBuffer.GetLogs();
        }

        public static Log[] getLogsHistoryArray()
        {
            return logBuffer.GetLogs();
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

        public static long Runtime => getRuntime();
    }

    // TODO: update ResultingStamp when the stamp of parent changed.
    public class Logger
    {

        private bool isNullLogger;

        public static Logger Null { get; } = new Logger { isNullLogger = true };

        public Logger()
        {
        }

        public Logger(string stamp)
        {
            _stamp = stamp;
        }

        public Logger(string stamp, LogEventHandler logged)
        {
            _stamp = stamp;
            Logged += logged;
        }

        public Logger(string stamp, Logger parentLogger)
        {
            _stamp = stamp;
            _parentLogger = parentLogger;
            UpdateResultingStamp();
        }

        public Logger CreateChild(string stamp)
        {
            return new Logger(stamp, this);
        }

        public string ResultingStamp { get; private set; }

        void UpdateResultingStamp()
        {
            if (_parentLogger?.ResultingStamp.IsNullOrEmpty() != false || _stamp.IsNullOrEmpty())
                ResultingStamp = _parentLogger?.ResultingStamp ?? _stamp;
            else
                ResultingStamp = _parentLogger?.ResultingStamp + "." + _stamp;
        }

        event LogEventHandler _logged;
        public event LogEventHandler Logged
        {
            add {
                if (!isNullLogger)
                    _logged += value;
            }
            remove {
                if (!isNullLogger)
                    _logged -= value;
            }
        }

        private Logger _parentLogger;
        public Logger ParentLogger
        {
            get { return _parentLogger; }
            set {
                if (isNullLogger)
                    return;
                _parentLogger = value;
                UpdateResultingStamp();
            }
        }

        private string _stamp;
        public string Stamp
        {
            get { return _stamp; }
            set {
                if (isNullLogger)
                    return;
                _stamp = value;
                UpdateResultingStamp();
            }
        }

        private void log(Logging.Log log)
        {
            if (isNullLogger)
                return;
            _parentLogger?.log(log);
            _logged?.Invoke(log);
        }

        public void log(string text, Logging.Level level)
        {
            if (isNullLogger)
                return;
            log(new Logging.Log {
                text = ResultingStamp.IsNullOrEmpty() ? text : "<" + ResultingStamp + ">: " + text,
                level = level,
                runningTime = Logging.Runtime,
                time = DateTime.Now
            });
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

        public void exception(Exception ex, Logging.Level level = Logging.Level.None, string text = null)
        {
            try {
                if (ex == null)
                    log(text + " (Exception object is null!!!)\r\nStackTrace:\r\n" + new StackTrace(true).ToString(), level);
                else
                    log(Logging.getExceptionText(ex, text), level);
            } catch (Exception) { }
        }
    }

    public unsafe class LogBuffer
    {
        static UTF8Encoding Encoding => NaiveUtils.UTF8Encoding;

        public LogBuffer(int size)
        {
            this.bufferSize = size;
            buffer = (byte*)Marshal.AllocHGlobal(size);
            GC.AddMemoryPressure(size);
            logs = new Queue<myLog>(1024);
        }

        ~LogBuffer()
        {
            Marshal.FreeHGlobal((IntPtr)buffer);
            GC.RemoveMemoryPressure(bufferSize);
        }

        Queue<myLog> logs;

        public object Lock => logs;

        byte* buffer;
        int bufferSize;
        int head, tail;

        int remaining => (tail > head) ? tail - head - 1 : bufferSize - head + tail;
        int remainingSeq => (tail > head) ? tail - head - 1 : bufferSize - head;

        public void Add(Logging.Log log)
        {
            lock (logs) {
                var ml = new myLog(log);
                var str = log.text;
                var strLen = ml.strLen = str.Length;
                var bufLen = ml.bufLen = Encoding.GetByteCount(str);
                if (bufLen > bufferSize)
                    return;
                while (remainingSeq < bufLen) {
                    if (tail < head)
                        head = 0;
                    Shift();
                }
                ml.bufOffset = head;
                head += bufLen;
                if (bufLen > 0)
                    fixed (char* pStr = str) {
                        Encoding.GetBytes(pStr, strLen, buffer + ml.bufOffset, bufLen);
                    }
                logs.Enqueue(ml);
            }
        }

        void Shift()
        {
            logs.Dequeue();
            if (logs.Count > 0) {
                tail = logs.Peek().bufOffset;
            } else {
                tail = head;
            }
        }

        public void SetBufferSize(int newBufferSize)
        {
            if (newBufferSize == bufferSize)
                return;
            lock (logs) {
                if (newBufferSize > bufferSize || (head < bufferSize && tail < head)) {
                    ResizeBuffer(newBufferSize);
                } else {
                    // TODO
                }
            }
        }

        private void ResizeBuffer(int newBufferSize)
        {
            buffer = (byte*)Marshal.ReAllocHGlobal((IntPtr)buffer, (IntPtr)newBufferSize);
        }

        public void Clear()
        {
            lock (logs) {
                logs.Clear();
                head = tail = 0;
            }
        }

        public Logging.Log[] GetLogs()
        {
            lock (logs) {
                var ret = new Logging.Log[logs.Count];
                var i = 0;
                foreach (var item in logs) {
                    ret[i++] = item.GetLog(this);
                }
                return ret;
            }
        }

        struct myLog
        {
            public myLog(Logging.Log log)
            {
                level = log.level;
                time = log.time;
                runningTime = log.runningTime;
                bufOffset = bufLen = strLen = 0;
            }

            public Logging.Level level;
            public DateTime time;
            public long runningTime;
            public int bufOffset, bufLen, strLen;

            public Logging.Log GetLog(LogBuffer logBuffer)
            {
                var l = new Logging.Log {
                    level = this.level,
                    time = this.time,
                    runningTime = this.runningTime,
                };
                if (strLen == 0) {
                    l.text = "";
                } else {
                    var str = new string('\0', strLen);
                    fixed (char* pStr = str)
                        Encoding.GetChars(logBuffer.buffer + bufOffset, bufLen, pStr, strLen);
                    l.text = str;
                }
                return l;
            }
        }
    }
}

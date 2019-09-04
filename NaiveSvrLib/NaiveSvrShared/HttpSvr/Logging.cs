using System;
using System.Collections;
using System.Collections.Concurrent;
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

        public static bool AsyncLogging = false;

        public static event LogEventHandler Logged;

        public static bool HistroyEnabled = true;
        public static uint HistroyMax = 50;

        static LogBuffer logBuffer = new LogBuffer(64 * 1024);

        public static bool WriteLogToConsole = false;
        public static bool WriteLogToConsoleWithTime = true;
        public static bool WriteLogToConsoleIndentation = false;

        public static Logger RootLogger { get; } = new Logger(null, (c) => _log(c));

        static int lastLogId;

        public static int IncrLogId() => Interlocked.Increment(ref lastLogId);
        public static int GetLastLogId() => lastLogId;

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
                        _timestamp = "[" + time.ToString("MM/dd HH:mm:ss") + " (" + sec + "." + ms.ToString("000") + ") " + levelStr + "]:";
                    }
                    return _timestamp;
                }
            }

            public string levelStr
            {
                get => level == Level.Warning ? "Warn" : level.ToString();
            }

            public static object ConsoleLock = new object();
            public void WriteToConsole()
            {
                lock (ConsoleLock) {
                    WriteToConsoleWithoutLock();
                }
            }

            public void WriteToConsoleWithoutLock()
            {
                System.Console.ForegroundColor = ConsoleColor.Black;
                switch (this.level) {
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
                string stamp = (WriteLogToConsoleWithTime) ? this.timestamp : "[" + this.levelStr + "] ";
                System.Console.Write(stamp);
                System.Console.ResetColor();
                System.Console.WriteLine(processText(this.text, stamp.Length));
            }
        }

        private static void log(Log log)
        {
            RootLogger.log(log);
        }

        // Printing log to console is really slow, so there is "async logging".
        // TODO: the process cannot quit until the async logging queue flushed.
        private static Thread _loggingThread;
        private static BlockingCollection<Log> _logQueue;

        private static void _log(Log log)
        {
            if (AsyncLogging) {
                if (_logQueue == null) {
                    var queue = new BlockingCollection<Log>();
                    if (Interlocked.CompareExchange(ref _logQueue, queue, null) == null) {
                        _loggingThread = new Thread(_logThreadMain) { IsBackground = true, Name = "LoggingThread" };
                        _loggingThread.Start();
                    }
                }
                _logQueue.Add(log);
            } else {
                _logCore(log);
            }
        }

        private static void _logThreadMain()
        {
            while (true) {
                Log log = _logQueue.Take();
                _logCore(log);
            }
        }

        private static void _logCore(Log log)
        {
            if (HistroyEnabled) {
                logBuffer.Add(log);
            }
            if (WriteLogToConsole)
                log.WriteToConsole();
            Logged?.Invoke(log);
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

        public static Log[] getLogsHistoryArray()
        {
            return logBuffer.GetLogs();
        }

        public static int getLogsHistory(ArraySegment<Log> arrSeg)
        {
            return logBuffer.GetLastLogs(arrSeg);
        }

        public static void GetLogsStat(out int minIndex, out int count)
        {
            minIndex = logBuffer.MinIndex;
            count = logBuffer.Count;
        }

        public static Logging.Log? TryGetLog(int index) => TryGetLog(index, true);
        public static Logging.Log? TryGetLog(int index, bool withText) => logBuffer.TryGetLog(index, withText);

        public static void log(string text)
        {
            log(text, Level.None);
        }

        public static void log(string text, Level level)
        {
            try {
                log(CreateLog(level, text));
            } catch (Exception) { }
        }

        public static Log CreateLog(Level level, string text)
        {
            return new Log() { time = DateTime.Now, runningTime = getRuntime(), text = text, level = level };
        }

        public static void logWithStackTrace(string text, Level level)
        {
            var sb = new StringBuilder(128);
            sb.AppendLine(text);
            GetStackTraceString(new StackTrace(1), sb);
            sb.AppendLine();
            log(sb.ToString(), level);
        }

        [Conditional("DEBUG")]
        public static void debug(string text)
        {
            log(text, Level.Debug);
        }

        public static void debugForce(string text)
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

        public static void errorAndThrow(string text)
        {
            log(text, Level.Error);
            throw new Exception(text);
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
            GetStackTraceString(st, sb);
        }

        public static void GetStackTraceString(StackTrace st, StringBuilder sb)
        {
            GetStackTraceString(st.ToString(), sb);
        }

        private static void GetStackTraceString(string st, StringBuilder sb)
        {
            if (st == null) {
                sb.Append("(No StackTrace)");
                return;
            }
            var hash = st.GetHashCode(); // TODO
            bool exists;
            lock (stackTraceHashes) {
                exists = stackTraceHashes.Contains(hash);
                if (!exists)
                    stackTraceHashes.Add(hash);
            }
            sb.Append("StackTrace");
            if (!exists)
                sb.Append(" (new)");
            sb.Append(" #").Append(hash.ToString("X")).Append(" (").Append(st.Length).Append(" chars)");
            if (!exists) {
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

        static long StartTimeTicks = -1;

        public static long getRuntime()
        {
            var tmp = CustomRunningTimeImpl;
            if (tmp != null) {
                return tmp();
            }

            if (StartTimeTicks == -1) {
                StartTimeTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            }
            return (DateTime.UtcNow.Ticks - StartTimeTicks) / 10000;
        }

        public static long Runtime => getRuntime();

        public static Func<long> CustomRunningTimeImpl;
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

        public void log(Logging.Log log)
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

        public void debugForce(string text)
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

        public void logWithStackTrace(string text, Logging.Level level)
        {
            var sb = new StringBuilder(128);
            sb.AppendLine(text);
            Logging.GetStackTraceString(new StackTrace(1), sb);
            sb.AppendLine();
            log(sb.ToString(), level);
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
            logs = new MyQueue<myLog>(256);
        }

        ~LogBuffer()
        {
            Marshal.FreeHGlobal((IntPtr)buffer);
            GC.RemoveMemoryPressure(bufferSize);
        }

        MyQueue<myLog> logs;
        int minIndex;

        public int MinIndex => minIndex;
        public int Count => logs.Count;

        public object Lock => logs;

        byte* buffer;
        int bufferSize;
        int head, tail;

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
#if DEBUG
                GetLogs();
#endif
            }
        }

        void Shift()
        {
            logs.Dequeue();
            minIndex++;
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
                    if (newBufferSize > bufferSize) {
                        GC.AddMemoryPressure(newBufferSize - bufferSize);
                    } else {
                        GC.RemoveMemoryPressure(bufferSize - newBufferSize);
                    }
                    ResizeBuffer(newBufferSize);
                    bufferSize = newBufferSize;
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

        public Logging.Log? TryGetLog(int index, bool withText)
        {
            lock (logs) {
                if (index < MinIndex || index >= MinIndex + Count)
                    return null;
                return logs.PeekAt(index - MinIndex).GetLog(this, withText);
            }
        }

        public Logging.Log[] GetLogs()
        {
            lock (logs) {
                int count = logs.Count;
                if (count == 0)
                    return new Logging.Log[0];
                var ret = new Logging.Log[count];
                for (int i = 0; i < count; i++) {
                    ret[i] = logs.PeekAt(i).GetLog(this, true);
                }
                return ret;
            }
        }

        public void GetLogs(int beginIdx, int count, Logging.Log[] arr, out int skipped)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            skipped = 0;
            int endIdx = beginIdx + count;
            lock (logs) {
                if (beginIdx < MinIndex) {
                    skipped = MinIndex - beginIdx;
                    beginIdx = MinIndex;
                    if (beginIdx >= endIdx) {
                        skipped = count;
                        return;
                    }
                }
                int arrIdx = 0;
                for (int i = beginIdx; i < endIdx; i++) {
                    arr[arrIdx++] = logs.PeekAt(i).GetLog(this, true);
                }
            }
        }

        public int GetLastLogs(ArraySegment<Logging.Log> arrseg)
        {
            lock (logs) {
                var array = arrseg.Array;
                var count = arrseg.Count;
                count = Math.Min(logs.Count, count);
                int beginIdx = logs.Count - count;
                int arrayIdx = arrseg.Offset;
                for (int i = 0; i < count; i++) {
                    array[arrayIdx + i] = logs.PeekAt(beginIdx + i).GetLog(this, true);
                }
                return count;
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
#if DEBUG
                hash = log.text.GetHashCode();
#endif
            }

            public Logging.Level level;
            public DateTime time;
            public long runningTime;
            public int bufOffset, bufLen, strLen;

#if DEBUG
            int hash;
#endif

            public Logging.Log GetLog(LogBuffer logBuffer, bool withText)
            {
                var l = new Logging.Log {
                    level = this.level,
                    time = this.time,
                    runningTime = this.runningTime,
                };
                if (withText) {
                    if (strLen == 0) {
                        l.text = "";
                    } else {
                        var str = new string('\0', strLen);
                        fixed (char* pStr = str)
                            Encoding.GetChars(logBuffer.buffer + bufOffset, bufLen, pStr, strLen);
                        l.text = str;
                    }
                }
#if DEBUG
                if (withText && l.text.GetHashCode() != hash) {
                    Console.CmdConsole.StdIO.Write("Log checksum failed!\n", Console.Color32.FromConsoleColor(ConsoleColor.Red));
                }
#endif
                return l;
            }
        }
    }

    public class LogFileWriter
    {
        StreamWriter sw;
        bool running;

        public LogFileWriter(string logFile, Logger logger)
        {
            var dir = Path.GetDirectoryName(logFile);
            if (!dir.IsNullOrEmpty()) Directory.CreateDirectory(dir);
            var fs = File.Open(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            sw = new StreamWriter(fs, NaiveUtils.UTF8Encoding);
            LogFile = logFile;
            Logger = logger;
        }

        public string LogFile { get; }
        public Logger Logger { get; }

        public void Start()
        {
            running = true;
            Logger.Logged += Logging_Logged;
            lock (sw) {
                sw.WriteLine();
                sw.WriteLine("==========LOG BEGIN========== at " + DateTime.Now.ToString("u"));
                sw.WriteLine();
                Flush();
            }
        }

        public void WriteHistoryLog()
        {
            foreach (var item in Logging.getLogsHistoryArray()) {
                Logging_Logged(item);
            }
        }

        public void Stop()
        {
            lock (sw) {
                running = false;
                Logger.Logged -= Logging_Logged;
                sw.Close();
            }
        }

        private void Logging_Logged(Logging.Log x)
        {
            lock (sw) {
                if (!running) return;
                sw.Write(x.timestamp);
                sw.WriteLine(x.text);
                Flush();
            }
        }

        private void Flush()
        {
            sw.Flush();
        }
    }
}

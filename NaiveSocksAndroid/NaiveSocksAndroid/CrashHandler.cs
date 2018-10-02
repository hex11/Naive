using Android.Content;
using Android.Util;
using Naive.HttpSvr;
using System;
using System.IO;
using System.Threading;

namespace NaiveSocksAndroid
{

    static class CrashHandler
    {
        static CrashHandler()
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogFile));
            using (var sw = File.CreateText(CrashLogFile)) {
                sw.Write(e.ExceptionObject.ToString());
            }
            Logging.exception(e.ExceptionObject as Exception, Logging.Level.Error, "=========== FATAL EXCEPTION ===========");
        }

        public static string CrashLogFile = "/sdcard/NaiveUnhandledException.txt";

        static object nullOnInit = new object();

        public static void CheckInit()
        {
            var tmp = nullOnInit;
            if (tmp == null)
                return;
            lock (tmp) {
                if (nullOnInit == null) {
                    return;
                }
                nullOnInit = null;
            }
            new LogFileWriter(Path.Combine(Android.App.Application.Context.CacheDir.AbsolutePath, "log.txt"), Logging.RootLogger).Start();
            string sdcard = Android.OS.Environment.ExternalStorageDirectory.AbsolutePath;
            CrashLogFile = Path.Combine(sdcard, "NaiveUnhandledException.txt");
            ThreadPool.SetMinThreads(1, 1);
            ThreadPool.SetMaxThreads(1024, 2);
            Logging.CustomRunningTimeImpl = AndroidGetRunningTime;
            Logging.Logged += Logging_Logged;
            Logging.info("Process PID=" + Android.OS.Process.MyPid());

            var osArch = Java.Lang.JavaSystem.GetProperty("os.arch");
            Logging.info("os.arch = " + osArch);
            NaiveSocks.YASocket.isX86 = (osArch.StartsWith("x86") || osArch == "amd64");
        }

        static long processStartTime = -1;

        private static long AndroidGetRunningTime()
        {
            if (processStartTime == -1) {
                processStartTime = Android.OS.Process.StartElapsedRealtime;
            }

            return Android.OS.SystemClock.ElapsedRealtime() - processStartTime;
        }

        private static void Logging_Logged(Logging.Log log)
        {
            Log.WriteLine(GetPri(log), "naivelog", log.text);
        }

        private static LogPriority GetPri(Logging.Log log)
        {
            switch (log.level) {
            case Logging.Level.None:
                return LogPriority.Verbose;
            case Logging.Level.Debug:
                return LogPriority.Debug;
            case Logging.Level.Info:
                return LogPriority.Info;
            case Logging.Level.Warning:
                return LogPriority.Warn;
            case Logging.Level.Error:
                return LogPriority.Error;
            default:
                return LogPriority.Info;
            }
        }
    }
}
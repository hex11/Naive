using Android.Content;
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
            inited = true;
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogFile));
            using (var sw = File.CreateText(CrashLogFile)) {
                sw.Write(e.ExceptionObject.ToString());
            }
        }

        public static string CrashLogFile = "/sdcard/NaiveUnhandledException.txt";

        static bool inited = false;

        public static void CheckInit()
        {
            if (inited == false)
                throw new Exception("!!! inited == false !!!");
            string sdcard = Android.OS.Environment.ExternalStorageDirectory.AbsolutePath;
            CrashLogFile = Path.Combine(sdcard, "NaiveUnhandledException.txt");
            ThreadPool.SetMinThreads(1, 1);
            ThreadPool.SetMaxThreads(1024, 2);
        }
    }
}
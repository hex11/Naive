using Android.Content;
using System;
using System.IO;

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
            using (var sw = File.CreateText(CrashLogFile)) {
                sw.Write(e.ToString());
            }
        }

        public static string CrashLogFile = "/sdcard/NaiveUnhandledException.txt";

        static bool inited = false;

        public static void CheckInit()
        {
            if (inited == false)
                throw new Exception("!!! inited == false !!!");
            CrashLogFile = Path.Combine(Android.OS.Environment.ExternalStorageDirectory.AbsolutePath, "NaiveUnhandledException.txt");
        }
    }
}
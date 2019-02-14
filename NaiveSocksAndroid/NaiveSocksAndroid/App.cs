using Android.Content;
using Android.Util;
using Naive.Console;
using Naive.HttpSvr;
using NaiveSocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace NaiveSocksAndroid
{
    static class App
    {
        static App()
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

        private static string cacheDir;
        private static string logsDir;

        public static string DnsDbFile;

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
            var timestamp = DateTime.Now.ToString("yyyyMMddTHHmmss_fff");
            cacheDir = Android.App.Application.Context.CacheDir.AbsolutePath;
            logsDir = Path.Combine(cacheDir, "logs");
            new LogFileWriter(Path.Combine(logsDir, timestamp + ".txt"), Logging.RootLogger).Start();
            string sdcard = Android.OS.Environment.ExternalStorageDirectory.AbsolutePath;
            CrashLogFile = Path.Combine(sdcard, "NaiveUnhandledException.txt");
            ThreadPool.SetMinThreads(1, 1);
            ThreadPool.SetMaxThreads(1024, 2);
            Logging.CustomRunningTimeImpl = AndroidGetRunningTime;
            Logging.Logged += Logging_Logged;
            Logging.info("Process PID=" + Android.OS.Process.MyPid());
            DeleteOldLogs(logsDir);
            NaiveUtils.NoAsyncOnFileStream = true;

            var osArch = Java.Lang.JavaSystem.GetProperty("os.arch");
            Logging.info("os.arch = " + osArch);
            NaiveSocks.YASocket.isX86 = (osArch.StartsWith("x86") || osArch == "amd64");

            DnsDbFile = Path.Combine(cacheDir, "dns.litedb");

            AddCommands();

            AppConfig.Init(Android.App.Application.Context);
        }

        private static void AddCommands()
        {
            Commands.AdditionalCommands.Add(new KeyValuePair<string, CommandHandler>("dnsdb-dump", cmd => {
                var db = new LiteDB.LiteDatabase(DnsDbFile);
                int cols = 0;
                foreach (var collName in db.GetCollectionNames()) {
                    cmd.Write("===== COLLECTION START (" + collName + ") =====\n");
                    int docs = 0;
                    int idxs = 0;
                    var coll = db.GetCollection(collName);
                    foreach (var item in coll.FindAll()) {
                        cmd.Write("Doc: " + item.ToString() + "\n");
                        docs++;
                    }
                    foreach (var idx in coll.GetIndexes()) {
                        cmd.Write($"Idx [{idx.Field}] experssion [{idx.Expression}] unique [{idx.Unique}] slot [{idx.Slot}] maxLevel [{idx.MaxLevel}]\n");
                        idxs++;
                    }
                    cmd.Write("===== COLLECTION END (" + collName + ") " + docs + " documents " + idxs + " indexes =====\n");
                    cols++;
                }
                cmd.Write("(" + cols + " collections)\n");
            }));
            Commands.AdditionalCommands.Add(new KeyValuePair<string, CommandHandler>("dnsdb-drop", cmd => {
                cmd.Write("deleting: " + DnsDbFile + "\n");
                File.Delete(DnsDbFile);
            }));
            Commands.AdditionalCommands.Add(new KeyValuePair<string, CommandHandler>("dnsdb", cmd => {
                var bg = BgService.Instance;
                if (bg == null || !bg.TryGetTarget(out var t)) {
                    cmd.WriteLine("Failed to get BgService.");
                    cmd.statusCode = 1;
                    return;
                }
                var db = t.DnsDb;
                if (db == null) {
                    cmd.WriteLine("Failed to get DnsDb.");
                    cmd.statusCode = 1;
                    return;
                }
                var subcmd = cmd.ArgOrNull(0);
                if (subcmd == "stat") {
                    var fileSize = "error: ";
                    try {
                        fileSize = (new FileInfo(db.FilePath).Length / 1024).ToString("N0") + " KB";
                    } catch (Exception e) {
                        fileSize += e.Message;
                    }
                    cmd.WriteLine($"FileSize: {fileSize}");
                    cmd.WriteLine($"Count: {db.RecordCount():N0}");
                    cmd.WriteLine($"Inserts: {db.inserts:N0} times in {db.insertTotalTime:N0} ms");
                    cmd.WriteLine($"QueryByIp: {db.queryByIps:N0} times in {db.queryByIpTotalTime:N0} ms");
                    cmd.WriteLine($"QueryByDomain: {db.queryByDomains:N0} times in {db.queryByDomainTotalTime:N0} ms");
                } else if (subcmd == "ip") {
                    var r = db.QueryByIp((uint)IPAddress.Parse(cmd.ArgOrNull(1)).Address);
                    cmd.WriteLine("Result: " + (r ?? "(null)"));
                    if (r != null && cmd.ArgOrNull(2) == "del") {
                        cmd.WriteLine($"Deleted {(db.Delete(r) ? "1" : "0")} records");
                    }
                } else if (subcmd == "name") {
                    var name = cmd.ArgOrNull(1);
                    if (db.QueryByName(name, out var r)) {
                        cmd.WriteLine("Result: " + r);
                        if (cmd.ArgOrNull(2) == "del") {
                            cmd.WriteLine($"Deleted {(db.Delete(name) ? "1" : "0")} records");
                        }
                    } else {
                        cmd.WriteLine("No result.");
                    }
                } else if (subcmd == "clean") {
                    var time = DateTime.Now - NaiveUtils.ParseDuration(cmd.ArgOrNull(1) ?? "3d");
                    cmd.WriteLine($"Deleting records before {time}...");
                    var r = db.Clean(time);
                    cmd.WriteLine($"Deleted {r:N0}.");
                } else if (subcmd == "shrink") {
                    cmd.WriteLine("Shrinking...");
                    var delta = -db.Shrink();
                    cmd.WriteLine($"Shrinked, delta: {delta:N0}.");
                } else {
                    cmd.WriteLine("Unknown sub-command.\n" +
                        "Usage: dnsdb (stat|ip IP [del]|name NAME [del]|shrink|clean [EXPIRED_BEFORE])\n" +
                        "(EXPIRED_BEFORE default: 3d)");
                    cmd.statusCode = 1;
                }
            }));
            Commands.AdditionalCommands.Add(new KeyValuePair<string, CommandHandler>("logs-drop", cmd => {
                var files = Directory.GetFiles(logsDir);
                cmd.Write(files.Length + " files to delete.");
                foreach (var item in files) {
                    cmd.Write("deleting: " + item + "\n");
                    File.Delete(item);
                }
            }));
        }

        private static void DeleteOldLogs(string logsDir)
        {
            if (Directory.Exists(logsDir)) {
                var files = Directory.GetFiles(logsDir);
                if (files.Length > 10) {
                    foreach (var item in files.Take(files.Length - 10)) {
                        Logging.info("deleting: " + item);
                        File.Delete(item);
                    }
                }
            }
        }

        private static long processStartTime = -1;

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
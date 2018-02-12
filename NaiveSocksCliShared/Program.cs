﻿using System;
using System.Diagnostics;
using System.Threading;
using Naive.Console;
using Naive.HttpSvr;
using Naive;
using System.IO;
using System.Threading.Tasks;
using System.Linq;

namespace NaiveSocks
{
    internal class Program
    {
        public static string configFileName = "naivesocks.tml";
        public static string configFilePath = configFileName;

        public static string specifiedConfigPath;

        public static string[] GetConfigFilePaths()
        {
            string[] paths = {
                ".",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"+ Path.DirectorySeparatorChar + "nsocks"),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            for (int i = 0; i < paths.Length; i++) {
                paths[i] = Path.Combine(paths[i], configFilePath);
            }
            return paths;
        }

        private const string NAME = "NaiveSocks" +
#if DEBUG
                    " (Debug)";
#else
                    "";
#endif

        private static string verstionText => "v0.tan90+1.1" + (__magic_is_packed ? " (single file edition)" : "");

        private static string cmdHelpText => $@"{NAME} {verstionText}

Usage: {NAME}.exe [-h|--help] [(-c|--config) FILE] [--no-cli] [--no-log-stdout]
                  [--log-file FILE] [--log-stdout-no-time]
";

        private static bool __magic_is_packed;

        private static void Main(string[] args)
        {
            Console.Title = NAME;
            Logging.HistroyEnabled = false;
            Logging.WriteLogToConsole = true;
            CmdConsole.ConsoleOnStdIO.Lock = Logging.ConsoleLock;

            //ThreadPool.SetMaxThreads(Environment.ProcessorCount, Environment.ProcessorCount);

            var argumentParser = new ArgumentParser();
            argumentParser.AddArg(ParasPara.OnePara, "-c", "--config");
            argumentParser.AddArg(ParasPara.NoPara, "-h", "--help");
            var ar = argumentParser.ParseArgs(args);
            if (ar.ContainsKey("-h")) {
                Console.Write(cmdHelpText);
                return;
            }
            if (ar.ContainsKey("--no-log-stdout")) {
                Logging.WriteLogToConsole = false;
            }
            if (ar.ContainsKey("--stdout-no-time") || ar.ContainsKey("--log-stdout-no-time")) {
                Logging.WriteLogToConsoleWithTime = false;
            }
            if (ar.TryGetValue("--log-file", out var logFile)) {
                Logging.info($"Logging file: {logFile.FirstParaOrThrow}");
                initLogFile(logFile.FirstParaOrThrow);
            }
            if (ar.TryGetValue("-c", out var v)) {
                specifiedConfigPath = v.FirstParaOrThrow;
                Logging.info($"configuation file: {configFilePath}");
            }

            var controller = new Controller();
            long lastPackets = 0, lastBytes = 0;
            void updateTitle()
            {
                lock (CmdConsole.ConsoleOnStdIO.Lock) {
                    var p = MyStream.TotalCopiedPackets;
                    var b = MyStream.TotalCopiedBytes;
                    Console.Title = $"{NAME} - current/total {controller.InConnections.Count}/{controller.TotalHandledConnections} connections." +
                        $" copied {p:N0} Δ{p - lastPackets:N0} packets / {b:N0} Δ{b - lastBytes:N0} bytes";
                    lastPackets = p;
                    lastBytes = b;
                }
            }
            async Task updateTitleLoopTask(CancellationToken ct)
            {
                while (true) {
                    await Task.Delay(1000);
                    if (ct.IsCancellationRequested) {
                        Console.Title = NAME;
                        return;
                    }
                    updateTitle();
                }
            }
            CancellationTokenSource currentUpdateCTS = null;
            controller.ConfigTomlLoaded += (x) => {
                if (currentUpdateCTS != null) {
                    currentUpdateCTS.Cancel();
                    currentUpdateCTS = null;
                }
                if (x.TryGetValue("update_title", Environment.OSVersion.Platform == PlatformID.Win32NT)) {
                    currentUpdateCTS = new CancellationTokenSource();
                    updateTitleLoopTask(currentUpdateCTS.Token).Forget();
                }
            };
            Logging.info($"{NAME} {verstionText}");
            if (specifiedConfigPath != null) {
                Commands.loadController(controller, specifiedConfigPath);
            } else {
                var paths = GetConfigFilePaths();
                controller.LoadConfigFileFromMultiPaths(paths);
                controller.Start();
            }
            var cmdHub = new CommandHub();
            cmdHub.Prompt = $"{NAME}>";
            Commands.AddCommands(cmdHub, controller, null);
            cmdHub.AddCmdHandler("newbie", (cmd) => Commands.NewbieWizard(cmd, controller, specifiedConfigPath ?? configFilePath));
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                cmdHub.AddCmdHandler("openfolder", (cmd) => Process.Start("explorer", "."));
#if NS_WINFORM
            cmdHub.AddCmdHandler("gui", (cmd) => {
                var form = new GUI.FormConnections();
                Application.Run(form);
            });
#endif
            if (ar.ContainsKey("--no-cli")) {
                while (true)
                    Thread.Sleep(int.MaxValue);
            } else {
                cmdHub.CmdLoop(CmdConsole.StdIO);
            }
        }

        private static void initLogFile(string logFile)
        {
            var fs = File.Open(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var sw = new StreamWriter(fs, NaiveUtils.UTF8Encoding);
            var pendingFlush = false;
            var delayFlush = new Func<Task>(async () => {
                if (pendingFlush)
                    return;
                pendingFlush = true;
                await Task.Delay(100);
                lock (sw) {
                    pendingFlush = false;
                    sw.Flush();
                }
            });
            Logging.Logged += (x) => {
                lock (sw) {
                    sw.Write(x.timestamp);
                    sw.WriteLine(x.text);
                    delayFlush();
                }
            };
            lock (sw) {
                sw.WriteLine("==========LOG BEGIN==========");
                sw.Flush();
            }
        }
    }
}
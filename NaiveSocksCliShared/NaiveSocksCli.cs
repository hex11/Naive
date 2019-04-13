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
    internal class NaiveSocksCli
    {
        public static string configFileName = "naivesocks.tml";
        public static string configFilePath = configFileName;

        public static string specifiedConfigPath;
        public static string specifiedConfigContent;

        public static string[] GetConfigFilePaths()
        {
            string userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string userAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] paths = {
                ".",
                Path.Combine(userDir, ".config", "nsocks"),
                Path.Combine(userDir, ".config"),
                Path.Combine(userAppData, "nsocks"),
                userAppData,
                Path.Combine(userDir, "nsocks"),
                userDir,
            };
            for (int i = 0; i < paths.Length; i++) {
                paths[i] = Path.Combine(paths[i], configFilePath);
            }
            return paths.Distinct().ToArray();
        }

        private static string NameWithVertionText
            => BuildInfo.AppName + " v" + BuildInfo.Version
                    + (BuildInfo.CurrentBuildText.IsNullOrEmpty() ? null : " " + BuildInfo.CurrentBuildText)
                    + (__magic_is_packed ? " (single file)" : "");

        private static string cmdHelpText => $@"{NameWithVertionText}

Usage: {BuildInfo.AppName_NoDebug} [-h|--help] [-V|--version] [(-c|--config) FILE] [--config-stdin]
        [--no-cli] [--no-log-stdout] [--log-file FILE] [--log-stdout-no-time]
        [--force-jit[-async]] [--socket-impl (1|2|fa|ya)]
        [--cmd CMDLINE...]";

        internal static bool __magic_is_packed;

        public static Controller Controller { get; private set; }

        internal static void Main(string[] args)
        {
            Console.Title = BuildInfo.AppName;
            //Logging.HistroyEnabled = false;
            Logging.WriteLogToConsole = false;
            Logging.Logged += (log) => {
                lock (CmdConsole.ConsoleOnStdIO.Lock) {
                    if (CmdConsole.StdIO.LastCharIsNewline == false) {
                        Console.WriteLine();
                        CmdConsole.StdIO.LastCharIsNewline = true;
                    }
                    log.WriteToConsoleWithoutLock();
                }
            };

            LogFileWriter logWriter = null;

            var argumentParser = new ArgumentParser();
            argumentParser.AddArg(ParasPara.OnePara, "-c", "--config");
            argumentParser.AddArg(ParasPara.NoPara, "-h", "--help");
            argumentParser.AddArg(ParasPara.NoPara, "-V", "--version");
            argumentParser.AddArg(ParasPara.AllParaAfterIt, "--cmd");
            var ar = argumentParser.ParseArgs(args);
            if (ar.ContainsKey("-h")) {
                Console.WriteLine(cmdHelpText);
                return;
            }
            if (ar.ContainsKey("-V")) {
                Console.WriteLine(NameWithVertionText);
                return;
            }
            if (ar.ContainsKey("--no-log-stdout")) {
                Logging.WriteLogToConsole = false;
            }
            if (ar.ContainsKey("--stdout-no-time") || ar.ContainsKey("--log-stdout-no-time")) {
                Logging.WriteLogToConsoleWithTime = false;
            }
            ar.TryGetValue("--cmd", out var argcmd);
            if (argcmd == null)
                Logging.info(NameWithVertionText);
            if (ar.TryGetValue("--log-file", out var logFile)) {
                Logging.info($"Logging file: {logFile.FirstParaOrThrow}");
                logWriter = new LogFileWriter(logFile.FirstParaOrThrow, Logging.RootLogger);
                logWriter.Start();
                logWriter.WriteHistoryLog();
            }
            if (ar.TryGetValue("-c", out var v)) {
                specifiedConfigPath = v.FirstParaOrThrow;
                Logging.info($"configuation file: {specifiedConfigPath}");
            } else if (ar.ContainsKey("--config-stdin")) {
                Logging.info("reading configuration from stdin until EOF...");
                specifiedConfigContent = Console.In.ReadToEnd();
                Logging.info($"configuration read {specifiedConfigContent.Length} chars");
            }

            if (ar.TryGetValue("--socket-impl", out var socketImpl)) {
                MyStream.SetSocketImpl(socketImpl.FirstParaOrThrow);
            }

            var controller = Controller = new Controller();
            controller.Logger.ParentLogger = Logging.RootLogger;
            long lastPackets = 0, lastBytes = 0;
            void updateTitle()
            {
                lock (CmdConsole.ConsoleOnStdIO.Lock) {
                    var p = MyStream.TotalCopiedPackets;
                    var b = MyStream.TotalCopiedBytes;
                    Console.Title = $"{controller.RunningConnections}/{controller.TotalHandledConnections} current/total connections | relayed {p:N0} Δ{p - lastPackets:N0} packets / {b:N0} Δ{b - lastBytes:N0} bytes - {BuildInfo.AppName}";
                    lastPackets = p;
                    lastBytes = b;
                }
            }
            bool titleUpdateRunning = false;
            Timer titleUpdateTimer = null;
            titleUpdateTimer = new Timer((x) => {
                if (titleUpdateRunning) {
                    updateTitle();
                } else {
                    titleUpdateTimer.Change(-1, -1);
                    Console.Title = BuildInfo.AppName;
                }
            });
            controller.ConfigTomlLoaded += (x) => {
                if (x.TryGetValue<string>("log_file", out var log_file)) {
                    log_file = controller.ProcessFilePath(log_file);
                    if (logWriter?.LogFile != log_file) {
                        logWriter?.Stop();
                        if (log_file != null) {
                            logWriter = new LogFileWriter(log_file, Logging.RootLogger);
                            logWriter.Start();
                        }
                    }
                }
                var toRun = x.TryGetValue("update_title", Environment.OSVersion.Platform == PlatformID.Win32NT);
                if (toRun != titleUpdateRunning) {
                    if (toRun) {
                        titleUpdateRunning = true;
                        titleUpdateTimer.Change(1000, 1000);
                    } else {
                        titleUpdateRunning = false;
                    }
                }
            };

            if (ar.ContainsKey("--force-jit")) {
                ForceJit();
            } else if (ar.ContainsKey("--force-jit-async")) {
                Task.Run(() => ForceJit());
            }


            if (argcmd == null || specifiedConfigPath != null || specifiedConfigContent != null) {
                if (specifiedConfigPath != null) {
                    Commands.loadController(controller, specifiedConfigPath);
                } else if (specifiedConfigContent != null) {
                    controller.FuncGetConfigFile = () => Controller.ConfigFile.FromContent(specifiedConfigContent);
                    controller.Load();
                    controller.Start();
                } else {
                    var paths = GetConfigFilePaths();
                    controller.LoadConfigFileFromMultiPaths(paths);
                    controller.Start();
                }
            }
            var cmdHub = new CommandHub();
            cmdHub.Prompt = $"{BuildInfo.AppName}>";
            Commands.AddCommands(cmdHub, controller, null, new string[] { "all" });
            cmdHub.AddCmdHandler("newbie", (cmd) => Commands.NewbieWizard(cmd, controller, specifiedConfigPath ?? configFilePath));
            cmdHub.AddCmdHandler("ver", (cmd) => cmd.WriteLine(NameWithVertionText));
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                cmdHub.AddCmdHandler("openfolder", (cmd) => {
                    if (cmd.args.Length == 0) {
                        Process.Start("explorer", ".");
                    } else if (cmd.args.Length > 1) {
                        goto WRONT_ARG;
                    } else if (cmd.args[0] == "exe") {
                        OpenFolerAndShowFile(Process.GetCurrentProcess().MainModule.FileName);
                    } else if (cmd.args[0] == "config") {
                        OpenFolerAndShowFile(controller.CurrentConfig.FilePath);
                    } else {
                        goto WRONT_ARG;
                    }
                    return;
                    WRONT_ARG:
                    cmd.statusCode = 1;
                    cmd.WriteLine("wrong arguments");
                    void OpenFolerAndShowFile(string fileName) => Process.Start("explorer", $"/select, \"{fileName}\"");
                }, "Usage: openfolder [exe|config]");
            }
            if (argcmd != null) {
                HandleArgCmd(argcmd, cmdHub);
                return;
            }
#if NS_WINFORM
            cmdHub.AddCmdHandler("gui", (cmd) => {
                WinForm.ControllerForm.RunGuiThread(controller);
            });
            //WinForm.ControllerForm.RunGuiThread(controller);
#endif
            if (!ar.ContainsKey("--no-cli")) {
                var stdio = CmdConsole.StdIO;
                stdio.Write("(Press [Enter] to start interactive interface)\n", Color32.FromConsoleColor(ConsoleColor.Green));
                var sw = Stopwatch.StartNew();
                var line = stdio.ReadLine();
                sw.Stop();
                if (line == null) {
                    Logging.warning("Read an EOF from stdin");
                    if (sw.ElapsedMilliseconds < 50) {
                        Logging.warning("...within 50 ms. Keep running without interactive interface.");
                        Logging.warning("Please use --no-cli option if the program do not run from a console.");
                        goto WAIT;
                    }
                    Logging.warning("...exiting...");
                } else {
                    cmdHub.CmdLoop(CmdConsole.StdIO);
                }
                Environment.Exit(0);
            }
            WAIT:
            while (true)
                Thread.Sleep(int.MaxValue);
        }

        private static void HandleArgCmd(Argument argcmd, CommandHub cmdHub)
        {
            if (argcmd.paras.Count == 0)
                Environment.Exit(-1);
            var cmdrun = Command.FromArray(argcmd.paras.ToArray());
            cmdHub.HandleCommand(CmdConsole.StdIO, cmdrun);
            Environment.Exit(cmdrun.statusCode);
        }

        private static void ForceJit()
        {
            Logging.info("Running ForceJit...");
            Stopwatch sw = Stopwatch.StartNew();
            try {
                var result = Naive.HttpSvr.ForceJit.ForceJitAssembly(typeof(NaiveSocksCli).Assembly, typeof(ForceJit).Assembly);
                Logging.info($"ForceJit spent {sw.ElapsedMilliseconds:N0} ms to complete.");
                Logging.info($"ForceJit result: {result.Types} types, {result.Ctors} ctors and {result.Methods} methods have been JITed. {result.Errors} errors.");
            } catch (Exception e) {
                Logging.exception(e, Logging.Level.Error, "ForceJit error");
            }
        }
    }
}

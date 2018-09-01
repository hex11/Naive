using System;
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

        private const string NAME_NoDebug = "NaiveSocks";
        private const string NAME = NAME_NoDebug +
#if DEBUG
                    " (Debug)";
#else
                    "";
#endif

        private static string NameWithVertionText
            => NAME + " v" + BuildInfo.Version
                    + (BuildInfo.CurrentBuildText.IsNullOrEmpty() ? null : " " + BuildInfo.CurrentBuildText)
                    + (__magic_is_packed ? " (single file)" : "");

        private static string cmdHelpText => $@"{NameWithVertionText}

Usage: {NAME_NoDebug} [-h|--help] [-V|--version] [(-c|--config) FILE]
        [--no-cli] [--no-log-stdout] [--log-file FILE] [--log-stdout-no-time]
        [--force-jit[-async]]";

        private static bool __magic_is_packed;

        private static void Main(string[] args)
        {
            Console.Title = NAME;
            //Logging.HistroyEnabled = false;
            Logging.WriteLogToConsole = true;
            CmdConsole.ConsoleOnStdIO.Lock = Logging.ConsoleLock;

            LogFileWriter logWriter = null;

            var argumentParser = new ArgumentParser();
            argumentParser.AddArg(ParasPara.OnePara, "-c", "--config");
            argumentParser.AddArg(ParasPara.NoPara, "-h", "--help");
            argumentParser.AddArg(ParasPara.NoPara, "-V", "--version");
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
            if (ar.TryGetValue("--log-file", out var logFile)) {
                Logging.info($"Logging file: {logFile.FirstParaOrThrow}");
                logWriter = new LogFileWriter(logFile.FirstParaOrThrow, Logging.RootLogger);
                logWriter.Start();
            }
            if (ar.TryGetValue("-c", out var v)) {
                specifiedConfigPath = v.FirstParaOrThrow;
                Logging.info($"configuation file: {specifiedConfigPath}");
            }

            var controller = new Controller();
            controller.Logger.ParentLogger = Logging.RootLogger;
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
            bool titleUpdateRunning = false;
            Timer titleUpdateTimer = null;
            titleUpdateTimer = new Timer((x) => {
                if (titleUpdateRunning) {
                    updateTitle();
                } else {
                    titleUpdateTimer.Change(-1, -1);
                    Console.Title = NAME;
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
            Logging.info(NameWithVertionText);

            if (ar.ContainsKey("--force-jit")) {
                ForceJit();
            } else if (ar.ContainsKey("--force-jit-async")) {
                Task.Run(() => ForceJit());
            }

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
            cmdHub.AddCmdHandler("ver", (cmd) => cmd.WriteLine(NameWithVertionText));
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                cmdHub.AddCmdHandler("openfolder", (cmd) => Process.Start("explorer", "."));
#if NS_WINFORM
            cmdHub.AddCmdHandler("gui", (cmd) => {
                var form = new GUI.FormConnections();
                Application.Run(form);
            });
#endif
            if (!ar.ContainsKey("--no-cli")) {
                var stdio = CmdConsole.StdIO;
                stdio.Write("(Press [Enter] to start interactive interface)\n", Color32.FromConsoleColor(ConsoleColor.Green));
                //var readTime = DateTime.Now;
                var line = stdio.ReadLine();
                if (line == null) {
                    Logging.warning("read an EOF from stdin");
                    //if (DateTime.Now - readTime < TimeSpan.FromMilliseconds(50)) {
                    //    Logging.warning("...in 50 ms. keep running without interactive interface.");
                    //    goto WAIT;
                    //}
                    Logging.warning("...exiting...");
                    return;
                }
                cmdHub.CmdLoop(CmdConsole.StdIO);
                return;
            }
            WAIT:
            while (true)
                Thread.Sleep(int.MaxValue);
        }

        private static void ForceJit()
        {
            Logging.info("Running ForceJit...");
            Stopwatch sw = Stopwatch.StartNew();
            try {
                var result = Naive.HttpSvr.ForceJit.ForceJitAssembly(typeof(Program).Assembly, typeof(ForceJit).Assembly);
                Logging.info($"ForceJit spent {sw.ElapsedMilliseconds:N0} ms to complete.");
                Logging.info($"ForceJit result: {result.Types} types, {result.Ctors} ctors and {result.Methods} methods have been JITed. {result.Errors} errors.");
            } catch (Exception e) {
                Logging.exception(e, Logging.Level.Error, "ForceJit error");
            }
        }

        private static void initLogFile(string logFile)
        {
            var fs = File.Open(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var sw = new StreamWriter(fs, NaiveUtils.UTF8Encoding);
            var pendingFlush = false;
            var delayFlush = new WaitCallback((x) => {
                lock (sw) {
                    pendingFlush = false;
                    sw.Flush();
                }
            });
            Logging.Logged += (x) => {
                lock (sw) {
                    sw.Write(x.timestamp);
                    sw.WriteLine(x.text);
                    if (!pendingFlush) {
                        pendingFlush = true;
                        ThreadPool.QueueUserWorkItem(delayFlush);
                    }
                }
            };
        }
    }
}

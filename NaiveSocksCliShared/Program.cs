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
        [--force-jit[-async]] [--socket-impl (1|2)]";

        private static bool __magic_is_packed;

        public static Controller Controller { get; private set; }

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

            if (ar.TryGetValue("--socket-impl", out var socketImpl)) {
                if (socketImpl.FirstParaOrThrow == "1") {
                    MyStream.CurrentSocketImpl = MyStream.SocketImpl.SocketStream1;
                } else if (socketImpl.FirstParaOrThrow == "2") {
                    MyStream.CurrentSocketImpl = MyStream.SocketImpl.SocketStream2;
                } else {
                    Logging.error(socketImpl.arg + " with wrong parameter");
                }
                Logging.info("Current SocketStream implementation: " + MyStream.CurrentSocketImpl);
            }

            var controller = Controller = new Controller();
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
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                cmdHub.AddCmdHandler("openfolder", (cmd) => {
                    if (cmd.args.Length == 0) {
                        Process.Start("explorer", ".");
                    } else if (cmd.args.Length > 1) {
                        cmd.statusCode = 1;
                        cmd.WriteLine("wrong arguments");
                    } else if (cmd.args[0] == "exe") {
                        OpenFolerAndShowFile(Process.GetCurrentProcess().MainModule.FileName);
                    } else if (cmd.args[0] == "config") {
                        OpenFolerAndShowFile(controller.CurrentConfig.FilePath);
                    }
                    void OpenFolerAndShowFile(string fileName) => Process.Start("explorer", $"/select, \"{fileName}\"");
                }, "Usage: openfolder [exe|config]");
            }
#if NS_WINFORM
            cmdHub.AddCmdHandler("gui", (cmd) => {
                var form = new GUI.FormConnections();
                Application.Run(form);
            });
#endif
            if (!ar.ContainsKey("--no-cli")) {
                var stdio = CmdConsole.StdIO;
                stdio.Write("(Press [Enter] to start interactive interface)\n", Color32.FromConsoleColor(ConsoleColor.Green));
                var sw = Stopwatch.StartNew();
                var line = stdio.ReadLine();
                sw.Stop();
                if (line == null) {
                    Logging.warning("read an EOF from stdin");
                    if (sw.ElapsedMilliseconds < 50) {
                        Logging.warning("...in 50 ms. keep running without interactive interface.");
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
    }
}

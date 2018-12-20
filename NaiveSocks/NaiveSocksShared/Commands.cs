﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Naive.Console;
using Naive.HttpSvr;
using System.Security.Cryptography;
using System.Threading;
using System.Net;
using System.Text;
using System.Net.Sockets;

namespace NaiveSocks
{
    public class Commands
    {
        static Commands()
        {
            AddAdditionalCommand("dl", cmd => {
                var src = cmd.args[0];
                var dst = cmd.args[1];
                //var conns = cmd.args.Length <= 2 ? 1 : int.Parse(cmd.args[2]);
                var uri = new Uri(src);
                Task.Run(async () => {
                    var httpClient = new System.Net.Http.HttpClient();
                    cmd.WriteLine("HTTP requesting...");
                    var resp = await httpClient.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    var stream = await resp.Content.ReadAsStreamAsync();
                    cmd.WriteLine("Response received.");
                    long length = -1;
                    try {
                        length = Int64.Parse(resp.Content.Headers.GetValues("Content-Length").Single());
                        cmd.WriteLine("Length: " + length.ToString("N0"));
                    } catch (Exception) {
                        cmd.WriteLine("Failed to get length.");
                    }
                    var buf = new BytesSegment(new byte[64 * 1024]);
                    Directory.CreateDirectory(new FileInfo(dst).DirectoryName);
                    long progress = 0;
                    using (var fs = File.Open(dst, FileMode.Create, FileAccess.Write, FileShare.Read)) {
                        cmd.WriteLine("Download started...");
                        while (true) {
                            var r = await stream.ReadAsync(buf);
                            if (r == 0) {
                                cmd.WriteLine("Received EOF: " + progress.ToString("N0"));
                                break;
                            }
                            progress += r;
                            //cmd.WriteLine("Progress: " + progress.ToString("N0"));
                            fs.Write(buf.Bytes, 0, r);
                        }
                    }
                }).Wait();
            });
            if (Environment.OSVersion.Platform == PlatformID.Unix) {
                AddAdditionalCommand("proc", (c) => {
                    c.Write(File.ReadAllText("/proc/self/status"));
                });
            }
        }

        public static void loadController(Controller c, string configFilePath)
        {
            c.LoadConfigFileOrWarning(configFilePath);
            c.Start();
        }

        public static void NewbieWizard(Command cmd, Controller c, string configFilePath)
        {
            cmd.Write($"** Naive Setup Wizard **\n\n", ConsoleColor.White);
            var template = cmd.Select("This is:", new Dictionary<string, Func<string>> {
                ["Client"] = () => {
                    var serverUri = cmd.ReadLine("Server URL (e.g., ws://your.server:8080/): ");
                    var key = cmd.ReadLine("Server key: ");
                    var imux = cmd.ReadLine("Inverse Mux count", "3");
                    var socks5port = cmd.ReadLine("Local socks5 listening port", "1080");
                    var config = new {
                        @in = new {
                            socks5in = new {
                                type = "socks5",
                                local = "127.1:" + socks5port,
                                @out = "nclient"
                            }
                        },
                        @out = new {
                            nclient = new {
                                type = "naive",
                                uri = serverUri,
                                key,
                                imux
                            }
                        }
                    };
                    return Nett.Toml.WriteString(config);
                },
                ["Server"] = () => {
                    var serverPort = cmd.ReadLine("Server listening port", "8080");
                    var serverPath = cmd.ReadLine("Server path (e.g., /updatews)", "/");
                    var key = cmd.ReadLine("Server key: ");
                    var config = new {
                        @in = new {
                            newbie_server = new {
                                type = "naive",
                                local = "0:" + serverPort,
                                path = serverPath,
                                key = key,
                                @out = "direct"
                            }
                        },
                        @out = new {
                            direct = new {
                                type = "direct"
                            }
                        }
                    };
                    return Nett.Toml.WriteString(config);
                },
            }, loopUntilSelect: false);
            var result = template?.Invoke();
            if (result == null)
                return;
            var str = "## Generated by Naive Setup Wizard\n" + result + "\n## End of File\n";
            cmd.WriteLine();
            cmd.Write(str, ConsoleColor.White);
            cmd.WriteLine();
            cmd.WriteLine("Configration generated.");
            if (cmd.YesOrNo($"Save to '{configFilePath}'?", true)) {
                if (File.Exists(configFilePath)
                    && !cmd.YesOrNo($"But '{configFilePath}' already exists, override?", false)) {
                    return;
                }
                File.WriteAllText(configFilePath, str, NaiveUtils.UTF8Encoding);
                cmd.WriteLine("Saved.");
                if (cmd.YesOrNo("Reload now?", true)) {
                    c.Reload();
                }
            }
        }

        public static List<KeyValuePair<string, CommandHandler>> AdditionalCommands = new List<KeyValuePair<string, CommandHandler>>();

        public static void AddAdditionalCommand(string name, CommandHandler handler) => AdditionalCommands.Add(new KeyValuePair<string, CommandHandler>(name, handler));

        public static void AddCommands(CommandHub cmdHub, Controller controller, string prefix, string[] cmds = null)
        {
            cmdHub.AddCmdHandler(prefix + "c", command => {
                var con = command.Console;
                string action = command.ArgOrNull(0);
                if (action == null || action == "list") {
                    var arr = controller.InConnections.ToArray();
                    var sb = new StringBuilder(64);
                    foreach (var item in arr) {
                        if (item.IsHandled) {
                            sb.Clear();
                            var flags = InConnection.ToStringFlags.Default
                                & ~InConnection.ToStringFlags.Bytes
                                & ~InConnection.ToStringFlags.AdditionFields
                                & ~InConnection.ToStringFlags.OutAdapter
                                & ~InConnection.ToStringFlags.OutStream;
                            item.ToString(sb, flags);
                            command.Write(sb.ToString());
                            if (item.ConnectResult?.Adapter != null) {
                                con.Write(" -> '" + item.ConnectResult.Adapter.Name + "'", ConsoleColor.Cyan);
                            }
                            var rw = item.BytesCountersRW;
                            con.Write("\n R=" + rw.R, ConsoleColor.Green);
                            con.Write(" W=" + rw.W + " ", ConsoleColor.Yellow);
                            con.Write(item.GetInfoStr(), ConsoleColor.DarkGray);
                            IMyStream outStream = item.ConnectResult?.Stream;
                            if (outStream != null) {
                                con.Write(" -> " + outStream.ToString() + "\n", ConsoleColor.Cyan);
                            }
                        } else {
                            command.Console.Write(item + "\n", ConsoleColor.Yellow);
                        }
                    }
                    command.WriteLine($"({arr.Length} connections)");
                } else if (action == "stop") {
                    for (int i = 1; i < command.args.Length; i++) {
                        var id = Int32.Parse(command.args[i]);
                        InConnection conneciton = null;
                        lock (controller.InConnectionsLock) {
                            foreach (var item in controller.InConnections) {
                                if (item.Id == id) {
                                    conneciton = item;
                                    break;
                                }
                            }
                        }
                        if (conneciton == null) {
                            con.WriteLine("connection #" + id + " not found");
                            command.statusCode = 1;
                        } else {
                            con.WriteLine("stopping connection #" + id);
                            conneciton.Stop();
                        }
                    }
                } else if (action == "stopall") {
                    InConnection[] arr;
                    lock (controller.InConnectionsLock) {
                        arr = controller.InConnections.ToArray();
                    }
                    con.WriteLine("stopping all connections, IDs: " + string.Join(", ", arr.Select(x => x.Id.ToString())));
                    foreach (var item in arr) {
                        item.Stop();
                    }
                }
            }, "[list|(stop ID1 ID2 IDn...)|stopall]");
            cmdHub.AddCmdHandler(prefix + "wsc", (cmd) => {
                cmd.WriteLine($"# managed websocket connections ({WebSocket.ManagedWebSockets.Count}): ");
                var curTime = WebSocket.CurrentTime;
                foreach (var item in WebSocket.ManagedWebSockets.ToArray()) {
                    cmd.WriteLine($"{item} LatestActive/StartTime={item.LatestActiveTime - curTime}/{item.CreateTime - curTime}");
                }
            });
            cmdHub.AddCmdHandler(prefix + "reload", command => {
                if (command.args.Length == 0) {
                    controller.Reload();
                } else if (command.args.Length == 1) {
                    controller.LoadConfigFileOrWarning(command.args[0], false);
                    controller.Reload();
                } else {
                    command.WriteLine("wrong arguments");
                    command.statusCode = 1;
                }
            }, "Usage: reload [NEW_FILE]");
            cmdHub.AddCmdHandler(prefix + "stat", command => {
                var proc = Process.GetCurrentProcess();
                var sb = new StringBuilder();
                var con = command.Console;

                con.Write("[Memory] ", ConsoleColor.Cyan);
                command.WriteLine(sb.Append("GC: ").Append(GC.GetTotalMemory(false).ToString("N0"))
                    .Append(" / WS: ").Append(proc.WorkingSet64.ToString("N0"))
                    .Append(" / Private: ").Append(proc.PrivateMemorySize64.ToString("N0")).ToString());
                sb.Clear();

                con.Write("[CollectionCount] ", ConsoleColor.Cyan);
                int max = GC.MaxGeneration + 1;
                for (int i = 0; i < max; i++) {
                    if (i != 0)
                        sb.Append(" / ");
                    sb.Append("Gen").Append(i).Append(": ").Append(GC.CollectionCount(i));
                }
                command.WriteLine(sb.ToString());
                sb.Clear();

                con.Write("[CPU Time] ", ConsoleColor.Cyan);
                command.WriteLine(proc.TotalProcessorTime.TotalMilliseconds.ToString("N0") + " ms");
                ThreadPool.GetMinThreads(out var workersMin, out var portsMin);
                ThreadPool.GetMaxThreads(out var workersMax, out var portsMax);
                con.Write("[Threads] ", ConsoleColor.Cyan);
                command.WriteLine($"{proc.Threads.Count} (workers: {workersMin}-{workersMax}, ports: {portsMin}-{portsMax})");
                con.Write("[Connections] ", ConsoleColor.Cyan);
                command.WriteLine($"{controller.RunningConnections} running, {controller.TotalHandledConnections} handled, {controller.TotalFailedConnections} failed");
                con.Write("[MyStream Copied] ", ConsoleColor.Cyan);
                command.WriteLine($"{MyStream.TotalCopiedPackets:N0} packets, {MyStream.TotalCopiedBytes:N0} bytes");
                con.Write("[SocketStream Counters]\n", ConsoleColor.Cyan);
                command.WriteLine($"  {SocketStream.GlobalCounters.StringRead};");
                command.WriteLine($"  {SocketStream.GlobalCounters.StringWrite}.");
            });
            cmdHub.AddCmdHandler(prefix + "config", c => {
                var con = c.Console;
                var cfg = controller.CurrentConfig;
                c.WriteLine("# Current configuration:");
                c.WriteLine();
                c.WriteLine("  File Path: " + cfg.FilePath);
                if (cfg.FilePath == null || Path.GetDirectoryName(cfg.FilePath) != cfg.WorkingDirectory) {
                    c.WriteLine("  Working Directory: " + cfg.WorkingDirectory);
                }
                c.WriteLine("  Logging Level: " + cfg.LoggingLevel);
                c.WriteLine();
                var inadas = cfg.InAdapters.ToArray();
                Array.Sort(inadas, (a, b) => {
                    return -a.BytesCountersRW.TotalValue.Bytes.CompareTo(b.BytesCountersRW.TotalValue.Bytes);
                });
                c.WriteLine($"  ## InAdapters ({inadas.Length}):");
                foreach (var item in inadas) {
                    var str = $"    - '{item.Name}': {item.ToString(false)} -> {item.@out?.ToString() ?? "(No OutAdapter)"}";
                    var rw = item.BytesCountersRW;
                    if (rw.TotalValue.Packets == 0) {
                        con.WriteLine(str);
                    } else {
                        con.Write(str);
                        con.Write(" R=" + rw.R, ConsoleColor.Green);
                        con.Write(" W=" + rw.W + "\n", ConsoleColor.Yellow);
                    }
                }
                c.WriteLine();
                var outadas = cfg.OutAdapters.ToArray();
                Array.Sort(outadas, (a, b) => {
                    return -a.BytesCountersRW.TotalValue.Bytes.CompareTo(b.BytesCountersRW.TotalValue.Bytes);
                });
                c.WriteLine($"  ## OutAdapters ({outadas.Length}):");
                foreach (var item in outadas) {
                    var str = $"    - '{item.Name}': {item.ToString(false)}";
                    var rw = item.BytesCountersRW;
                    if (rw.TotalValue.Packets == 0) {
                        con.WriteLine(str);
                    } else {
                        con.Write(str);
                        con.Write(" R=" + rw.R, ConsoleColor.Green);
                        con.Write(" W=" + rw.W + "\n", ConsoleColor.Yellow);
                    }
                }
            });
            cmdHub.AddCmdHandler(prefix + "logs", command => {
                var cmd = command.ArgOrNull(0);
                if (cmd == "dump") {
                    var logs = Logging.getLogsHistoryArray();
                    var path = command.ArgOrNull(1);
                    if (path == null) {
                        command.WriteLine("missing path.");
                        command.statusCode = 1;
                        return;
                    }
                    using (var sw = new StreamWriter(
                        File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite),
                        NaiveUtils.UTF8Encoding, 8192)) {
                        foreach (var item in logs) {
                            sw.Write(item.timestamp);
                            sw.Write(item.text);
                            sw.WriteLine();
                        }
                    }
                } else if (cmd == "show") {
                    Logging.Log[] logs;
                    if (command.ArgOrNull(1) == null) {
                        logs = Logging.getLogsHistoryArray();
                    } else {
                        var count = int.Parse(command.ArgOrNull(1));
                        logs = new Logging.Log[count];
                        Logging.getLogsHistory(new ArraySegment<Logging.Log>(logs));
                    }
                    PrintLogs(command.Console, logs);
                } else if (cmd == "test") {
                    for (int i = 0; i < 10000; i++) {
                        Logging.log("(Log test " + (i + 1) + " of 10000)", Logging.Level.Debug);
                    }
                } else {
                    command.WriteLine("wrong arguments.");
                }
            }, "Usage: logs (dump PATH)|show");
            cmdHub.AddCmdHandler(prefix + "gc", command => {
                NaiveUtils.GCCollect(command.WriteLine);
            });
            cmdHub.AddCmdHandler(prefix + "test", cmd => {
                cmd.WriteLine($"Stopwatch.Frequency: {Stopwatch.Frequency}");
                cmd.WriteLine($"Stopwatch.IsHighResolution: {Stopwatch.IsHighResolution}");
                cmd.WriteLine($"Environment.ProcessorCount: {pcount}");
                var selections = tests.Select(x => x.Name).ToList();
                var all = "ALL tests with '*'";
                selections.Insert(0, all);
                while (true) {
                    var index = cmd.Select("Selete a performance test:", selections, "(input other text to exit): ", false);
                    if (index < 0) {
                        return;
                    } else if (index == 0) {
                        foreach (var item in tests) {
                            if (item.Name.StartsWith("*"))
                                item.Run(cmd.Console);
                        }
                    } else {
                        tests[index - 1].Run(cmd.Console);
                    }
                    cmd.WriteLine("");
                }
            });
            cmdHub.AddCmdHandler(prefix + "mtest", cmd => {
                var adapters = controller.OutAdapters.Select(x => x as NaiveMOutAdapter).Where(x => x != null).ToList();
                if (adapters.Count == 0) {
                    cmd.WriteLine("NaiveM OutAdapter not found.");
                    return;
                }
                NaiveMOutAdapter selected;
                if (adapters.Count == 1) {
                    selected = adapters.Single();
                } else {
                    var selectedId = cmd.Select("NaiveM OutAdapters:", adapters.Select(x => x.ToString(true)).ToList(),
                            "Select to start speed test: ", false);
                    if (selectedId < 0)
                        return;
                    selected = adapters[selectedId];
                }
                selected.SpeedTest((x) => cmd.WriteLine(x)).Wait();
            });
            cmdHub.AddCmdHandler(prefix + "mdebug", (cmd) => {
                Channel.Debug ^= true;
                cmd.WriteLine($"Multiplexing debugging is now set to {Channel.Debug}");
            });
            cmdHub.AddCmdHandler(prefix + "mping", (cmd) => {
                var keep = true;
                var pingEnabled = false;
                switch (cmd.ArgOrNull(0)) {
                    case "start":
                        pingEnabled = true;
                        break;
                    case "stop":
                        pingEnabled = false;
                        break;
                    case null:
                        keep = false;
                        break;
                    default:
                        cmd.WriteLine($"wrong argument '{cmd.ArgOrNull(0)}'");
                        cmd.statusCode = 1;
                        return;
                }
                if (keep) {
                    cmd.WriteLine(pingEnabled ? "start ping" : "stop ping");
                }
                List<Task> tasks = new List<Task>();
                var outs = from x in controller.OutAdapters where x is NaiveMOutAdapter select (x as NaiveMOutAdapter);
                if (keep) {
                    foreach (var item in outs) {
                        item.ping_enabled = pingEnabled;
                    }
                    return;
                }
                var ins = from x in controller.InAdapters where x is NaiveMServerBase select x.As<NaiveMServerBase>().nmsList;
                foreach (IEnumerable<NaiveMChannels> item in (from x in outs select from y in x.ncsPool select y.nms).Union(ins)) {
                    foreach (var poolItem in item) {
                        var task = NaiveUtils.RunAsyncTask(async () => {
                            try {
                                await (poolItem?.Ping((t) => cmd.WriteLine($"{poolItem.BaseChannels}: {t}"), true)).NoNRE();
                            } catch (Exception e) {
                                cmd.WriteLine(Logging.getExceptionText(e, $"{poolItem?.BaseChannels} pinging"));
                            }
                        });
                        tasks.Add(task);
                    }
                }
                if (Task.WhenAll(tasks.ToArray()).WithTimeout(3 * 1000).Wait()) {
                    cmd.WriteLine($"waiting timed out.");
                }
            }, "Usage: mping [start|stop]");
            cmdHub.AddCmdHandler(prefix + "dns", cmd => {
                var adapters = controller.OutAdapters.Select(x => x as IDnsProvider).Where(x => x != null).ToList();
                if (adapters.Count == 0) {
                    cmd.WriteLine("NaiveM OutAdapter not found.");
                    return;
                }
                IDnsProvider selected;
                if (adapters.Count == 1) {
                    selected = adapters.Single();
                } else {
                    var selectedId = cmd.Select("IDnsProviders:", adapters.Select(x => x.GetAdapter().ToString(true)).ToList(),
                            "Select one: ", false);
                    if (selectedId < 0)
                        return;
                    selected = adapters[selectedId];
                }
                Task.Run(async () => {
                    foreach (var item in await selected.ResolveName(cmd.args[0])) {
                        cmd.WriteLine(item.ToString());
                    }
                }).Wait(3000);
            }, "Usage: dns DOMAIN");
            cmdHub.AddCmdHandler(prefix + "settp", cmd => {
                ThreadPool.SetMinThreads(cmd.ArgOrNull(0).ToInt(), cmd.ArgOrNull(1).ToInt());
                ThreadPool.SetMaxThreads(cmd.ArgOrNull(2).ToInt(), cmd.ArgOrNull(3).ToInt());
            }, "workerMin portMin workerMax portMax");
            AddSocketTests(cmdHub, prefix);
            if (Environment.OSVersion.Platform == PlatformID.Unix) {
                cmdHub.AddCmdHandler(prefix + "ya-ls", cmd => {
                    void lsEpoller(Command c, Epoller e)
                    {
                        var running = e.RunningHandler;
                        if (running != null)
                            cmd.WriteLine("running -> " + running);
                        foreach (var item in e.GetMap()) {
                            cmd.WriteLine(item.Key + " -> " + item.Value);
                        }
                    }
                    cmd.WriteLine("GlobalEpoller:");
                    lsEpoller(cmd, YASocket.GlobalEpoller);
                    cmd.WriteLine("GlobalEpollerW:");
                    lsEpoller(cmd, YASocket.GlobalEpollerW);
                });
            }
            if (cmds != null) {
                if (cmds.Length == 1 && cmds[0] == "all") {
                    foreach (var r in AdditionalCommands) {
                        cmdHub.AddCmdHandler(prefix + r.Key, r.Value);
                    }
                } else {
                    foreach (var cmd in cmds) {
                        var r = AdditionalCommands.Find(x => x.Key == cmd);
                        if (r.Value != null) {
                            cmdHub.AddCmdHandler(prefix + r.Key, r.Value);
                        }
                    }
                }
            }
        }

        private static void AddSocketTests(CommandHub cmdHub, string prefix)
        {
            prefix += "spt";
            cmdHub.AddCmdHandler(prefix + "-sync", command => {
                const int BufSize = 64 * 1024;
                var ip = $"127.{NaiveUtils.Random.Next(1 << 16)}";
                var listener = new System.Net.Sockets.TcpListener(NaiveUtils.ParseIPEndPoint(ip + ":52345"));
                listener.Start();
                command.WriteLine("Running socket test...");
                Task.Run(() => {
                    try {
                        using (var cli = listener.AcceptTcpClient()) {
                            var s = cli.Client;
                            var buf = new byte[BufSize];
                            var sw = Stopwatch.StartNew();
                            while (true) {
                                s.Send(buf, 0, BufSize, System.Net.Sockets.SocketFlags.None);
                                if (sw.ElapsedMilliseconds > 5 * 1000) {
                                    break;
                                }
                            }
                        }
                    } finally {
                        listener.Stop();
                    }
                }).Forget();

                {
                    long total = 0;
                    var sw = Stopwatch.StartNew();
                    using (var cli = new System.Net.Sockets.TcpClient()) {
                        cli.Connect(NaiveUtils.ParseIPEndPoint(ip + ":52345"));
                        var s = cli.Client;
                        var buf = new byte[BufSize];
                        int read;
                        while ((read = s.Receive(buf)) > 0) {
                            total += read;
                        }
                    }
                    var ms = sw.ElapsedMilliseconds;
                    command.WriteLine($"Transferred {total:N0} bytes in {ms:N0} ms.");
                    command.WriteLine("Speed: " + (total / 1024 / 1024 * 1000 / ms) + " MiB/s");
                }
            });
            cmdHub.AddCmdHandler(prefix + "-1", command => {
                RunSocketTest(command, x => new SocketStream1(x), false);
            });
            cmdHub.AddCmdHandler(prefix + "-1r", command => {
                RunSocketTest(command, x => new SocketStream1(x), true);
            });
            cmdHub.AddCmdHandler(prefix + "-2", command => {
                RunSocketTest(command, x => new SocketStream2(x), false);
            });
            cmdHub.AddCmdHandler(prefix + "-ya", command => {
                RunSocketTest(command, x => new YASocket(x), false);
            });
            cmdHub.AddCmdHandler(prefix + "-yar", command => {
                RunSocketTest(command, x => new YASocket(x), true);
            });
        }

        private static void RunSocketTest(Command command, Func<Socket, IMyStream> createStream, bool useR)
        {
            const int BufSize = 64 * 1024;
            var ip = $"127.{NaiveUtils.Random.Next(1 << 16)}";
            var listener = new System.Net.Sockets.TcpListener(NaiveUtils.ParseIPEndPoint(ip + ":52345"));
            listener.Start();
            command.WriteLine("Running socket test...");
            Task.Run(async () => {
                try {
                    using (var cli = await listener.AcceptTcpClientAsync()) {
                        var s = createStream(cli.Client);
                        var buf = new byte[BufSize];
                        var sw = Stopwatch.StartNew();
                        while (true) {
                            if (useR)
                                await s.WriteAsyncR(new BytesSegment(buf, 0, BufSize));
                            else
                                await s.WriteAsync(new BytesSegment(buf, 0, BufSize));
                            if (sw.ElapsedMilliseconds > 5 * 1000) {
                                break;
                            }
                        }
                    }
                } catch (Exception ex) {
                    command.WriteLine("Writer error: " + ex);
                } finally {
                    listener.Stop();
                }
            }).Forget();

            Task.Run(async () => {
                long total = 0;
                var sw = Stopwatch.StartNew();
                using (var cli = new System.Net.Sockets.TcpClient()) {
                    await cli.ConnectAsync(ip, 52345);
                    var s = createStream(cli.Client);
                    var buf = new byte[BufSize];
                    int read;
                    try {
                        while (true) {
                            if (useR)
                                read = await s.ReadAsyncR(new BytesSegment(buf, 0, BufSize));
                            else
                                read = await s.ReadAsync(new BytesSegment(buf, 0, BufSize));
                            if (read > 0)
                                total += read;
                            else
                                break;
                        }
                    } catch (Exception ex) {
                        command.WriteLine("Reader error: " + ex);
                    }
                }
                var ms = sw.ElapsedMilliseconds;
                command.WriteLine($"Transferred {total:N0} bytes in {ms:N0} ms.");
                command.WriteLine("Speed: " + (total / 1024 / 1024 * 1000 / ms) + " MiB/s");
            }).Wait();
        }

        static byte[] sampleKey(int bytesCount) => NaiveProtocol.GetRealKeyFromString("testtttt", bytesCount);
        static byte[] sampleIV(int bytesCount, byte b = 0x80) => Enumerable.Range(0, bytesCount).Select(x => b).ToArray();
        static int pcount = Environment.ProcessorCount;

        struct TestCtx
        {
            public Stopwatch sw;
        }

        class TestItem
        {
            public string Name;
            public Action<TestCtx> Action;

            public TestItem(string name, Action<TestCtx> action)
            {
                Name = name;
                Action = action;
            }

            public void Run(CmdConsole con)
            {
                con.Write(Name, Color32.FromConsoleColor(ConsoleColor.Cyan));
                con.Write("...GC...");
                var sw = Stopwatch.StartNew();
                GC.Collect();
                try {
                    GC.WaitForFullGCComplete();
                } catch (Exception) {
                    con.Write("WaitGCFailed...");
                    Thread.Sleep(200);
                }
                sw.Stop();
                con.WriteLine(sw.ElapsedTicks + " ticks.");
                con.Write("Running...");
                sw.Restart();
                Action(new TestCtx { sw = sw });
                sw.Stop();
                con.Write("\tresult: ");
                con.Write($"[{sw.ElapsedMilliseconds:N0} ms]", Color32.FromConsoleColor(ConsoleColor.Green));
                con.WriteLine($", or {sw.ElapsedTicks:N0} ticks");
            }
        }

        static TestItem[] tests = new TestItem[] {
            new TestItem("alloc 32 KiB bytes 1024 times", (ctx) => {
                for (int i = 0; i < 1024; i++) {
                    var arr = new byte[32 * 1024];
                }
            }),
            new TestItem("alloc & copy 32 KiB bytes 1024 times", (ctx) => {
                var arr = new byte[32 * 1024];
                for (int i = 0; i < 1024; i++) {
                    NaiveUtils.CopyBytes(new byte[32 * 1024], 0, arr, 0, 32 * 1024);
                }
            }),
            new TestItem("* encrypt 3 bytes 1024 * 1024 times (ws filter - aes-128-ofb)", (ctx) => {
                var filter = WebSocket.GetAesStreamFilter(true, sampleKey(16));
                for (int i = 0; i < 1024 * 1024; i++) {
                    var buf = new byte[3];
                    var bv = new BytesView(buf);
                    filter(bv);
                }
            }),
            new TestItem("* encrypt 1024 bytes 16 * 1024 times (ws filter - aes-128-ofb)", (ctx) => {
                var filter = WebSocket.GetAesStreamFilter(true, sampleKey(16));
                for (int i = 0; i < 32 * 1024; i++) {
                    var buf = new byte[1024];
                    var bv = new BytesView(buf);
                    filter(bv);
                }
            }),
            new TestItem("* encrypt 32 KiB 512 times (ws filter - aes-128-ofb)", (ctx) => {
                var filter = WebSocket.GetAesStreamFilter(true, sampleKey(16));
                for (int i = 0; i < 512; i++) {
                    var buf = new byte[32 * 1024];
                    var bv = new BytesView(buf);
                    filter(bv);
                }
            }),
            new TestItem("* encrypt 128 KiB 128 times (aes-128-ctr)", (ctx) => {
                var provider = new AesCryptoServiceProvider();
                provider.Mode = CipherMode.ECB;
                provider.Padding = PaddingMode.None;
                provider.KeySize = 128;
                provider.Key = sampleKey(16);
                var ch = new CtrEncryptor(provider.CreateEncryptor());
                ch.IV = sampleIV(16);
                var buf = new byte[128 * 1024];
                var bv = new BytesSegment(buf);
                for (int i = 0; i < 128; i++) {
                    ch.Update(bv);
                }
            }),
            new TestItem("* encrypt 128 KiB 128 times (aes-128-cfb)", (ctx) => {
                var provider = new AesCryptoServiceProvider();
                provider.Mode = CipherMode.ECB;
                provider.Padding = PaddingMode.None;
                provider.KeySize = 128;
                provider.Key = sampleKey(16);
                var ch = new CfbEncryptor(provider.CreateEncryptor(), true);
                ch.IV = sampleIV(16);
                var buf = new byte[128 * 1024];
                var bv = new BytesSegment(buf);
                for (int i = 0; i < 128; i++) {
                    ch.Update(bv);
                }
            }),
            new TestItem("* encrypt 128 KiB 128 times (chacha20-ietf)", (ctx) => {
                var ch = new ChaCha20IetfEncryptor(sampleKey(32));
                ch.IV = sampleIV(12);
                var buf = new byte[128 * 1024];
                var bv = new BytesSegment(buf);
                for (int i = 0; i < 128; i++) {
                    ch.Update(bv);
                }
            }),
            new TestItem("* encrypt 128 KiB 128 times ('speck0' (speck-128/128-ctr) without multi-threading)", (ctx) => {
                Speck0Test(sampleKey(16), 128 * 1024, 128, false);
            }),
            new TestItem("encrypt 128 KiB 128 times ('speck0' (speck-128/128-ctr))", (ctx) => {
                Speck0Test(sampleKey(16), 128 * 1024, 128, true);
            }),
            new TestItem("* encrypt 16 KiB 1024 times ('speck0' (speck-128/128-ctr) without multi-threading)", (ctx) => {
                Speck0Test(sampleKey(16), 16 * 1024, 1024, false);
            }),
            new TestItem("encrypt 16 KiB 1024 times ('speck0' (speck-128/128-ctr))", (ctx) => {
                Speck0Test(sampleKey(16), 16 * 1024, 1024, true);
            }),
            new TestItem("encrypt 3 B 1024 * 1024 times ('speck0' (speck-128/128-ctr))", (ctx) => {
                Speck0Test(sampleKey(16), 3, 1024 * 1024, true);
            }),
            new TestItem("* encrypt 128 KiB 128 times ('speck064' (speck-64/128-ctr))", (ctx) => {
                var ch = new Speck.Ctr64128(sampleKey(16));
                ch.IV = sampleIV(8);
                var buf = new byte[128 * 1024];
                var bv = new BytesSegment(buf);
                for (int i = 0; i < 128; i++) {
                    ch.Update(bv);
                }
            }),
            new TestItem("* localhost socket 1", (ctx) => {
                var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(20000, 60000));
                var listener = new Listener(null, ep) { LogInfo = false };
                listener.Accepted += (x) => NaiveUtils.RunAsyncTask(async () => {
                    var stream = new SocketStream1(x.Client);
                    var buf = new byte[32 * 1024];
                    while (await stream.ReadAsync(buf) > 0) {
                    }
                    x.Close();
                });
                listener.Run().Forget();
                TestSocketWrite(ctx.sw, ep, 32 * 1024, 1024);
                listener.Stop();
            }),
            new TestItem("localhost socket 1 (4 bytes read buffer)", (ctx) => {
                var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(20000, 60000));
                var listener = new Listener(null, ep) { LogInfo = false };
                listener.Accepted += (x) => NaiveUtils.RunAsyncTask(async () => {
                    var stream = new SocketStream1(x.Client);
                    var buf = new byte[4];
                    while (await stream.ReadAsync(buf) > 0) {
                    }
                    x.Close();
                });
                listener.Run().Forget();
                TestSocketWrite(ctx.sw, ep, 32 * 1024, 1024);
                listener.Stop();
            }),
            new TestItem("localhost socket 1 (4 bytes read buffer without smart buffer)", (ctx) => {
                var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(20000, 60000));
                var listener = new Listener(null, ep) { LogInfo = false };
                listener.Accepted += (x) => NaiveUtils.RunAsyncTask(async () => {
                    var stream = new SocketStream1(x.Client);
                    stream.EnableReadaheadBuffer = false;
                    var buf = new byte[4];
                    while (await stream.ReadAsync(buf) > 0) {
                    }
                    x.Close();
                });
                listener.Run().Forget();
                TestSocketWrite(ctx.sw, ep, 32 * 1024, 1024);
                listener.Stop();
            }),
            new TestItem("localhost socket 1 (4 bytes read buffer without smart buffer/sync)", (ctx) => {
                var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(20000, 60000));
                var listener = new Listener(null, ep) { LogInfo = false };
                listener.Accepted += (x) => NaiveUtils.RunAsyncTask(async () => {
                    var stream = new SocketStream1(x.Client);
                    stream.EnableReadaheadBuffer = false;
                    stream.EnableSmartSyncRead = false;
                    var buf = new byte[4];
                    while (await stream.ReadAsync(buf) > 0) {
                    }
                    x.Close();
                });
                listener.Run().Forget();
                TestSocketWrite(ctx.sw, ep, 32 * 1024, 1024);
                listener.Stop();
            }),
            new TestItem("localhost socket 2", (ctx) => {
                var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(20000, 60000));
                var listener = new Listener(null, ep) { LogInfo = false };
                listener.Accepted += (x) => NaiveUtils.RunAsyncTask(async () => {
                    var stream = new SocketStream2(x.Client);
                    var buf = new byte[32 * 1024];
                    while (await stream.ReadAsync(buf) > 0) {
                    }
                    x.Close();
                });
                listener.Run().Forget();
                NaiveUtils.RunAsyncTask(async () => {
                    var socket = await NaiveUtils.ConnectTcpAsync(AddrPort.Parse(ep.ToString()), 5000);
                    var stream = new SocketStream2(socket);
                    ctx.sw.Restart();
                    var buf = new byte[32 * 1024];
                    for (int i = 0; i < 1024; i++) {
                        await stream.WriteAsync(buf);
                    }
                    socket.Close();
                }).RunSync();
                listener.Stop();
            }),
            new TestItem("localhost socket 1 sync", (ctx) => {
                var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(50000, 60000));
                var listener = new Listener(null, ep) { LogInfo = false };
                listener.Accepted += (x) => {
                    var stream = new SocketStream1(x.Client);
                    var buf = new byte[32 * 1024];
                    while (stream.Read(buf) > 0) {
                    }
                    x.Close();
                };
                listener.Run().Forget();
                {
                    var socket = NaiveUtils.ConnectTcpAsync(AddrPort.Parse(ep.ToString()), 5000).RunSync();
                    var stream = new SocketStream1(socket);
                    ctx.sw.Restart();
                    var buf = new byte[32 * 1024];
                    for (int i = 0; i < 1024; i++) {
                        stream.Write(buf);
                    }
                    socket.Close();
                }
                listener.Stop();
            }),
            new TestItem("get DateTime.Now 1024 * 1024 times", (ctx) => {
                for (int i = 0; i < 1024 * 1024; i++) {
                    var now = DateTime.Now;
                }
            }),
        };

        private static void PrintLogs(CmdConsole con, Logging.Log[] logs)
        {
            foreach (var item in logs) {
                PrintLog(con, item);
            }
            con.ForegroundColor = ConsoleColor.Blue;
            con.WriteLine($"({logs.Length} logs)");
            con.ResetColor();
        }

        private static void PrintLog(CmdConsole con, Logging.Log item)
        {
            ConsoleColor color;
            switch (item.level) {
                default:
                case Logging.Level.None:
                    color = ConsoleColor.Gray;
                    break;
                case Logging.Level.Info:
                    color = ConsoleColor.White;
                    break;
                case Logging.Level.Warning:
                    color = ConsoleColor.Yellow;
                    break;
                case Logging.Level.Error:
                    color = ConsoleColor.Red;
                    break;
            }
            con.ForegroundColor = color;
            con.Write(item.timestamp);
            con.CustomColorEnabled = false;
            con.WriteLine(item.text);
        }

        private static void TestSocketWrite(Stopwatch sw, IPEndPoint ep, int bufSize, int count)
        {
            NaiveUtils.RunAsyncTask(async () => {
                var socket = await NaiveUtils.ConnectTcpAsync(AddrPort.Parse(ep.ToString()), 5000);
                var stream = new SocketStream1(socket);
                sw.Restart();
                var buf = new byte[bufSize];
                for (int i = 0; i < count; i++) {
                    await stream.WriteAsync(buf);
                }
                socket.Close();
            }).RunSync();
        }

        private static void Speck0Test(byte[] samplekey, int bufSize, int loops, bool allowMultiThreading)
        {
            var ch = new Speck.Ctr128128(samplekey);
            ch.EnableMultiThreading = allowMultiThreading;
            ch.IV = new byte[] { 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80 };
            var buf = new byte[bufSize];
            var bv = new BytesSegment(buf);
            for (int i = 0; i < loops; i++) {
                ch.Update(bv);
            }
        }
    }
}

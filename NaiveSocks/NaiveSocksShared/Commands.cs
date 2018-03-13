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

//using FSF.HttpSvr;

namespace NaiveSocks
{
    public class Commands
    {
        public static void loadController(Controller c, string configFilePath)
        {
            c.LoadConfigFileOrWarning(configFilePath);
            c.Start();
        }

        public static void NewbieWizard(Command cmd, Controller c, string configFilePath)
        {
            cmd.WriteLine($"** Naive Setup Wizard **\n");
            var template = cmd.Select("This is:", new Dictionary<string, Func<string>> {
                ["Client"] = () => {
                    var server = cmd.ReadLine("Server addr:port (e.g., baidu.com:80): ");
                    var serverPath = cmd.ReadLine("Server path", "/");
                    var key = cmd.ReadLine("Server key: ");
                    var socks5port = cmd.ReadLine("Local socks5 listening port", "1080");
                    var config = new {
                        @in = new {
                            socks5in = new {
                                type = "socks5",
                                local = "127.1:" + socks5port,
                                @out = "naiveclient"
                            }
                        },
                        @out = new {
                            naiveclient = new {
                                type = "naive",
                                server = server,
                                path = serverPath,
                                key = key
                            }
                        }
                    };
                    return Nett.Toml.WriteString(config);
                },
                ["Server"] = () => {
                    var serverPort = cmd.ReadLine("Server listening port", "8080");
                    var serverPath = cmd.ReadLine("Server path (e.g. /updatews)", "/");
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
            });
            if (template == null)
                return;
            var str = "## Generated by Naive Setup Wizard\n\n" + template() + "\n## End of File\n";
            cmd.WriteLine(str);
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

        public static void AddCommands(CommandHub cmdHub, Controller controller, string prefix)
        {
            cmdHub.AddCmdHandler(prefix + "c", command => {
                var arr = controller.InConnections.ToArray();
                foreach (var item in arr) {
                    command.WriteLine(item.ToString());
                }
                command.WriteLine($"# {arr.Length} connections");
            });
            cmdHub.AddCmdHandler(prefix + "wsc", (cmd) => {
                cmd.WriteLine($"# managed websocket connections ({WebSocket.ManagedWebSockets.Count}): ");
                foreach (var item in WebSocket.ManagedWebSockets.ToArray()) {
                    cmd.WriteLine($"{item} LatestActive/StartTime={item.LatestActiveTime - WebSocket.CurrentTime}/{item.CreateTime - WebSocket.CurrentTime}");
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
                }
            }, "Usage: reload [NEW_FILE]");
            cmdHub.AddCmdHandler(prefix + "stat", command => {
                var proc = Process.GetCurrentProcess();
                command.WriteLine($"TotalMemory: {GC.GetTotalMemory(command.args.Contains("gc")).ToString("N0")}");
                command.WriteLine("CollectionCount: " + string.Join(
                    ", ",
                    Enumerable.Range(0, GC.MaxGeneration + 1)
                        .Select(x => $"({x}) {GC.CollectionCount(x)}")));
                command.WriteLine($"WorkingSet: {proc.WorkingSet64.ToString("N0")}");
                command.WriteLine($"PrivateMemory: {proc.PrivateMemorySize64.ToString("N0")}");
                command.WriteLine($"CPUTime: {proc.TotalProcessorTime.TotalMilliseconds.ToString("N0")} ms");
                command.WriteLine($"Threads: {proc.Threads.Count}");
                command.WriteLine($"Connections: {controller.RunningConnections:N0} running, {controller.TotalHandledConnections} handled");
                command.WriteLine($"MyStream Copied: {MyStream.TotalCopiedPackets:N0} packets, {MyStream.TotalCopiedBytes:N0} bytes");
                command.WriteLine($"SocketStream1: {SocketStream1.GlobalCounters.StringRead};");
                command.WriteLine($"               {SocketStream1.GlobalCounters.StringWrite}.");
            });
            cmdHub.AddCmdHandler(prefix + "config", c => {
                var cfg = controller.CurrentConfig;
                c.WriteLine("# Current configuration:");
                c.WriteLine();
                c.WriteLine("  File Path: " + cfg.FilePath);
                if (cfg.FilePath == null || Path.GetDirectoryName(cfg.FilePath) != cfg.WorkingDirectory) {
                    c.WriteLine("  Working Directory: " + cfg.WorkingDirectory);
                }
                c.WriteLine("  Logging Level: " + cfg.LoggingLevel);
                c.WriteLine();
                c.WriteLine($"  ## InAdapters ({cfg.InAdapters.Count}):");
                foreach (var item in cfg.InAdapters) {
                    c.WriteLine($"    - '{item.Name}': {item} -> {item.@out?.ToString() ?? "(No OutAdapter)"}");
                }
                c.WriteLine();
                c.WriteLine($"  ## OutAdapters ({cfg.OutAdapters.Count}):");
                foreach (var item in cfg.OutAdapters) {
                    c.WriteLine($"    - '{item.Name}': {item}");
                }
            });
            cmdHub.AddCmdHandler(prefix + "logs", command => {
                var logs = Logging.getLogsHistoryArray();
                var cmd = command.ArgOrNull(0);
                if (cmd == "dump") {
                    var path = command.ArgOrNull(1);
                    if (path == null) {
                        command.WriteLine("missing path.");
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
                    foreach (var item in logs) {
                        command.WriteLine(item.timestamp + item.text);
                    }
                    command.WriteLine($"(total {logs.Length} logs)");
                } else {
                    command.WriteLine("wrong arguments.");
                }
            }, "Usage: logs (dump PATH)|show");
            cmdHub.AddCmdHandler(prefix + "gc", command => {
                NaiveUtils.GCCollect(command.WriteLine);
            });
            cmdHub.AddCmdHandler(prefix + "test", cmd => {
                byte[] samplekey = NaiveProtocol.GetRealKeyFromString("testtttt");
                var pcount = Environment.ProcessorCount;
                var sw = new Stopwatch();
                var tests = new Dictionary<string, Action> {
                    ["alloc 32 KiB bytes 1024 times"] = () => {
                        for (int i = 0; i < 1024; i++) {
                            var arr = new byte[32 * 1024];
                        }
                    },
                    ["alloc & copy 32 KiB bytes 1024 times"] = () => {
                        var arr = new byte[32 * 1024];
                        for (int i = 0; i < 1024; i++) {
                            NaiveUtils.CopyBytes(new byte[32 * 1024], 0, arr, 0, 32 * 1024);
                        }
                    },
                    ["encrypt 3 bytes 32 * 1024 times (ws filter - aes-128-ofb)"] = () => {
                        var filter = WebSocket.GetAesStreamFilter(true, samplekey);
                        for (int i = 0; i < 32 * 1024; i++) {
                            var buf = new byte[3];
                            var bv = new BytesView(buf);
                            filter(bv);
                        }
                    },
                    ["encrypt 1024 bytes 16 * 1024 times (ws filter - aes-128-ofb)"] = () => {
                        var filter = WebSocket.GetAesStreamFilter(true, samplekey);
                        for (int i = 0; i < 32 * 1024; i++) {
                            var buf = new byte[1024];
                            var bv = new BytesView(buf);
                            filter(bv);
                        }
                    },
                    ["encrypt 32 KiB 512 times (ws filter - aes-128-ofb)"] = () => {
                        var filter = WebSocket.GetAesStreamFilter(true, samplekey);
                        for (int i = 0; i < 512; i++) {
                            var buf = new byte[32 * 1024];
                            var bv = new BytesView(buf);
                            filter(bv);
                        }
                    },
                    ["encrypt 128 KiB 128 times (aes-128-ctr)"] = () => {
                        var provider = new AesCryptoServiceProvider();
                        provider.Mode = CipherMode.ECB;
                        provider.Padding = PaddingMode.None;
                        provider.KeySize = 128;
                        provider.Key = samplekey;
                        var ch = new CtrEncryptor(provider.CreateEncryptor());
                        ch.IV = new byte[] { 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80 };
                        var buf = new byte[128 * 1024];
                        var bv = new BytesSegment(buf);
                        for (int i = 0; i < 128; i++) {
                            ch.Update(bv);
                        }
                    },
                    ["encrypt 128 KiB 128 times (aes-128-cfb)"] = () => {
                        var provider = new AesCryptoServiceProvider();
                        provider.Mode = CipherMode.ECB;
                        provider.Padding = PaddingMode.None;
                        provider.KeySize = 128;
                        provider.Key = samplekey;
                        var ch = new CfbEncryptor(provider.CreateEncryptor(), true);
                        ch.IV = new byte[] { 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80 };
                        var buf = new byte[128 * 1024];
                        var bv = new BytesSegment(buf);
                        for (int i = 0; i < 128; i++) {
                            ch.Update(bv);
                        }
                    },
                    ["encrypt 128 KiB 128 times (chacha20-ietf)"] = () => {
                        var ch = new ChaCha20IetfEncryptor(NaiveUtils.ConcatBytes(samplekey, samplekey));
                        ch.IV = new byte[] { 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80, 80 };
                        var buf = new byte[128 * 1024];
                        var bv = new BytesSegment(buf);
                        for (int i = 0; i < 128; i++) {
                            ch.Update(bv);
                        }
                    },
                    ["encrypt 128 KiB 128 times ('speck0' (speck-128/128-ctr) without multi-threading)"] = () => {
                        Speck0Test(samplekey, 128 * 1024, 128, false);
                    },
                    ["encrypt 128 KiB 128 times ('speck0' (speck-128/128-ctr))"] = () => {
                        Speck0Test(samplekey, 128 * 1024, 128, true);
                    },
                    ["encrypt 16 KiB 1024 times ('speck0' (speck-128/128-ctr) without multi-threading)"] = () => {
                        Speck0Test(samplekey, 16 * 1024, 1024, false);
                    },
                    ["encrypt 16 KiB 1024 times ('speck0' (speck-128/128-ctr))"] = () => {
                        Speck0Test(samplekey, 16 * 1024, 1024, true);
                    },
                    ["encrypt 3 B 1024 * 1024 times ('speck0' (speck-128/128-ctr))"] = () => {
                        Speck0Test(samplekey, 3, 1024 * 1024, true);
                    },
                    ["encrypt 128 KiB 128 times ('speck064' (speck-64/128-ctr))"] = () => {
                        var ch = new Speck.Ctr64128(samplekey);
                        ch.IV = new byte[] { 80, 80, 80, 80, 80, 80, 80, 80 };
                        var buf = new byte[128 * 1024];
                        var bv = new BytesSegment(buf);
                        for (int i = 0; i < 128; i++) {
                            ch.Update(bv);
                        }
                    },
                    //["Increment 128m times (single thread)"] = () => {
                    //    var x = 0;
                    //    for (int i = 0; i < 128 * 1024 * 1024; i++) {
                    //        x++;
                    //    }
                    //},
                    //["Interlocked.Increment 128m times (single thread)"] = () => {
                    //    var x = 0;
                    //    for (int i = 0; i < 128 * 1024 * 1024; i++) {
                    //        Interlocked.Increment(ref x);
                    //    }
                    //},
                    //["Interlocked.Increment 128m times (2 threads)"] = () => {
                    //    var x = 0;
                    //    Task[] tasks = new Task[2];
                    //    for (int t = 0; t < tasks.Length; t++) {
                    //        tasks[t] = Task.Run(() => {
                    //            for (int i = 0; i < 128 * 1024 * 1024; i++) {
                    //                Interlocked.Increment(ref x);
                    //            }
                    //        });
                    //    }
                    //    Task.WaitAll(tasks);
                    //},
                    //["lock and incr 128m times (single thread)"] = () => {
                    //    var x = 0;
                    //    var l = new object();
                    //    for (int i = 0; i < 128 * 1024 * 1024; i++) {
                    //        lock (l)
                    //            x++;
                    //    }
                    //},
                    //["lock and incr 128m times (2 threads)"] = () => {
                    //    var x = 0;
                    //    var l = new object();
                    //    Task[] tasks = new Task[2];
                    //    for (int t = 0; t < tasks.Length; t++) {
                    //        tasks[t] = Task.Run(() => {
                    //            for (int i = 0; i < 128 * 1024 * 1024; i++) {
                    //                lock (l)
                    //                    x++;
                    //            }
                    //        });
                    //    }
                    //    Task.WaitAll(tasks);
                    //},
                    ["localhost socket 1"] = () => {
                        var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(20000, 60000));
                        var listener = new Listener(ep) { LogInfo = false };
                        listener.Accepted += (x) => NaiveUtils.RunAsyncTask(async () => {
                            var stream = new SocketStream1(x.Client);
                            var buf = new byte[32 * 1024];
                            while (await stream.ReadAsync(buf) > 0) {
                            }
                            x.Close();
                        });
                        listener.Run().Forget();
                        TestSocketWrite(sw, ep, 32 * 1024, 1024);
                        listener.Stop();
                    },
                    ["localhost socket 1 (4 bytes read buffer)"] = () => {
                        var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(20000, 60000));
                        var listener = new Listener(ep) { LogInfo = false };
                        listener.Accepted += (x) => NaiveUtils.RunAsyncTask(async () => {
                            var stream = new SocketStream1(x.Client);
                            var buf = new byte[4];
                            while (await stream.ReadAsync(buf) > 0) {
                            }
                            x.Close();
                        });
                        listener.Run().Forget();
                        TestSocketWrite(sw, ep, 32 * 1024, 1024);
                        listener.Stop();
                    },
                    ["localhost socket 1 (4 bytes read buffer without smart buffer)"] = () => {
                        var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(20000, 60000));
                        var listener = new Listener(ep) { LogInfo = false };
                        listener.Accepted += (x) => NaiveUtils.RunAsyncTask(async () => {
                            var stream = new SocketStream1(x.Client);
                            stream.EnableSmartReadBuffer = false;
                            var buf = new byte[4];
                            while (await stream.ReadAsync(buf) > 0) {
                            }
                            x.Close();
                        });
                        listener.Run().Forget();
                        TestSocketWrite(sw, ep, 32 * 1024, 1024);
                        listener.Stop();
                    },
                    ["localhost socket 1 (4 bytes read buffer without smart buffer/sync)"] = () => {
                        var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(20000, 60000));
                        var listener = new Listener(ep) { LogInfo = false };
                        listener.Accepted += (x) => NaiveUtils.RunAsyncTask(async () => {
                            var stream = new SocketStream1(x.Client);
                            stream.EnableSmartReadBuffer = false;
                            stream.EnableSmartSyncRead = false;
                            var buf = new byte[4];
                            while (await stream.ReadAsync(buf) > 0) {
                            }
                            x.Close();
                        });
                        listener.Run().Forget();
                        TestSocketWrite(sw, ep, 32 * 1024, 1024);
                        listener.Stop();
                    },
                    ["localhost socket 2"] = () => {
                        var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(20000, 60000));
                        var listener = new Listener(ep) { LogInfo = false };
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
                            sw.Restart();
                            var buf = new byte[32 * 1024];
                            for (int i = 0; i < 1024; i++) {
                                await stream.WriteAsync(buf);
                            }
                            socket.Close();
                        }).RunSync();
                        listener.Stop();
                    },
                    ["localhost socket sync"] = () => {
                        var ep = new IPEndPoint(IPAddress.Loopback, NaiveUtils.Random.Next(20000, 60000));
                        var listener = new Listener(ep) { LogInfo = false };
                        listener.Accepted += (x) => {
                            var stream = new SocketStream2(x.Client);
                            var buf = new byte[32 * 1024];
                            while (stream.Read(buf) > 0) {
                            }
                            x.Close();
                        };
                        listener.Run().Forget();
                        {
                            var socket = NaiveUtils.ConnectTcpAsync(AddrPort.Parse(ep.ToString()), 5000).RunSync();
                            var stream = new SocketStream2(socket);
                            sw.Restart();
                            var buf = new byte[32 * 1024];
                            for (int i = 0; i < 1024; i++) {
                                stream.Write(buf);
                            }
                            socket.Close();
                        }
                        listener.Stop();
                    }
                };
                void runTest(string name, Action action)
                {
                    cmd.Write($"{name}...GC...");
                    sw.Restart();
                    GC.Collect();
                    try {
                        GC.WaitForFullGCComplete();
                    } catch (Exception) {
                        cmd.Write("WaitGCFailed...");
                        Thread.Sleep(200);
                    }
                    sw.Stop();
                    cmd.WriteLine(sw.ElapsedTicks + " ticks.");
                    cmd.Write("Running...");
                    sw.Restart();
                    action();
                    sw.Stop();
                    cmd.WriteLine($"\tresult: {sw.ElapsedMilliseconds} ms, or {sw.ElapsedTicks} ticks");
                }
                void runAll()
                {
                    foreach (var item in tests) {
                        runTest(item.Key, item.Value);
                    }
                }
                cmd.WriteLine($"Stopwatch.Frequency: {Stopwatch.Frequency}");
                cmd.WriteLine($"Stopwatch.IsHighResolution: {Stopwatch.IsHighResolution}");
                cmd.WriteLine($"Environment.ProcessorCount: {pcount}");
                var selections = tests.Keys.ToList();
                selections.Insert(0, "ALL");
                while (true) {
                    var selection = cmd.Select("Select a test:", selections, "(input other text to exit): ", false);
                    if (selection == null)
                        return;
                    if (selection == "ALL")
                        runAll();
                    else
                        runTest(selection, tests[selection]);
                    cmd.WriteLine("");
                }
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

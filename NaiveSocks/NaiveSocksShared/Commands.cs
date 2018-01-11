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

        public static void AddCommands(CommandHub cmdHub, Controller c, string prefix)
        {
            cmdHub.AddCmdHandler(prefix + "c", command => {
                var arr = c.InConnections.ToArray();
                foreach (var item in arr) {
                    command.WriteLine(item.ToString());
                }
                command.WriteLine($"# {arr.Length} connections");
            });
            cmdHub.AddCmdHandler(prefix + "reload", command => {
                c.Reload();
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
                            Buffer.BlockCopy(new byte[32 * 1024], 0, arr, 0, 32 * 1024);
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
                        listener.Start().Forget();
                        NaiveUtils.RunAsyncTask(async () => {
                            var socket = await NaiveUtils.ConnectTCPAsync(AddrPort.Parse(ep.ToString()), 5000);
                            var stream = new SocketStream1(socket);
                            sw.Restart();
                            var buf = new byte[32 * 1024];
                            for (int i = 0; i < 1024; i++) {
                                await stream.WriteAsync(buf);
                            }
                            socket.Close();
                        }).RunSync();
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
                        listener.Start().Forget();
                        NaiveUtils.RunAsyncTask(async () => {
                            var socket = await NaiveUtils.ConnectTCPAsync(AddrPort.Parse(ep.ToString()), 5000);
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
                        listener.Start().Forget();
                        {
                            var socket = NaiveUtils.ConnectTCPAsync(AddrPort.Parse(ep.ToString()), 5000).RunSync();
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
                var outs = from x in c.OutAdapters where x is NaiveMOutAdapter select (x as NaiveMOutAdapter);
                if (keep) {
                    foreach (var item in outs) {
                        item.ping_enabled = pingEnabled;
                    }
                    return;
                }
                var ins = from x in c.InAdapters where x is NaiveProtocol.NaiveMServerBase select (x as NaiveProtocol.NaiveMServerBase).nmsList;
                foreach (IEnumerable<NaiveMSocks> item in (from x in outs select from y in x.ncsPool select y.nms).Union(ins)) {
                    foreach (var poolItem in item) {
                        var task = NaiveUtils.RunAsyncTask(async () => {
                            try {
                                await poolItem?.Ping((t) => cmd.WriteLine($"{poolItem.BaseChannels}: {t}"), true);
                            } catch (Exception e) {
                                cmd.WriteLine(Logging.getExceptionText(e, $"{poolItem?.BaseChannels} pinging"));
                            }
                        });
                        tasks.Add(task);
                    }
                }
                var timeout = Task.Delay(3 * 1000);
                if (Task.WaitAny(Task.WhenAll(tasks.ToArray()), timeout) == 1) {
                    cmd.WriteLine($"waiting timed out.");
                }
            }, "Usage: mping [start|stop]");
        }
    }
}

using Naive.HttpSvr;
using Nett;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace NaiveSocks
{
    public partial class Controller
    {
        public class ConfigFile
        {
            public string Content;
            public string Path;

            public static ConfigFile FromPath(string path)
            {
                return new ConfigFile {
                    Content = File.ReadAllText(path, Encoding.UTF8),
                    Path = path
                };
            }

            public static ConfigFile FromContent(string content)
            {
                return new ConfigFile {
                    Content = content
                };
            }
        }

        class ConfigLoader
        {
            public Logger Logger { get; }
            private Types Types => Types.Current;

            public ConfigLoader(Logger logger)
            {
                Logger = logger;
            }

            private InAdapter NewRegisteredInType(TomlTable tt, string name)
                => NewRegisteredAdapter(Types.RegisteredInTypes, Types.RegisteredInCreators, tt, name);

            private OutAdapter NewRegisteredOutType(TomlTable tt, string name)
                => NewRegisteredAdapter(Types.RegisteredOutTypes, Types.RegisteredOutCreators, tt, name);

            private T NewRegisteredAdapter<T>(Dictionary<string, Type> types, Dictionary<string, Func<TomlTable, T>> creators,
                TomlTable tt, string name) where T : Adapter
            {
                var instance = NewRegisteredType<T>(types, creators, tt);
                instance.Name = name;
                instance.SetLogger(Logger);
                instance.SetConfig(tt);
                return instance;
            }

            private T NewRegisteredType<T>(Dictionary<string, Type> types, Dictionary<string, Func<TomlTable, T>> creators, TomlTable table)
                where T : class
            {
                var strType = table.Get<string>("type");
                if (types.TryGetValue(strType, out var type) == false) {
                    if (creators.TryGetValue(strType, out var ctor) == false) {
                        throw new Exception($"type '{strType}' as '{typeof(T)}' not found");
                    }
                    return ctor.Invoke(table) ?? throw new Exception($"creator '{strType}' returns null");
                }
                return table.Get(type) as T;
            }

            public LoadedConfig LoadConfig(ConfigFile cf, LoadedConfig newcfg)
            {
                var toml = cf.Content;
                newcfg = newcfg ?? new LoadedConfig();
                if (cf.Path != null) {
                    newcfg.FilePath = cf.Path;
                    newcfg.WorkingDirectory = Path.GetDirectoryName(cf.Path);
                }
                Config t;
                TomlTable tomlTable;
                var refs = new List<AdapterRef>();
                try {
                    var tomlSettings = CreateTomlSettings(refs);
                    tomlTable = Toml.ReadString(toml, tomlSettings);
                    t = tomlTable.Get<Config>();
                } catch (Exception e) {
                    Logger.exception(e, Logging.Level.Error, "TOML Error");
                    return null;
                }

                newcfg.TomlTable = tomlTable;
                newcfg.SocketImpl = t.socket_impl;
                newcfg.LoggingLevel = t.log_level;
                newcfg.Aliases = t.aliases;
                newcfg.DebugFlags = t?.debug?.flags ?? new string[0];

                int failedCount = 0;
                if (t.@in != null)
                    foreach (var item in t.@in) {
                        try {
                            var tt = item.Value;
                            var adapter = NewRegisteredInType(tt, item.Key);
                            newcfg.InAdapters.Add(adapter);
                        } catch (Exception e) {
                            Logger.exception(e, Logging.Level.Error, $"TOML table 'in.{item.Key}':");
                            failedCount++;
                        }
                    }
                if (t.@out != null)
                    foreach (var item in t.@out) {
                        try {
                            var tt = item.Value;
                            var adapter = NewRegisteredOutType(tt, item.Key);
                            newcfg.OutAdapters.Add(adapter);
                        } catch (Exception e) {
                            Logger.exception(e, Logging.Level.Error, $"TOML table 'out.{item.Key}':");
                            failedCount++;
                        }
                    }
                foreach (var r in refs.Where(x => x.IsTable)) {
                    var tt = r.Ref as TomlTable;
                    try {
                        string name = null;
                        if (tt.TryGetValue("name", out string n)) {
                            name = n;
                        }
                        if (name == null) {
                            int i = 0;
                            do {
                                name = $"_{tt["type"].Get<string>()}_" + ((i++ == 0) ? "" : i.ToString());
                            } while (newcfg.OutAdapters.Any(x => x.Name == name));
                        }
                        var adapter = NewRegisteredOutType(tt, name);
                        r.Adapter = adapter;
                        newcfg.OutAdapters.Add(adapter);
                    } catch (Exception e) {
                        Logger.exception(e, Logging.Level.Error, $"TOML inline table:");
                        failedCount++;
                    }
                }
                bool notExistAndNeed(string name) =>
                        newcfg.OutAdapters.Any(x => x.Name == name) == false
                            /* && refs.Any(x => x.IsName && x.Ref as string == name) */;
                if (notExistAndNeed("direct")) {
                    newcfg.OutAdapters.Add(new DirectOutAdapter() { Name = "direct" });
                }
                if (notExistAndNeed("fail")) {
                    newcfg.OutAdapters.Add(new FailAdapter() { Name = "fail" });
                }
                foreach (var r in refs) {
                    if (r.IsName) {
                        r.Adapter = FindAdapter<IAdapter>(newcfg, r.Ref as string, -1);
                    }
                }
                newcfg.FailedCount = failedCount;
                return newcfg;
            }

            private static TomlSettings CreateTomlSettings(List<AdapterRef> refs)
            {
                return TomlSettings.Create(cfg => cfg
                        .AllowNonstandard(true)
                        .ConfigureType<IPEndPoint>(type => type
                            .WithConversionFor<TomlString>(convert => convert
                                .ToToml(custom => custom.ToString())
                                .FromToml(tmlString => Utils.CreateIPEndPoint(tmlString.Value))))
                        .ConfigureType<AddrPort>(type => type
                            .WithConversionFor<TomlString>(convert => convert
                                .ToToml(custom => custom.ToString())
                                .FromToml(tmlString => AddrPort.Parse(tmlString.Value))))
                        .ConfigureType<AdapterRef>(type => type
                            .WithConversionFor<TomlString>(convert => convert
                                .ToToml(custom => custom.Ref.ToString())
                                .FromToml(tmlString => {
                                    var a = new AdapterRef { IsName = true, Ref = tmlString.Value };
                                    refs.Add(a);
                                    return a;
                                }))
                            .WithConversionFor<TomlTable>(convert => convert
                                .FromToml(tml => {
                                    var a = new AdapterRef { IsTable = true, Ref = tml };
                                    refs.Add(a);
                                    return a;
                                })))
                        .ConfigureType<AdapterRefOrArray>(type => type
                            .WithConversionFor<TomlString>(convert => convert
                                .FromToml(tmlString => {
                                    var str = tmlString.Value;
                                    if (str.Contains('|')) {
                                        var splits = str.Split('|');
                                        var aarr = new AdapterRef[splits.Length];
                                        for (int i = 0; i < splits.Length; i++) {
                                            var a = new AdapterRef { IsName = true, Ref = splits[i] };
                                            refs.Add(a);
                                            aarr[i] = a;
                                        }
                                        return new AdapterRefOrArray { obj = aarr };
                                    } else {
                                        return new AdapterRefOrArray { obj = tmlString.Get<AdapterRef>() };
                                    }
                                }))
                            .WithConversionFor<TomlTable>(convert => convert
                                .FromToml(tmlTable => {
                                    var a = new AdapterRefOrArray { obj = tmlTable.Get<AdapterRef>() };
                                    return a;
                                }))
                            .WithConversionFor<TomlArray>(convert => convert
                                .FromToml(tmlTable => {
                                    var a = new AdapterRefOrArray { obj = tmlTable.Get<AdapterRef[]>() };
                                    return a;
                                }))
                            .WithConversionFor<TomlTableArray>(convert => convert
                                .FromToml(tmlTable => {
                                    var a = new AdapterRefOrArray { obj = tmlTable.Get<AdapterRef[]>() };
                                    return a;
                                })))
                        .ConfigureType<StringOrArray>(type => type
                            .WithConversionFor<TomlString>(convert => convert
                                .FromToml(tmlString => {
                                    return new StringOrArray { obj = tmlString.Get<string>() };
                                }))
                            .WithConversionFor<TomlTable>(convert => convert
                                .FromToml(tmlTable => {
                                    return new StringOrArray { obj = tmlTable.Get<string>() };
                                }))
                            .WithConversionFor<TomlArray>(convert => convert
                                .FromToml(tmlArray => {
                                    return new StringOrArray { obj = tmlArray.Get<string[]>() };
                                })))
                    );
            }
        }
    }
}

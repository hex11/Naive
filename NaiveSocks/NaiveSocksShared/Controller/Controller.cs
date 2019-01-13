﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;
using Nett;

namespace NaiveSocks
{
    public class Controller
    {
        public LoadedConfig CurrentConfig = new LoadedConfig();

        public List<InAdapter> InAdapters => CurrentConfig.InAdapters;
        public List<OutAdapter> OutAdapters => CurrentConfig.OutAdapters;

        public Logger Logger { get; } = new Logger();

        public class LoadedConfig
        {
            public List<InAdapter> InAdapters = new List<InAdapter>();
            public List<OutAdapter> OutAdapters = new List<OutAdapter>();

            public Dictionary<string, string> Aliases = new Dictionary<string, string>();

            public string[] DebugFlags;

            public string SocketImpl;

            public Logging.Level LoggingLevel;

            public string FilePath;
            public string WorkingDirectory = ".";

            public int FailedCount;

            public TomlTable TomlTable;
        }

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

        Logging.Level LoggingLevel => CurrentConfig.LoggingLevel;

        public List<InConnection> InConnections = new List<InConnection>();
        public object InConnectionsLock => InConnections;

        private int _totalHandledConnections, _failedConnections;
        public int TotalHandledConnections => _totalHandledConnections;
        public int TotalFailedConnections => _failedConnections;
        public int RunningConnections => InConnections.Count;

        public Dictionary<string, Type> RegisteredInTypes = new Dictionary<string, Type>();
        public Dictionary<string, Type> RegisteredOutTypes = new Dictionary<string, Type>();
        public Dictionary<string, Func<TomlTable, InAdapter>> RegisteredInCreators = new Dictionary<string, Func<TomlTable, InAdapter>>();
        public Dictionary<string, Func<TomlTable, OutAdapter>> RegisteredOutCreators = new Dictionary<string, Func<TomlTable, OutAdapter>>();

        public event Action<InConnection> NewConnection;
        public event Action<InConnection> EndConnection;

        public event Action<TomlTable> ConfigTomlLoading;
        public event Action<TomlTable> ConfigTomlLoaded;

        public Func<ConfigFile> FuncGetConfigFile;

        public string WorkingDirectory => CurrentConfig?.WorkingDirectory ?? ".";
        public string ProcessFilePath(string input)
        {
            if (input == null)
                return null;
            if (WorkingDirectory?.Length == 0 || WorkingDirectory == "." || Path.IsPathRooted(input))
                return input;
            return Path.Combine(WorkingDirectory, input);
        }

        public Controller()
        {
            RegisterBuiltInTypes();
        }

        private void RegisterBuiltInTypes()
        {
            RegisteredInTypes.Add("direct", typeof(DirectInAdapter));
            RegisteredInTypes.Add("socks", typeof(SocksInAdapter));
            RegisteredInTypes.Add("socks5", typeof(SocksInAdapter));
            RegisteredInTypes.Add("http", typeof(HttpInAdapter));
            RegisteredInTypes.Add("tlssni", typeof(TlsSniInAdapter));
            RegisteredInTypes.Add("naive", typeof(NaiveMInAdapter));
            RegisteredInTypes.Add("naivec", typeof(NaiveMInAdapter));
            RegisteredInTypes.Add("naive0", typeof(Naive0InAdapter));
            RegisteredInTypes.Add("ss", typeof(SsInAdapter));

            RegisteredOutTypes.Add("direct", typeof(DirectOutAdapter));
            RegisteredOutTypes.Add("socks", typeof(SocksOutAdapter));
            RegisteredOutTypes.Add("socks5", typeof(SocksOutAdapter));
            RegisteredOutTypes.Add("http", typeof(HttpOutAdapter));
            RegisteredOutTypes.Add("naive", typeof(NaiveMOutAdapter));
            RegisteredOutTypes.Add("naivec", typeof(NaiveMOutAdapter));
            RegisteredOutTypes.Add("naive0", typeof(Naive0OutAdapter));
            RegisteredOutTypes.Add("ss", typeof(SsOutAdapter));
            RegisteredOutTypes.Add("webcon", typeof(WebConAdapter));
            RegisteredOutTypes.Add("webfile", typeof(WebFileAdapter));

            RegisteredOutTypes.Add("router", typeof(RouterAdapter));
            RegisteredOutTypes.Add("fail", typeof(FailAdapter));
            RegisteredOutTypes.Add("nnetwork", typeof(NNetworkAdapter));
        }

        public void LoadConfigFileOrWarning(string path, bool now = true)
        {
            FuncGetConfigFile = () => {
                if (File.Exists(path)) {
                    return ConfigFile.FromPath(path);
                }
                warning($"configuation file '{path}' does not exist.");
                return null;
            };
            if (now)
                Load();
        }

        public void LoadConfigFileFromMultiPaths(string[] paths, bool now = true)
        {
            FuncGetConfigFile = () => {
                foreach (var item in paths) {
                    if (File.Exists(item)) {
                        info("using configuration file: " + item);
                        return ConfigFile.FromPath(item);
                    }
                }
                warning("configuration file not found. searched paths:\n\t" + string.Join("\n\t", paths));
                return null;
            };
            if (now)
                Load();
        }

        public void Load() => Load(null);
        public void Load(ConfigFile configFile)
        {
            var config = GetConfigFileOrLog(configFile);
            if (config != null) {
                LoadConfig(config);
            }
        }

        ConfigFile GetConfigFileOrLog(ConfigFile configFile)
        {
            var config = configFile ?? FuncGetConfigFile?.Invoke();
            if (config == null)
                warning($"no configuration.");
            return config;
        }

        public void LoadConfigStr(string toml)
        {
            LoadConfig(ConfigFile.FromContent(toml));
        }

        public void LoadConfig(ConfigFile configFile)
        {
            SetCurrentConfig(LoadConfig(configFile, null) ?? CurrentConfig);
            Logger.info($"configuration loaded. {InAdapters.Count} InAdapters, {OutAdapters.Count} OutAdapters.");
            if (CurrentConfig.FailedCount > 0)
                Logger.warning($"And {CurrentConfig.FailedCount} ERRORs");
        }

        private void SetCurrentConfig(LoadedConfig loadedConfig)
        {
            CurrentConfig = loadedConfig;
            if (!loadedConfig.SocketImpl.IsNullOrEmpty())
                MyStream.SetSocketImpl(loadedConfig.SocketImpl);
            MyStream.TwoWayCopier.DefaultUseLoggerAsVerboseLogger = IsDebugFlagEnabled("copier_v");
            Channel.Debug = IsDebugFlagEnabled("mux_v");
            YASocket.Debug = IsDebugFlagEnabled("ya_v");
            ConfigTomlLoaded?.Invoke(loadedConfig.TomlTable);
        }

        private LoadedConfig LoadConfig(ConfigFile cf, LoadedConfig newcfg)
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
                var tomlSettings = TomlSettings.Create(cfg => cfg
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
                                    var a = new AdapterRefOrArray { obj = tmlString.Get<AdapterRef>() };
                                    return a;
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
                tomlTable = Toml.ReadString(toml, tomlSettings);
                t = tomlTable.Get<Config>();
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "TOML Error");
                return null;
            }

            ConfigTomlLoading?.Invoke(tomlTable);

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

        private void SetLogger(Adapter adapter)
        {
            adapter.Logger.ParentLogger = this.Logger;
            adapter.Logger.Stamp = adapter.Name;
        }

        private InAdapter NewRegisteredInType(TomlTable tt, string name)
            => NewRegisteredAdapter(RegisteredInTypes, RegisteredInCreators, tt, name);

        private OutAdapter NewRegisteredOutType(TomlTable tt, string name)
            => NewRegisteredAdapter(RegisteredOutTypes, RegisteredOutCreators, tt, name);

        private T NewRegisteredAdapter<T>(Dictionary<string, Type> types, Dictionary<string, Func<TomlTable, T>> creators,
            TomlTable tt, string name) where T : Adapter
        {
            var instance = NewRegisteredType<T>(types, creators, tt);
            instance.Name = name;
            SetLogger(instance);
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

        public void Reload() => Reload(null);
        public void Reload(ConfigFile configFile)
        {
            warning("==========Reload==========");
            var newCfgFile = GetConfigFileOrLog(configFile);
            if (newCfgFile == null)
                return;
            var newCfg = LoadConfig(newCfgFile, null);
            if (newCfg == null) {
                error("failed to load the new configuration.");
                info("still running with previous configuration.");
                return;
            }
            info($"new configuration loaded. {newCfg.InAdapters.Count} InAdapters, {newCfg.OutAdapters.Count} OutAdapters.");
            var oldCfg = CurrentConfig;
            var oldCanReload = InAdapters.Union<Adapter>(OutAdapters)
                                        .Select(x => x as ICanReload)
                                        .Where(x => x != null).ToList();
            var newCanReload = newCfg.InAdapters.Union<Adapter>(newCfg.OutAdapters)
                                        .Select(x => x as ICanReload)
                                        .Where(x => x != null).ToList();
            var oldNewCanReload = oldCanReload.Where(x => {
                return newCanReload.Any(y => y.Name == x.Name && y.GetType() == x.GetType());
            }).ToList();
            warning("stopping old adapters...");
            this.Stop(CurrentConfig, oldNewCanReload);
            warning("starting new adapters...");
            SetCurrentConfig(newCfg);
            this.Start(oldNewCanReload);
        }

        public void Start() => Start(null);
        private void Start(List<ICanReload> oldAdapters)
        {
            bool checkIcr(IAdapter item)
            {
                if (oldAdapters != null && item is ICanReload icr) {
                    var old = oldAdapters.Find(x => x.Name == icr.Name && x.GetType() == icr.GetType());
                    if (old != null) {
                        oldAdapters.Remove(old);
                        if (icr.Reloading(old))
                            return false;
                    }
                }
                return true;
            }
            int failedCount = 0;
            foreach (var item in OutAdapters) {
                info($"OutAdapter '{item.Name}' = {item.ToString(false)}");
                try {
                    InitAdapter(item);
                    item.StartInternal(checkIcr(item));
                } catch (Exception e) {
                    Logger.exception(e, Logging.Level.Error, $"starting OutAdapter '{item.Name}' = {item}");
                    failedCount++;
                }
            }
            foreach (var item in InAdapters) {
                info($"InAdapter '{item.Name}' = {item.ToString(false)} -> {item.@out?.Adapter?.Name?.Quoted() ?? "(No OutAdapter)"}");
                try {
                    InitAdapter(item);
                    item.StartInternal(checkIcr(item));
                } catch (Exception e) {
                    Logger.exception(e, Logging.Level.Error, $"starting InAdapter '{item.Name}' = {item}");
                    failedCount++;
                }
            }
            if (failedCount > 0) {
                Logger.warning($"=====Adapters Started===== ({failedCount} FAILURES)");
            } else {
                Logger.info($"=====Adapters Started=====");
            }
        }

        public void AddInAdapter(InAdapter adap, bool init)
        {
            InAdapters.Add(adap);
            SetLogger(adap);
            //adap.SetConfig(null);
            if (init) {
                InitAdapter(adap);
            }
        }

        private void InitAdapter(Adapter item)
        {
            item.Init(this);
        }

        public void Stop() => Stop(CurrentConfig, null);
        private void Stop(LoadedConfig config, List<ICanReload> listNotCallStop)
        {
            foreach (var item in config.InAdapters) {
                var reloading = item is ICanReload icr && listNotCallStop?.Contains(icr) == true;
                info($"stopping{(reloading ? " (reloading)" : "")} InAdapter: {item}");
                item.StopInternal(!reloading);
            }
            foreach (var item in config.OutAdapters) {
                var reloading = item is ICanReload icr && listNotCallStop?.Contains(icr) == true;
                info($"stopping{(reloading ? " (reloading)" : "")} OutAdapter: '{item.Name}' {item}");
                item.StopInternal(!reloading);
            }
            Logger.info($"=====Adapters Stopped=====");
        }

        public void Reset()
        {
            CurrentConfig = new LoadedConfig();
        }

        public bool IsDebugFlagEnabled(string flag)
        {
            return CurrentConfig.DebugFlags.Contains(flag);
        }

        public virtual Task HandleInConnection(InConnection inConnection)
        {
            if (inConnection == null)
                throw new ArgumentNullException(nameof(inConnection));

            if (inConnection.InAdapter is IInAdapter ina) {
                return HandleInConnection(inConnection, ina.@out.Adapter as IConnectionHandler);
            } else {
                throw new ArgumentException($"InConnection.InAdapter({inConnection.InAdapter}) does not implement IInAdapter.");
            }
        }

        public virtual Task HandleInConnection(InConnection inc, string outAdapterName)
        {
            if (string.IsNullOrEmpty(outAdapterName))
                throw new ArgumentException("outAdapterName is null or empty");
            IConnectionHandler selectedAdapter = FindOutAdapter(outAdapterName);
            if (selectedAdapter == null)
                if (LoggingLevel <= Logging.Level.Warning)
                    warning($"out adapter '{outAdapterName}' not found");
            return HandleInConnection(inc, selectedAdapter);
        }

        public virtual Task HandleInConnection(InConnection inc, AdapterRef outAdapterRef)
        {
            if (outAdapterRef == null)
                throw new ArgumentNullException(nameof(outAdapterRef));
            IConnectionHandler adapter = outAdapterRef.Adapter as IConnectionHandler;
            if (adapter == null)
                if (LoggingLevel <= Logging.Level.Warning)
                    warning($"out adapter ({outAdapterRef}) not found");
            return HandleInConnection(inc, adapter);
        }

        public virtual async Task HandleInConnection(InConnection inc, IConnectionHandler outAdapter)
        {
            try {
                if (outAdapter == null) {
                    warning($"'{inc.InAdapter.Name}' {inc} -> (no out adapter)");
                    return;
                }
                onConnectionBegin(inc, outAdapter);
                int redirectCount = 0;
                while (true) {
                    inc.RunningHandler = outAdapter;
                    await outAdapter.HandleConnection(inc).CAF();
                    if (inc.IsHandled || !inc.IsRedirected) {
                        break;
                    }
                    if (++redirectCount >= 10) {
                        error($"'{inc.InAdapter.Name}' {inc} too many redirects, last redirect: {outAdapter.Name}");
                        return;
                    }
                    var nextAdapter = inc.Redirected?.Adapter as IConnectionHandler;
                    if (nextAdapter == null) {
                        warning($"'{inc.InAdapter.Name}' {inc} was redirected by '{outAdapter.Name}'" +
                                $" to '{inc.Redirected}' which can not be found.");
                        return;
                    }
                    outAdapter = nextAdapter;
                    if (LoggingLevel <= Logging.Level.None)
                        debug($"'{inc.InAdapter.Name}' {inc} was redirected to '{inc.Redirected}'");
                    inc.Redirected = null;
                }
            } catch (Exception e) {
                await onConnectionException(inc, e).CAF();
            } finally {
                await onConnectionEnd(inc).CAF();
            }
        }

        public virtual async Task<ConnectResponse> Connect(ConnectRequest request, IAdapter outAdapter)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ConnectResult result;

            if (outAdapter == null) {
                warning($"'{request.InAdapter.Name}' {request} -> (no out adapter)");
                result = new ConnectResult(null, "no out adapter");
                goto RETURN;
            }
            onConnectionBegin(request, outAdapter);
            try {
                int redirectCount = 0;
                while (true) {
                    AdapterRef redirected;
                    ConnectResult r;
                    request.RunningHandler = outAdapter;
                    if (outAdapter is IConnectionProvider cp) {
                        r = await cp.Connect(request).CAF();
                    } else if (outAdapter is IConnectionHandler ch) {
                        r = await OutAdapter2.ConnectWrapper(ch, request).CAF();
                    } else {
                        error($"{outAdapter} implement neither IConnectionProvider nor IConnectionHandler.");
                        result = new ConnectResult(null, "wrong adapter");
                        goto RETURN;
                    }
                    if (!r.IsRedirected) {
                        await request.HandleAndGetStream(r);
                        result = r;
                        goto RETURN;
                    }
                    redirected = r.Redirected;
                    if (++redirectCount >= 10) {
                        error($"'{request.InAdapter.Name}' {request} too many redirects, last redirect: {outAdapter.Name}");
                        result = new ConnectResult(null, "too many redirects");
                        goto RETURN;
                    }
                    var nextAdapter = redirected.Adapter;
                    if (nextAdapter == null) {
                        warning($"'{request.InAdapter.Name}' {request} was redirected by '{outAdapter.Name}'" +
                                $" to '{redirected}' which can not be found.");
                        result = new ConnectResult(null, "redirect not found");
                        goto RETURN;
                    }
                    outAdapter = nextAdapter;
                    if (LoggingLevel <= Logging.Level.None)
                        debug($"'{request.InAdapter.Name}' {request} was redirected to '{redirected}'");
                }
            } catch (Exception e) {
                await onConnectionException(request, e);
                await onConnectionEnd(request);
                throw;
            }
        RETURN:
            return new ConnectResponse(result, request);
        }

        private void onConnectionBegin(InConnection inc, IAdapter outAdapter)
        {
            if (LoggingLevel <= Logging.Level.None)
                debug($"'{inc.InAdapter.Name}' {inc} -> '{outAdapter.Name}'");
            try {
                lock (InConnectionsLock) {
                    inc.InAdapter.GetAdapter().CreatedConnections++;
                    _totalHandledConnections++;
                    InConnections.Add(inc);
                    NewConnection?.Invoke(inc);
                }
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "event NewConnection");
            }
        }

        internal async Task onConnectionEnd(InConnection inc)
        {
            inc.IsFinished = true;
            inc.RunningHandler = null;
            if (LoggingLevel <= Logging.Level.None)
                debug($"{inc} End.");
            if (inc.IsHandled == false) {
                await inc.HandleFailed(null);
            }
            var dataStream = inc.DataStream;
            if (dataStream != null && dataStream.State != MyStreamState.Closed)
                await MyStream.CloseWithTimeout(dataStream);
            try {
                lock (InConnectionsLock) {
                    if (inc.ConnectResult?.Result != ConnectResultEnum.Conneceted)
                        _failedConnections++;
                    InConnections.Remove(inc);
                    EndConnection?.Invoke(inc);
                }
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "event EndConnection");
            }
        }

        internal async Task onConnectionException(InConnection inc, Exception e)
        {
            Logger.exception(e, Logging.Level.Error, $"Handling {inc}");
            if (inc.IsHandled == false) {
                await inc.HandleAndGetStream(new ConnectResult(null, ConnectResultEnum.Failed) {
                    FailedReason = $"exception: {e.GetType()}: {e.Message}"
                });
            }
        }

        public IConnectionHandler FindOutAdapter(string name)
        {
            return FindAdapter<IConnectionHandler>(name, -1);
        }

        public T FindAdapter<T>(string name) where T : class
        {
            return FindAdapter<T>(name, -1);
        }

        T FindAdapter<T>(string name, int ttl) where T : class
        {
            return FindAdapter<T>(CurrentConfig, name, ttl);
        }

        static T FindAdapter<T>(LoadedConfig cfg, string name, int ttl) where T : class
        {
            if (ttl == -1)
                ttl = 16;
            string new_name = null;
            if (cfg.Aliases?.TryGetValue(name, out new_name) == true) {
                if (ttl == 0)
                    throw new Exception("alias loop?");
                return FindAdapter<T>(cfg, new_name, ttl - 1);
            }
            return cfg.OutAdapters.Find(a => a.Name == name) as T
                ?? cfg.InAdapters.Find(a => a.Name == name) as T;
        }

        public AdapterRef AdapterRefFromName(string name)
        {
            return new AdapterRef() { IsName = true, Ref = name, Adapter = FindAdapter<Adapter>(name) };
        }

        private void debug(string str) => log(str, Logging.Level.None);
        private void info(string str) => log(str, Logging.Level.Info);
        private void warning(string str) => log(str, Logging.Level.Warning);
        private void error(string str) => log(str, Logging.Level.Error);

        private void log(string str, Logging.Level level)
        {
            if (LoggingLevel <= level)
                Logger.log(str, level);
        }
    }

    public class Config
    {
        public string socket_impl { get; set; }
        public Logging.Level log_level { get; set; } =
#if DEBUG
            Logging.Level.None;
#else
            Logging.Level.Info;
#endif

        public string dir { get; set; }

        public DebugSection debug { get; set; }

        public Dictionary<string, string> aliases { get; set; }

        public Dictionary<string, TomlTable> @in { get; set; }
        public Dictionary<string, TomlTable> @out { get; set; }

        public class DebugSection
        {
            public string[] flags { get; set; }
        }
    }
}
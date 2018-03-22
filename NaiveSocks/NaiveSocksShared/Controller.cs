using System;
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
        public Config CurrentConfig = new Config();

        public List<InAdapter> InAdapters => CurrentConfig.InAdapters;
        public List<OutAdapter> OutAdapters => CurrentConfig.OutAdapters;

        public Logger Logger { get; } = new Logger();

        public class Config
        {
            public List<InAdapter> InAdapters = new List<InAdapter>();
            public List<OutAdapter> OutAdapters = new List<OutAdapter>();

            public Dictionary<string, string> Aliases = new Dictionary<string, string>();

            public Logging.Level LoggingLevel;

            public string FilePath;
            public string WorkingDirectory = ".";
        }

        public class ConfigFile
        {
            public string Context;
            public string Path;

            public static ConfigFile FromPath(string path)
            {
                return new ConfigFile {
                    Context = File.ReadAllText(path, Encoding.UTF8),
                    Path = path
                };
            }

            public static ConfigFile FromContent(string content)
            {
                return new ConfigFile {
                    Context = content
                };
            }
        }

        Logging.Level LoggingLevel => CurrentConfig.LoggingLevel;

        public List<InConnection> InConnections = new List<InConnection>();

        private int _totalHandledConnections;
        public int TotalHandledConnections => _totalHandledConnections;
        public int RunningConnections => InConnections.Count;

        public Dictionary<string, Type> RegisteredInTypes = new Dictionary<string, Type>();
        public Dictionary<string, Type> RegisteredOutTypes = new Dictionary<string, Type>();
        public Dictionary<string, Func<TomlTable, InAdapter>> RegisteredInCreators = new Dictionary<string, Func<TomlTable, InAdapter>>();
        public Dictionary<string, Func<TomlTable, OutAdapter>> RegisteredOutCreators = new Dictionary<string, Func<TomlTable, OutAdapter>>();

        public event Action<InConnection> NewConnection;
        public event Action<InConnection> EndConnection;

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
            RegisteredInTypes.Add("naive", typeof(NaiveMInAdapter));
            RegisteredInTypes.Add("naivec", typeof(NaiveMInAdapter));
            RegisteredInTypes.Add("naive0", typeof(Naive0InAdapter));
            RegisteredInTypes.Add("ss", typeof(SSInAdapter));

            RegisteredOutTypes.Add("direct", typeof(DirectOutAdapter));
            RegisteredOutTypes.Add("socks", typeof(SocksOutAdapter));
            RegisteredOutTypes.Add("socks5", typeof(SocksOutAdapter));
            RegisteredOutTypes.Add("http", typeof(HttpOutAdapter));
            RegisteredOutTypes.Add("naive", typeof(NaiveMOutAdapter));
            RegisteredOutTypes.Add("naivec", typeof(NaiveMOutAdapter));
            RegisteredOutTypes.Add("naive0", typeof(Naive0OutAdapter));
            RegisteredOutTypes.Add("ss", typeof(SSOutAdapter));
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
            CurrentConfig = LoadConfig(configFile, null) ?? CurrentConfig;
            Logger.info($"configuration loaded. {InAdapters.Count} InAdapters, {OutAdapters.Count} OutAdapters.");
        }

        private Config LoadConfig(ConfigFile cf, Config newcfg)
        {
            var toml = cf.Context;
            newcfg = newcfg ?? new Config();
            if (cf.Path != null) {
                newcfg.FilePath = cf.Path;
                newcfg.WorkingDirectory = Path.GetDirectoryName(cf.Path);
            }
            NaiveSocks.Config t;
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
                                }))
                        )
                    );
                tomlTable = Toml.ReadString(toml, tomlSettings);
                t = tomlTable.Get<NaiveSocks.Config>();
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "TOML Error");
                return null;
            }
            ConfigTomlLoaded?.Invoke(tomlTable);
            newcfg.LoggingLevel = t.log_level;
            newcfg.Aliases = t.aliases;
            if (t.@in != null)
                foreach (var item in t.@in) {
                    try {
                        var tt = item.Value;
                        var adapter = NewRegisteredInType(tt, item.Key);
                        newcfg.InAdapters.Add(adapter);
                    } catch (Exception e) {
                        Logger.exception(e, Logging.Level.Error, $"TOML table 'in.{item.Key}':");
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
            CurrentConfig = newCfg;
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
            foreach (var item in OutAdapters) {
                info($"OutAdapter '{item.Name}': {item}");
                try {
                    item.InternalInit(this);
                    item.InternalStart(checkIcr(item));
                } catch (Exception e) {
                    Logger.exception(e, Logging.Level.Error, $"starting OutAdapter '{item.Name}': {item}");
                }
            }
            foreach (var item in InAdapters) {
                info($"InAdapter '{item.Name}': {item} -> {item.@out?.Adapter?.Name?.Quoted() ?? "(No OutAdapter)"}");
                try {
                    item.InternalInit(this);
                    item.InternalStart(checkIcr(item));
                } catch (Exception e) {
                    Logger.exception(e, Logging.Level.Error, $"starting InAdapter '{item.Name}': {item}");
                }
            }
            Logger.info($"=====Adapters Started=====");
        }

        public void Stop() => Stop(CurrentConfig, null);
        private void Stop(Config config, List<ICanReload> listNotCallStop)
        {
            foreach (var item in config.InAdapters) {
                var reloading = item is ICanReload icr && listNotCallStop?.Contains(icr) == true;
                info($"stopping{(reloading ? " (reloading)" : "")} InAdapter: {item}");
                item.InternalStop(!reloading);
            }
            foreach (var item in config.OutAdapters) {
                var reloading = item is ICanReload icr && listNotCallStop?.Contains(icr) == true;
                info($"stopping{(reloading ? " (reloading)" : "")} OutAdapter: '{item.Name}' {item}");
                item.InternalStop(!reloading);
            }
            Logger.info($"=====Adapters Stopped=====");
        }

        public void Reset()
        {
            CurrentConfig = new Config();
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
                    await outAdapter.HandleConnection(inc).CAF();
                    if (inc.CallbackCalled || !inc.IsRedirected) {
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

        public virtual async Task Connect(InConnection inc, IAdapter outAdapter, Func<ConnectResult, Task> callback)
        {
            if (inc == null)
                throw new ArgumentNullException(nameof(inc));

            try {
                if (outAdapter == null) {
                    warning($"'{inc.InAdapter.Name}' {inc} -> (no out adapter)");
                    return;
                }
                onConnectionBegin(inc, outAdapter);
                int redirectCount = 0;
                while (true) {
                    AdapterRef redirected;
                    ConnectResult r;
                    if (outAdapter is IConnectionProvider cp) {
                        r = await cp.Connect(inc).CAF();
                    } else if (outAdapter is IConnectionHandler ch) {
                        r = await OutAdapter2.Connect(ch.HandleConnection, inc).CAF();
                    } else {
                        error($"{outAdapter} implement neither IConnectionProvider nor IConnectionHandler.");
                        return;
                    }
                    if (!r.IsRedirected) {
                        await inc.SetConnectResult(r);
                        await callback(r);
                        return;
                    }
                    redirected = r.Redirected;
                    if (++redirectCount >= 10) {
                        error($"'{inc.InAdapter.Name}' {inc} too many redirects, last redirect: {outAdapter.Name}");
                        return;
                    }
                    var nextAdapter = redirected.Adapter;
                    if (nextAdapter == null) {
                        warning($"'{inc.InAdapter.Name}' {inc} was redirected by '{outAdapter.Name}'" +
                                $" to '{redirected}' which can not be found.");
                        return;
                    }
                    outAdapter = nextAdapter;
                    if (LoggingLevel <= Logging.Level.None)
                        debug($"'{inc.InAdapter.Name}' {inc} was redirected to '{redirected}'");
                    inc.Redirected = null;
                }
            } catch (Exception e) {
                await onConnectionException(inc, e).CAF();
            } finally {
                await onConnectionEnd(inc).CAF();
            }
        }

        private void onConnectionBegin(InConnection inc, IAdapter outAdapter)
        {
            System.Threading.Interlocked.Increment(ref _totalHandledConnections);
            if (LoggingLevel <= Logging.Level.None)
                debug($"'{inc.InAdapter.Name}' {inc} -> '{outAdapter.Name}'");
            lock (InConnections)
                InConnections.Add(inc);
            try {
                NewConnection?.Invoke(inc);
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "event NewConnection");
            }
        }

        private async Task onConnectionEnd(InConnection inc)
        {
            lock (InConnections)
                InConnections.Remove(inc);
            if (LoggingLevel <= Logging.Level.None)
                debug($"{inc} End.");
            if (inc.CallbackCalled == false) {
                await inc.SetConnectResult(ConnectResults.Failed, new IPEndPoint(0, 0));
            }
            if (inc.DataStream != null && inc.DataStream.State != MyStreamState.Closed)
                await MyStream.CloseWithTimeout(inc.DataStream);
            try {
                EndConnection?.Invoke(inc);
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "event EndConnection");
            }
        }

        private async Task onConnectionException(InConnection inc, Exception e)
        {
            Logger.exception(e, Logging.Level.Error, $"Handling {inc}");
            if (inc.CallbackCalled == false) {
                await inc.SetConnectResult(new ConnectResult(ConnectResults.Failed) {
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

        static T FindAdapter<T>(Config cfg, string name, int ttl) where T : class
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
            return new AdapterRef() { IsName = true, Ref = name, Adapter = FindOutAdapter(name) };
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
        public int gc_interval { get; set; } = 0;
        public Logging.Level log_level { get; set; } =
#if DEBUG
            Logging.Level.None;
#else
            Logging.Level.Info;
#endif

        public string dir { get; set; }

        public Dictionary<string, string> aliases { get; set; }

        public Dictionary<string, TomlTable> @in { get; set; }
        public Dictionary<string, TomlTable> @out { get; set; }
    }
}

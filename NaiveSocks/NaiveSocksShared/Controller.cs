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
        public List<InAdapter> InAdapters = new List<InAdapter>();
        public List<OutAdapter> OutAdapters = new List<OutAdapter>();

        public List<InConnection> InConnections = new List<InConnection>();

        private int _totalHandledConnections;
        public int TotalHandledConnections => _totalHandledConnections;
        public int RunningConnections => InConnections.Count;

        public Dictionary<string, Type> RegisteredInTypes = new Dictionary<string, Type>();
        public Dictionary<string, Type> RegisteredOutTypes = new Dictionary<string, Type>();
        public Dictionary<string, Func<TomlTable, InAdapter>> RegisteredInCreators = new Dictionary<string, Func<TomlTable, InAdapter>>();
        public Dictionary<string, Func<TomlTable, OutAdapter>> RegisteredOutCreators = new Dictionary<string, Func<TomlTable, OutAdapter>>();

        public Dictionary<string, string> Aliases = new Dictionary<string, string>();

        public event Action<InConnection> NewConnection;
        public event Action<InConnection> EndConnection;

        public event Action<TomlTable> ConfigTomlLoaded;

        public Func<string> FuncGetConfigString;

        public string WorkingDirectory = ".";
        public string ProcessFilePath(string input)
        {
            if (WorkingDirectory == "" || WorkingDirectory == "." || Path.IsPathRooted(input))
                return input;
            return Path.Combine(WorkingDirectory, input);
        }

        public Logging.Level LoggingLevel;

        public Controller()
        {
            RegisteredInTypes.Add("direct", typeof(DirectInAdapter));
            RegisteredInTypes.Add("socks", typeof(SocksInAdapter));
            RegisteredInTypes.Add("socks5", typeof(SocksInAdapter));
            RegisteredInTypes.Add("http", typeof(HttpInAdapter));
            RegisteredInTypes.Add("naive", typeof(NaiveInAdapter));
            RegisteredInTypes.Add("naivec", typeof(NaiveInAdapter));
            RegisteredInTypes.Add("ss", typeof(SSInAdapter));

            RegisteredOutTypes.Add("direct", typeof(DirectOutAdapter));
            RegisteredOutTypes.Add("socks", typeof(SocksOutAdapter));
            RegisteredOutTypes.Add("socks5", typeof(SocksOutAdapter));
            RegisteredOutTypes.Add("http", typeof(HttpOutAdapter));
            RegisteredOutTypes.Add("naive", typeof(NaiveMOutAdapter));
            RegisteredOutTypes.Add("naivec", typeof(NaiveMOutAdapter));
            RegisteredOutTypes.Add("ss", typeof(SSOutAdapter));

            RegisteredOutTypes.Add("router", typeof(RouterAdapter));
            RegisteredOutTypes.Add("fail", typeof(FailAdapter));
            RegisteredOutTypes.Add("nnetwork", typeof(NNetworkAdapter));
        }

        public void LoadConfigFileOrWarning(string path)
        {
            FuncGetConfigString = () => {
                if (File.Exists(path))
                    return File.ReadAllText(path, Encoding.UTF8);
                Logging.warning($"configuation file '{path}' does not exist.");
                return null;
            };
            WorkingDirectory = Path.GetDirectoryName(path);
            Load();
        }

        public void LoadConfigFileFromMultiPaths(string[] paths)
        {
            FuncGetConfigString = () => {
                foreach (var item in paths) {
                    if (File.Exists(item)) {
                        Logging.info("using configuration file: " + item);
                        WorkingDirectory = Path.GetDirectoryName(item);
                        return File.ReadAllText(item, System.Text.Encoding.UTF8);
                    }
                }
                Logging.warning("configuration file not found. searched paths:\n\t" + string.Join("\n\t", paths));
                return null;
            };
            Load();
        }

        List<ICanReloadBetter> reloading_oldAdapters;

        public void Reload()
        {
            Reload(null);
        }

        public void Reload(string configContent)
        {
            Logging.warning("================================");
            Logging.warning("stopping controller...");
            reloading_oldAdapters = new List<ICanReloadBetter>();
            this.Stop();
            this.Reset();
            Logging.warning("================================");
            Logging.warning("restarting controller...");
            Load(configContent);
            this.Start();
            reloading_oldAdapters = null;
        }

        public void Load() => Load(null);
        public void Load(string configContent)
        {
            var config = configContent ?? FuncGetConfigString?.Invoke();
            if (config != null) {
                LoadConfigStr(config);
            } else {
                Logging.warning($"no configuration.");
            }
        }

        public void LoadConfigStr(string toml)
        {
            Config t;
            TomlTable tomlTable;
            var refs = new List<AdapterRef>();
            try {
                var tomlSettings = TomlSettings.Create(cfg => cfg
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
                t = tomlTable.Get<Config>();
            } catch (Exception e) {
                Logging.error($"TOML error: " + e.Message);
                return;
            }
            ConfigTomlLoaded?.Invoke(tomlTable);
            LoggingLevel = t.log_level;
            Aliases = t.aliases;
            if (t.@in != null)
                foreach (var item in t.@in) {
                    try {
                        var tt = item.Value;
                        var adapter = NewRegisteredInType(tt);
                        adapter.Name = item.Key;
                        InAdapters.Add(adapter);
                    } catch (Exception e) {
                        Logging.exception(e, Logging.Level.Error, $"TOML table 'in.{item.Key}':");
                    }
                }
            if (t.@out != null)
                foreach (var item in t.@out) {
                    try {
                        var tt = item.Value;
                        var adapter = NewRegisteredOutType(tt);
                        adapter.Name = item.Key;
                        OutAdapters.Add(adapter);
                    } catch (Exception e) {
                        Logging.exception(e, Logging.Level.Error, $"TOML table 'out.{item.Key}':");
                    }
                }
            foreach (var r in refs) {
                if (r.IsTable) {
                    var tt = r.Ref as TomlTable;
                    var adapter = NewRegisteredOutType(tt);
                    if (tt.ContainsKey("name")) {
                        adapter.Name = tt["name"].Get<string>();
                    }
                    if (adapter.Name == null) {
                        int i = 0;
                        string name;
                        do {
                            name = $"_{tt["type"].Get<string>()}_" + (i++ == 0 ? "" : i.ToString());
                        } while (OutAdapters.Any(x => x.Name == name));
                        adapter.Name = name;
                    }
                    r.Adapter = adapter;
                    OutAdapters.Add(adapter);
                }
            }
            if (OutAdapters.Any(x => x.Name == "direct") == false) {
                OutAdapters.Add(new DirectOutAdapter() { Name = "direct" });
            }
            if (OutAdapters.Any(x => x.Name == "fail") == false) {
                OutAdapters.Add(new FailAdapter() { Name = "fail" });
            }
            foreach (var r in refs) {
                if (r.IsName) {
                    r.Adapter = FindOutAdapter(r.Ref as string);
                }
            }
        }

        private InAdapter NewRegisteredInType(TomlTable tt)
        {
            var instance = NewRegisteredType<InAdapter>(RegisteredInTypes, RegisteredInCreators, tt);
            instance.SetConfig(tt);
            return instance;
        }

        private OutAdapter NewRegisteredOutType(TomlTable tt)
        {
            var instance = NewRegisteredType<OutAdapter>(RegisteredOutTypes, RegisteredOutCreators, tt);
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

        public void Start()
        {
            void checkIcrb(IAdapter item)
            {
                if (reloading_oldAdapters != null && item is ICanReloadBetter icrb) {
                    var old = reloading_oldAdapters.Find(x => x.Name == icrb.Name && x.GetType() == icrb.GetType());
                    if (old != null) {
                        icrb.Reloading(old);
                    }
                }
            }
            foreach (var item in OutAdapters) {
                info($"OutAdapter '{item.Name}': {item}");
                try {
                    item.InternalInit(this);
                    checkIcrb(item);
                    item.Start();
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, $"starting OutAdapter '{item.Name}': {item}");
                }
            }
            foreach (var item in InAdapters) {
                info($"InAdapter '{item.Name}': {item} -> {item.@out?.Adapter?.Name?.Quoted() ?? "(No OutAdapter)"}");
                try {
                    item.InternalInit(this);
                    checkIcrb(item);
                    item.Start();
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, $"starting InAdapter '{item.Name}': {item}");
                }
            }
        }

        public void Stop()
        {
            foreach (var item in InAdapters) {
                info($"stopping InAdapter: {item}");
                if (reloading_oldAdapters != null && item is ICanReloadBetter icrb) {
                    icrb.StopForReloading();
                    reloading_oldAdapters.Add(icrb);
                }
                item.Stop();
            }
            foreach (var item in OutAdapters) {
                info($"stopping OutAdapter: '{item.Name}' {item}");
                if (reloading_oldAdapters != null && item is ICanReloadBetter icrb) {
                    icrb.StopForReloading();
                    reloading_oldAdapters.Add(icrb);
                }
                item.Stop();
            }
        }

        public void Reset()
        {
            InAdapters.Clear();
            OutAdapters.Clear();
            Aliases.Clear();
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
                    if (inc.CallbackCalled != false || !inc.IsRedirected) {
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
                        error($"{inc} does implement neither IConnectionProvider nor IConnectionHandler.");
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
            NewConnection?.Invoke(inc);
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
            if (inc.DataStream?.State != MyStreamState.Closed)
                MyStream.CloseWithTimeout(inc.DataStream);
            EndConnection?.Invoke(inc);
        }

        private async Task onConnectionException(InConnection inc, Exception e)
        {
            debug(Logging.getExceptionText(e, ""));
            if (inc.CallbackCalled == false) {
                await inc.SetConnectResult(new ConnectResult(ConnectResults.Failed) {
                    FailedReason = $"exception: {e.GetType()}: {e.Message}"
                });
            }
        }

        public IConnectionHandler FindOutAdapter(string name)
        {
            return FindAdapter<IConnectionHandler>(name, 16);
        }

        public T FindAdapter<T>(string name) where T : class
        {
            return FindAdapter<T>(name, 16);
        }

        T FindAdapter<T>(string name, int ttl) where T : class
        {
            string new_name = null;
            if (Aliases?.TryGetValue(name, out new_name) == true) {
                if (ttl == 0)
                    throw new Exception("alias loop?");
                return FindAdapter<T>(new_name, ttl - 1);
            }
            return OutAdapters.Find(a => a.Name == name) as T
                ?? InAdapters.Find(a => a.Name == name) as T;
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
                Logging.log(str, level);
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

        public Dictionary<string, string> aliases { get; set; }

        public Dictionary<string, TomlTable> @in { get; set; }
        public Dictionary<string, TomlTable> @out { get; set; }
    }
}

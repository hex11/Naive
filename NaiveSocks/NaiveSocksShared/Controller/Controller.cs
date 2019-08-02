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
    public partial class Controller : IAdapter
    {
        string IAdapter.Name => "(Controller)";
        Controller IAdapter.Controller => this;
        Adapter IAdapter.GetAdapter() => null; // throw new InvalidOperationException("No, I'm controller.");

        public LoadedConfig CurrentConfig = new LoadedConfig();

        private ConfigLoader configLoader;

        public List<Adapter> Adapters => CurrentConfig.Adapters;

        public Logger Logger { get; } = new Logger();

        public Controller()
        {
            configLoader = new ConfigLoader(this.Logger);
        }

        Logging.Level LoggingLevel => CurrentConfig.LoggingLevel;

        public Dictionary<int, InConnection> InConnections = new Dictionary<int, InConnection>();
        public object InConnectionsLock => InConnections;

        private int _totalHandledConnections, _failedConnections;
        public int TotalHandledConnections => _totalHandledConnections;
        public int TotalFailedConnections => _failedConnections;
        public int RunningConnections => InConnections.Count;

        public int StartTimes { get; private set; }
        public DateTime LastStart { get; private set; }

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
                error($"no configuration.");
            return config;
        }

        public void LoadConfigStr(string toml)
        {
            LoadConfig(ConfigFile.FromContent(toml));
        }

        public void LoadConfig(ConfigFile configFile)
        {
            SetCurrentConfig(configLoader.LoadConfig(configFile, null) ?? CurrentConfig);
            Logger.info($"configuration loaded. {Adapters.Count} Adapters.");
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
            Logging.AsyncLogging = IsDebugFlagEnabled("asynclog");
            ConfigTomlLoaded?.Invoke(loadedConfig.TomlTable);
        }

        public void Reload() => Reload(null);
        public void Reload(ConfigFile configFile)
        {
            warning("==========Reload==========");
            var newCfgFile = GetConfigFileOrLog(configFile);
            if (newCfgFile == null)
                return;
            var newCfg = configLoader.LoadConfig(newCfgFile, null);
            if (newCfg == null) {
                error("failed to load the new configuration.");
                info("still running with previous configuration.");
                return;
            }
            info($"new configuration loaded. {newCfg.Adapters.Count} Adapters.");
            var oldCfg = CurrentConfig;
            var oldCanReload = oldCfg.Adapters
                                        .Select(x => x as ICanReload)
                                        .Where(x => x != null).ToList();
            var newCanReload = newCfg.Adapters
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
            StartTimes++;
            LastStart = DateTime.UtcNow;

            int failedCount = 0;
            foreach (var item in Adapters) {
                if (item is InAdapter inadap) {
                    info($"Adapter '{item.Name}' = {item.ToString(false)} -> {inadap.@out?.ToString() ?? "(No 'out')"}");
                } else {
                    info($"Adapter '{item.Name}' = {item.ToString(false)}");
                }
                try {
                    InitAdapter(item);
                    bool callStart = true;
                    if (oldAdapters != null && item is ICanReload icr) {
                        var old = oldAdapters.Find(x => x.Name == icr.Name && x.GetType() == icr.GetType());
                        if (old != null) {
                            oldAdapters.Remove(old);
                            if (icr.Reloading(old)) callStart = false;
                        }
                    }
                    item.StartInternal(callStart);
                } catch (Exception e) {
                    Logger.exception(e, Logging.Level.Error, $"starting Adapter '{item.Name}' = {item}");
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
            Adapters.Add(adap);
            adap.SetLogger(Logger);
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
            foreach (var item in config.Adapters) {
                var reloading = item is ICanReload icr && listNotCallStop?.Contains(icr) == true;
                info($"stopping{(reloading ? " (reloading)" : "")} Adapter: '{item.Name}' {item}");
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

        public Task HandleInConnection(InConnection inConnection)
        {
            if (inConnection == null)
                throw new ArgumentNullException(nameof(inConnection));
            if (inConnection.InAdapter is IInAdapter ina) {
                return HandleInConnection(inConnection, ina.@out.Adapter as IConnectionHandler);
            } else {
                throw new ArgumentException($"InConnection.InAdapter({inConnection.InAdapter}) does not implement IInAdapter.");
            }
        }

        public Task HandleInConnection(InConnection inc, string outAdapterName)
        {
            if (string.IsNullOrEmpty(outAdapterName))
                throw new ArgumentException("outAdapterName is null or empty");
            IConnectionHandler selectedAdapter = FindOutAdapter(outAdapterName);
            if (selectedAdapter == null)
                if (LoggingLevel <= Logging.Level.Warning)
                    warning($"out adapter '{outAdapterName}' not found");
            return HandleInConnection(inc, selectedAdapter);
        }

        public Task HandleInConnection(InConnection inc, AdapterRef outAdapterRef)
        {
            if (outAdapterRef == null)
                throw new ArgumentNullException(nameof(outAdapterRef));
            IConnectionHandler adapter = outAdapterRef.Adapter as IConnectionHandler;
            if (adapter == null)
                if (LoggingLevel <= Logging.Level.Warning)
                    warning($"null out adapter reference ({outAdapterRef})");
            return HandleInConnection(inc, adapter);
        }

        public async Task HandleInConnection(InConnection inc, IConnectionHandler outAdapter)
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
                    if (outAdapter is IConnectionHandler2 ich2) {
                        await ich2.HandleConnection(inc).CAF();
                    } else if (inc is InConnectionTcp tcp) {
                        await outAdapter.HandleTcpConnection(tcp).CAF();
                    } else {
                        throw new Exception($"'{inc.InAdapter.Name}' cannot handle this type of connection.");
                    }
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

        public Task<ConnectResponse> Connect(ConnectRequest request, IAdapter outAdapter)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ConnectResult result;

            if (outAdapter == null) {
                warning($"'{request.InAdapter.Name}' {request} -> (no out adapter)");
                result = new ConnectResult(this, "no out adapter");
                return Task.FromResult(new ConnectResponse(result, request));
            }

            request.tcs = new TaskCompletionSource<ConnectResponse>();

            HandleInConnection(request, outAdapter as IConnectionHandler).Forget();

            return request.tcs.Task;
        }

        public async Task<DnsResponse> ResolveName(IAdapter creator, AdapterRef handler, DnsRequest request)
        {
            var cxn = InConnectionDns.Create(creator, request);
            await HandleInConnection(cxn, handler);
            var result = cxn.ConnectResult;
            if (result?.Ok == false) {
                if (result.FailedReason != null)
                    throw new Exception(result.FailedReason);
                throw new Exception("name resolving failed.");
            }
            return result as DnsResponse ?? DnsResponse.Empty(this);
        }

        private void onConnectionBegin(InConnection inc, IAdapter outAdapter)
        {
            if (LoggingLevel <= Logging.Level.None)
                debug($"'{inc.InAdapter.Name}' {inc} -> '{outAdapter.Name}'");
            try {
                lock (InConnectionsLock) {
                    inc.InAdapter.GetAdapter().CreatedConnections++;
                    _totalHandledConnections++;
                    InConnections.Add(inc.Id, inc);
                    NewConnection?.Invoke(inc);
                }
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "event NewConnection");
            }
        }

        internal async Task onConnectionEnd(InConnection inc)
        {
            inc.RunningHandler = null;
            if (LoggingLevel <= Logging.Level.None)
                debug($"{inc} End.");
            await inc.Finish();
            try {
                lock (InConnectionsLock) {
                    if (inc.ConnectResult?.Result != ConnectResultEnum.OK)
                        _failedConnections++;
                    InConnections.Remove(inc.Id);
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
                await inc.SetResult(new ConnectResultBase(this, ConnectResultEnum.Failed) {
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
            foreach (var item in cfg.Adapters) {
                if (item.Name == name) return item as T;
            }
            var name2 = "out." + name;
            foreach (var item in cfg.Adapters) {
                if (item.Name == name2) return item as T;
            }
            name2 = "in." + name;
            foreach (var item in cfg.Adapters) {
                if (item.Name == name2) return item as T;
            }
            return null;
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
}

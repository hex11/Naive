using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nett;
using Naive.HttpSvr;
using System.Text;
using System.Reflection;
using System.Linq;
using System.Collections;
using System.Collections.Generic;

namespace NaiveSocks
{
    public interface IAdapter
    {
        string Name { get; }
        Controller Controller { get; }
        Adapter GetAdapter();
    }

    public class AdapterRef
    {
        public IAdapter Adapter { get; internal set; }
        public bool IsName;
        public bool IsTable;
        internal object Ref { get; set; }

        public override string ToString()
        {
            if (Adapter != null)
                return IsName ? $"ref'{Adapter.Name}'" : $"inline'{Adapter.Name}'";
            else
                return $"ref'{Ref}'(Not Found)";
        }

        public static AdapterRef FromAdapter(IAdapter adapter)
        {
            return new AdapterRef() { Adapter = adapter };
        }
    }

    public struct AdapterRefOrArray
    {
        public object obj;
        public AdapterRef AsAdapterRef => obj as AdapterRef;
        public AdapterRef[] AsArray => obj as AdapterRef[];
        public bool IsNull => obj == null;

        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        public struct Enumerator : IEnumerator<AdapterRef>
        {
            internal Enumerator(AdapterRefOrArray soa)
            {
                _soa = soa;
                i = 0;
                size = soa.AsArray?.Length ?? (soa.AsAdapterRef != null ? 1 : 0);
                Current = null;
            }

            private readonly AdapterRefOrArray _soa;
            int i;
            int size;

            public AdapterRef Current { get; private set; }

            object IEnumerator.Current => this.Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (i >= size)
                    return false;
                Current = _soa.AsArray != null ? _soa.AsArray[i] : _soa.AsAdapterRef;
                i++;
                return true;
            }

            public void Reset()
            {
                i = 0;
                Current = null;
            }
        }
    }

    public struct StringOrArray : IEnumerable<string>
    {
        public StringOrArray(string str)
        {
            obj = str;
        }

        public StringOrArray(string[] arr)
        {
            obj = arr;
        }

        public object obj;
        public string AsString => obj as string;
        public string[] AsArray => obj as string[];
        public bool IsNull => obj == null;

        public bool IsOrContains(string str) => (AsString != null && str == AsString) || AsArray?.Contains(str) == true;

        public bool TrySplit(char seperator, bool trim)
        {
            var str = AsString;
            if (str == null) return false;
            if (str.Contains(seperator) == false) return false;
            var arr = str.Split(new[] { seperator }, StringSplitOptions.None);
            if (trim) {
                for (int i = 0; i < arr.Length; i++) {
                    arr[i] = arr[i].Trim();
                }
            }
            obj = arr;
            return true;
        }

        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        IEnumerator<string> IEnumerable<string>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<string>
        {
            internal Enumerator(StringOrArray soa)
            {
                _soa = soa;
                i = 0;
                size = soa.AsArray?.Length ?? (soa.AsString != null ? 1 : 0);
                Current = null;
            }

            private readonly StringOrArray _soa;
            int i;
            int size;

            public string Current { get; private set; }

            object IEnumerator.Current => this.Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (i >= size)
                    return false;
                Current = _soa.AsArray != null ? _soa.AsArray[i] : _soa.AsString;
                i++;
                return true;
            }

            public void Reset()
            {
                i = 0;
                Current = null;
            }
        }
    }

    public interface ICanReload : IAdapter
    {
        /// <summary>
        /// Called by the controller when it's reloading and an old instance is found.
        /// Returns false if the adapter wants the Start() method being called later.
        /// </summary>
        bool Reloading(object oldInstance);
    }

    [System.AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
    sealed class NotConfAttribute : Attribute
    {
    }

    [System.AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
    sealed class ConfTypeAttribute : Attribute
    {
    }

    public abstract class Adapter : IAdapter
    {
        [NotConf]
        public string Name { get; set; }

        public Adapter GetAdapter() => this;

        public Controller Controller { get; private set; }
        public Logger Logger { get; } = new Logger();

        public string socket_impl { get; set; }

        public IMyStream GetMyStreamFromSocket(Socket socket) => MyStream.FromSocket(socket, socket_impl);

        public int CreatedConnections;
        public int HandledConnections;

        public BytesCountersRW BytesCountersRW { get; } = new BytesCountersRW {
            R = new BytesCounter(),
            W = new BytesCounter(MyStream.GlobalWriteCounter)
        };

        public string flags { get; set; }
        protected virtual bool AutoFlags => true;

        public string QuotedName
        {
            get {
                return Name.Quoted("'");
            }
        }

        public bool Inited { get; private set; }
        public bool IsRunning { get; private set; }

        protected virtual void OnStart()
        {
        }

        public void Start()
        {
            StartInternal(true);
        }

        internal void StartInternal(bool callStart)
        {
            if (IsRunning) {
                Logger.warning("Start() when is already running.");
                return;
            }
            IsRunning = true;
            try {
                if (callStart)
                    OnStart();
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "OnStart()");
            }
        }

        protected virtual void OnStop()
        {
        }

        public void Stop(bool callStop)
        {
            StopInternal(true);
        }

        internal void StopInternal(bool callStop)
        {
            if (!IsRunning) {
                Logger.warning("Stop() when is not running.");
                return;
            }
            IsRunning = false;
            try {
                if (callStop)
                    OnStop();
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "OnStop()");
            }
        }

        protected virtual void OnInit()
        {
        }

        internal void Init(Controller controller)
        {
            if (Inited) {
                Logger.warning("Init() when already inited.");
                return;
            }
            this.Controller = controller;
            Inited = true;
            try {
                OnInit();
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "OnInit()");
            }
        }

        public virtual void SetConfig(TomlTable toml)
        {
            if (AutoFlags && !flags.IsNullOrEmpty()) {
                var flgs = flags.Split(' ');
                foreach (var item in flgs) {
                    var flg = item;
                    bool val = true;
                    if (flg.StartsWith("!")) {
                        val = false;
                        flg = flg.Substring(1);
                    }
                    var prop = this.GetType().GetProperty(flg, BindingFlags.Instance | BindingFlags.Public);
                    if (prop == null) {
                        Logger.warning($"flags: can not find public property '{flg}'.");
                        continue;
                    }
                    if (prop.PropertyType != typeof(bool)) {
                        Logger.warning($"flags: the type of property '{flg}' is not bool.");
                        continue;
                    }
                    if (prop.CanWrite == false) {
                        Logger.warning($"flags: the property '{flg}' is not writable.");
                        continue;
                    }
                    //Logger.warning($"flags: {flg}={val}");
                    prop.SetValue(this, val);
                }
            }
        }

        internal void SetLogger(Logger parent)
        {
            Logger.ParentLogger = parent;
            Logger.Stamp = Name;
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToString(bool withName)
        {
            var sb = new StringBuilder();
            ToString(sb, withName);
            return sb.ToString();
        }

        public void ToString(StringBuilder sb, bool withName = true)
        {
            sb.Append('{');
            if (withName) {
                sb.Append('\'').Append(Name).Append('\'');
                sb.Append(' ');
            }
            sb.Append(TypeName);
            GetDetailString(sb);
            sb.Append('}');
        }

        public virtual string TypeName
        {
            get {
                var name = this.GetType().Name;
                if (name.EndsWith("Adapter")) {
                    return name.Substring(0, name.Length - "Adapter".Length);
                }
                return name;
            }
        }

        public void GetDetailString(StringBuilder sb)
        {
            GetDetail(new GetDetailContext(sb));
        }

        protected virtual void GetDetail(GetDetailContext ctx)
        {
        }

        protected struct GetDetailContext
        {
            public GetDetailContext(StringBuilder stringBuilder)
            {
                StringBuilder = stringBuilder;
            }

            public StringBuilder StringBuilder { get; set; }

            public void AddField<T>(string name, T value)
            {
                StringBuilder.Append(' ').Append(name).Append('=').Append(value?.ToString());
            }

            public void AddTag(string tag)
            {
                StringBuilder.Append(' ').Append(tag);
            }
        }

        public ConnectResult CreateConnectResultWithStream(IMyStream stream)
        {
            return new ConnectResult(this, stream);
        }

        public ConnectResult CreateConnectResultWithFailed()
        {
            return new ConnectResult(this, ConnectResultEnum.Failed);
        }
    }

    public interface IInAdapter : IAdapter
    {
        AdapterRef @out { get; set; }
    }

    public abstract class InAdapter : Adapter, IInAdapter
    {
        public AdapterRef @out { get; set; }

        protected Task HandleIncommingConnection(InConnection inConnection)
        {
            return Controller.HandleInConnection(inConnection, @out.Adapter as IConnectionHandler);
        }
    }

    public abstract class InAdapterWithListener : InAdapterWithListenField
    {
        private Listener listener;

        protected override void OnStart()
        {
            base.OnStart();
            if (listen == null) {
                Logger.warning("No 'listen'!");
                return;
            }
            listener = new Listener(this, listen);
            listener.Accepted += OnNewConnection;
            listener.Run().Forget();
        }

        protected override void OnStop()
        {
            base.OnStop();
            listener?.Stop();
        }

        public abstract void OnNewConnection(TcpClient client);
    }

    public abstract class InAdapterWithListenField : InAdapter
    {
        public IPEndPoint listen { get; set; }

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("listen", listen);
        }

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            listen = toml.TryGetValue<IPEndPoint>("local", listen);
        }
    }
}

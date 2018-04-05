using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nett;
using Naive.HttpSvr;
using System.Text;
using System.Reflection;

namespace NaiveSocks
{
    public interface IAdapter
    {
        string Name { get; }
        Controller Controller { get; }
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
    }

    public interface ICanReload : IAdapter
    {
        /// <summary>
        /// Called by the controller when it's reloading and an old instance is found.
        /// Returns false if the adapter wants the Start() method being called later.
        /// </summary>
        bool Reloading(object oldInstance);
    }

    public abstract class Adapter : IAdapter
    {
        public string Name { get; set; }

        public Controller Controller { get; private set; }
        public Logger Logger { get; } = new Logger();

        public string flags { get; set; }

        public string QuotedName
        {
            get {
                return Name.Quoted("'");
            }
        }

        public bool IsRunning { get; set; }

        public virtual void Start()
        {
        }

        internal void InternalStart(bool callStart)
        {
            IsRunning = true;
            if (callStart)
                Start();
        }

        public virtual void Stop()
        {
        }

        internal void InternalStop(bool callStop)
        {
            IsRunning = false;
            if (callStop)
                Stop();
        }

        internal void InternalInit(Controller controller)
        {
            this.Controller = controller;
            Init();
        }

        public virtual void SetConfig(TomlTable toml)
        {
            if (!flags.IsNullOrEmpty()) {
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

        protected virtual void Init() { }

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
                StringBuilder.Append(' ').Append(name).Append('=').Append(value.ToString());
            }

            public void AddTag(string tag)
            {
                StringBuilder.Append(' ').Append(tag);
            }
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

        public override void Start()
        {
            listener = new Listener(listen);
            listener.Accepted += OnNewConnection;
            listener.Run().Forget();
        }

        public override void Stop()
        {
            listener.Stop();
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

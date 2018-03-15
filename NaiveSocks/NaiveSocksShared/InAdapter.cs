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
                return IsName ? $"ref'{Adapter.Name}'" : $"new'{Adapter.Name}'";
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
            if (Name != null)
                return Name.Quoted();
            return base.ToString();
        }

        public virtual void GetDetailString(StringBuilder sb)
        {

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

        public override string ToString() => $"{{{AdapterType} listen={listen}}}";

        public virtual string AdapterType => GetType().Name;

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            listen = toml.TryGetValue<IPEndPoint>("local", listen);
        }
    }
}

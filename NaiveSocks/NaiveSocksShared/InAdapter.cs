using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nett;
using Naive.HttpSvr;

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
                return $"ref'{Adapter.Name}'";
            else
                return $"ref'{Ref}'(NoAdapter)";
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
        }

        protected virtual void Init() { }

        public override string ToString()
        {
            if (Name != null)
                return Name.Quoted();
            return base.ToString();
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
            listener.Start().Forget();
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

        public override string ToString() => $"{{{GetType().Name} listen={listen}}}";

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            listen = toml.TryGetValue<IPEndPoint>("local", listen);
        }
    }
}

using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{



    public class NaiveInAdapter : NaiveProtocol.NaiveMServerBase, IHttpRequestAsyncHandler
    {
        private NaiveWebsiteServer httpServer;
        public IPEndPoint local { get; set; }
        public string path { get; set; } = "/";
        private const string DefaultKey = "hello, world";
        public string key { get; set; } = DefaultKey;
        public Dictionary<string, string> paths { get; set; }

        public Dictionary<string, AdapterRef> networks { get; set; }
        public AdapterRef network { get; set; }

        protected override INetwork GetNetwork(string name)
        {
            if (networks.TryGetValue(name, out var n))
                return n.Adapter as INetwork;
            return null;
        }

        public override void Start()
        {
            base.Start();
            if (networks == null)
                networks = new Dictionary<string, AdapterRef>();
            if (network != null)
                networks.Add("default", network);
            httpServer = new NaiveWebsiteServer();
            if (local != null)
                httpServer.AddListener(local);
            if (paths == null) {
                if (key == DefaultKey) {
                    Logging.warning($"{this} is using default key: '{DefaultKey}'");
                }
                addPath(path, key);
            } else {
                foreach (var item in paths) {
                    addPath(item.Key, item.Value);
                }
            }
            httpServer.Run();
        }

        private void addPath(string path, string key)
        {
            var realKey = NaiveProtocol.GetRealKeyFromString(key);
            httpServer.Router.AddAsyncRoute(path, (p) => this.HandleRequestAsync(p, realKey));
        }

        public override void Stop()
        {
            httpServer.Stop();
            base.Stop();
        }

        public Task HandleRequestAsync(HttpConnection p)
        {
            return httpServer.Router.HandleRequestAsync(p);
        }
    }
}

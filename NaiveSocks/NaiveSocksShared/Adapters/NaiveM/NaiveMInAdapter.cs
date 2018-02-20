using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Naive.HttpSvr;
using Nett;

namespace NaiveSocks
{

    public class NaiveMInAdapter : NaiveProtocol.NaiveMServerBase, IHttpRequestAsyncHandler
    {
        private NaiveWebsiteServer httpServer;
        public IPEndPoint listen { get; set; }

        public Dictionary<string, PathSettings> path_settings { get; set; }

        public class PathSettings : Settings
        {
            public string format { get; set; } = @"token=(?<token>[\w%]*)";
            public string key { get; set; }
            public Dictionary<string, AdapterRef> networks { get; set; }
            public AdapterRef network { get; set; }

            public override INetwork GetNetwork(string str)
            {
                if (networks == null)
                    return null;
                networks.TryGetValue(str, out var aref);
                return aref.Adapter as INetwork;
            }
        }

        public string path { get; set; } = "/";
        private const string DefaultKey = "hello, world";
        public string key { get; set; } = DefaultKey;

        // map path to encryption key
        public Dictionary<string, string> paths { get; set; }

        public int imux_max { get; set; } = 16;

        // map network name to INetwork
        public Dictionary<string, AdapterRef> networks { get; set; }

        // 'default' network
        public AdapterRef network { get; set; }

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            listen = toml.TryGetValue("local", listen);
        }

        public override void Start()
        {
            base.Start();
            networks = networks ?? new Dictionary<string, AdapterRef>();
            if (network != null)
                networks.Add("default", network);
            httpServer = new NaiveWebsiteServer();
            path_settings = path_settings ?? new Dictionary<string, PathSettings>();
            if (paths != null) {
                foreach (var item in paths) {
                    path_settings.Add(item.Key, new PathSettings {
                        networks = this.networks,
                        key = item.Value
                    });
                }
            }
            if (path_settings.Count == 0) {
                path_settings = new Dictionary<string, PathSettings>();
                if (key == DefaultKey) {
                    Logging.warning($"{this} is using default key: '{DefaultKey}'");
                }
                path_settings.Add(path, new PathSettings {
                    networks = this.networks
                });
            }
            foreach (var item in path_settings) {
                addPath(item.Key, item.Value);
            }
            if (listen != null) {
                httpServer.AddListener(listen);
                httpServer.Run();
            } else {
                Logging.warning($"{this}: no listener!");
            }
        }

        private void addPath(string path, PathSettings settings)
        {
            if (settings.imux_max < 0)
                settings.imux_max = imux_max;
            if (settings.network != null) {
                settings.networks = settings.networks ?? new Dictionary<string, AdapterRef>();
                settings.networks.Add("default", settings.network);
            }
            settings.realKey = NaiveProtocol.GetRealKeyFromString(settings.key ?? this.key, 32);
            httpServer.Router.AddAsyncRoute(path, (p) => {
                var token = Regex.Match(p.Url_qstr, settings.format).Groups["token"].Value;
                token = HttpUtil.UrlDecode(token);
                return this.HandleRequestAsync(p, settings, token);
            });
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

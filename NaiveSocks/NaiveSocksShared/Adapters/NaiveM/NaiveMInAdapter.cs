﻿using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Naive.HttpSvr;
using Nett;

namespace NaiveSocks
{

    public class NaiveMInAdapter : NaiveMServerBase, IHttpRequestAsyncHandler
    {
        private NaiveWebsiteServer httpServer;
        public IPEndPoint listen { get; set; }

        public Dictionary<string, PathSettings> path_settings { get; set; }

        [ConfType]
        public class PathSettings : Settings
        {
            public string format { get; set; } = @"(token=)?(?<token>[\w%]+)";
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
        public string key { get; set; }

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

        protected override void OnStart()
        {
            base.OnStart();

            httpServer = new NaiveWebsiteServer();
            httpServer.Router.AutoSetHandled = false;
            httpServer.Router.AutoSetResponseCode = false;

            networks = networks ?? new Dictionary<string, AdapterRef>();
            if (network != null)
                networks.Add("default", network);
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
                if (key == null) {
                    Logger.error("key is not specified");
                    return;
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
                Logger.warning("no listener!");
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
                var m = Regex.Match(p.Url_qstr, settings.format);
                if (m.Success == false)
                    return NaiveUtils.CompletedTask;
                var token = m.Groups["token"].Value;
                token = HttpUtil.UrlDecode(token);
                return this.HandleRequestAsync(p, settings, token);
            });
        }

        protected override void OnStop()
        {
            httpServer.Stop();
            base.OnStop();
        }

        public Task HandleRequestAsync(HttpConnection p)
        {
            return httpServer.Router.HandleRequestAsync(p);
        }
    }
}

using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace NaiveSocks
{
    public interface INetwork
    {
        void AddClient(NClient client);
        string Domain { get; }
    }

    public class NClient
    {
        static IncrNumberGenerator idgen = new IncrNumberGenerator();
        public Task WhenDisconnected { get; set; }
        //public TaskCompletionSource<object> taskCompletionSource = new TaskCompletionSource<object>();
        //public void OnDisconneted()
        //{
        //    taskCompletionSource.SetResult(null);
        //}

        public List<string> tags { get; }

        public IPAddress Ip { get; set; }

        // Websocket time
        public int CreateTime;

        public Func<InConnection, Task> HandleConnection;

        public NClient(params string[] tags)
        {
            this.tags = tags.ToList();
            this.tags.Insert(0, $"id{idgen.Get()}");
            CreateTime = WebSocket.CurrentTime;
        }

        //public abstract Task HandleConnection(InConnection connection)
        public override string ToString() => $"{{NClient tags={string.Join(",", tags)} ip={Ip} {(WhenDisconnected.IsCompleted ? " disconnected" : "")}}}";
    }

    public class NNetworkAdapter : WebBaseAdapter, INetwork, IDnsProvider
    {
        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("domain", domain);
        }

        public string domain { get; set; }
        public string list_name { get; set; } = "list";

        public AdapterRef if_notfound { get; set; }
        public AdapterRef if_notmatch { get; set; }

        public bool enable_ip { get; set; } = true;

        public string Domain => domain;

        List<NClient> clients = new List<NClient>();

        Dictionary<uint, NClient> ipmap = new Dictionary<uint, NClient>();

        uint nextIp;
        uint listIp;

        private uint AllocateIp()
        {
            var r = nextIp;
            nextIp = SwapEndian(SwapEndian(r) + 1);
            return r;
        }

        static uint SwapEndian(uint i)
        {
            return (i & 0x000000ff) << 24
                | (i & 0x0000ff00) << 8
                | (i & 0x00ff0000) >> 8
                | (i & 0xff000000) >> 24;
        }

        protected override void OnInit()
        {
            base.OnInit();
            nextIp = (uint)IPAddress.Parse("10.23." + NaiveUtils.Random.Next(0, 255) + ".1").Address;
            listIp = AllocateIp();
            domain = domain.TrimStart('.');
        }

        public override bool Reloading(object oldInstance)
        {
            base.Reloading(oldInstance);
            var old = oldInstance as NNetworkAdapter;
            clients = old.clients;
            ipmap = old.ipmap;
            nextIp = old.nextIp;
            listIp = old.listIp;
            Logger.info($"reloading with {clients.Count} client(s).");
            return false;
        }

        static Random rd => NaiveUtils.Random;

        public override async Task HandleConnection(InConnection connection)
        {
            var host = connection.Dest.Host;
            var name = TryGetName(host);
            if (name != null) {
                if (name.Length == 0 || name == list_name) {
                    if (connection.Dest.Port == 80) {
                        goto LIST;
                    }
                } else {
                    var cli = FindClientByName(name);
                    if (cli != null) {
                        await cli.HandleConnection(connection);
                    } else if (if_notfound == null) {
                        await connection.HandleAndGetStream(new ConnectResult(this, ConnectResultEnum.Failed) {
                            FailedReason = "no such client"
                        });
                    } else {
                        connection.RedirectTo(if_notfound);
                    }
                }
            } else {
                if (enable_ip && AddrPort.TryParseIpv4(host, out var ip)) {
                    if (ipmap.TryGetValue(ip, out var cli)) {
                        await cli.HandleConnection(connection);
                        return;
                    } else if (ip == listIp) {
                        goto LIST;
                    }
                }
                if (if_notmatch != null) {
                    connection.RedirectTo(if_notmatch);
                }
            }
            return;

            LIST:
            var stream = await connection.HandleAndGetStream(this);
            var httpConnection = CreateHttpConnectionFromMyStream(stream, HttpSvr);
            httpConnection.SetTag("connection", connection);
            await httpConnection.Process();
        }

        public Task<DnsResponse> ResolveName(DnsRequest req)
        {
            //throw new NotImplementedException();
            var name = TryGetName(req.Name);
            if (name == null)
                return (if_notmatch.Adapter as IDnsProvider).ResolveName(req);
            if (name.Length == 0 || name == list_name)
                return Task.FromResult(new DnsResponse(new IPAddress((long)listIp)));
            var clis = FindClientsByName(name);
            if (clis.Count > 0) {
                return Task.FromResult(new DnsResponse(clis.Select(x => x.Ip).ToArray()));
            } else {
                if (if_notfound == null)
                    return Task.FromResult(DnsResponse.Empty);
                return (if_notfound.Adapter as IDnsProvider).ResolveName(req);
            }
        }

        private string TryGetName(string host)
        {
            if (host.EndsWith(domain) && (host == domain || host[host.Length - domain.Length - 1] == '.')) {
                if (host == domain) {
                    return "";
                } else {
                    return host.Substring(0, host.Length - domain.Length - 1);
                }
            }
            return null;
        }

        private List<NClient> FindClientsByName(string name)
        {
            List<NClient> clis = null;
            lock (clients)
                clis = clients.FindAll(x => x.tags.Contains(name));
            return clis;
        }

        private NClient FindClientByName(string name)
        {
            var clis = FindClientsByName(name);
            if (clis.Count == 0) return null;
            return clis[NaiveUtils.Random.Next(clis.Count)];
        }

        public void AddClient(NClient client)
        {
            lock (clients) {
                clients.Add(client);
                if (enable_ip) {
                    var ip = AllocateIp();
                    client.Ip = new IPAddress((long)ip);
                    ipmap.Add(ip, client);
                }
            }
            Logger.info($"added: {client}");
            client.WhenDisconnected.GetAwaiter().OnCompleted(() => RemoveClient(client));
        }

        private void RemoveClient(NClient client)
        {
            bool success;
            lock (clients) {
                success = clients.Remove(client);
                if (success && enable_ip) ipmap.Remove((uint)client.Ip.Address);
            }
            if (success) {
                Logger.info($"removed: {client}");
            } else {
                Logger.warning($"tried to remove a client that not found: {client}");
            }
        }

        public override Task HandleRequestAsyncImpl(HttpConnection p)
        {
            if (p.Url_path == "/" && p.Method == "GET") {
                var con = p.GetTag("connection") as InConnection;
                return ClientList(p, con);
            } else {
                p.setStatusCode("404 Not Found");
                return p.EndResponseAsync("<h1>Client not found</h1>");
            }
        }

        public async Task ClientList(HttpConnection p, InConnection con)
        {
            p.setStatusCode("200 OK");
            var sb = new StringBuilder(512);
            sb.AppendLine("<html><head><meta name='viewport' content='width=device-width, initial-scale=1'></head>")
              .AppendLine($"<body style='margin: 0 auto; max-width: 100ch; padding: 8px;'>" +
                            $"<h1 style='text-align: center'>NNetwork '{domain}'</h1>");
            sb.AppendLine("<h2>Connected Clients</h2><pre style='overflow: auto;'>");
            var curTime = WebSocket.CurrentTime;
            foreach (var item in clients) {
                sb.Append(item.Ip.ToString());
                foreach (var name in item.tags) {
                    sb.Append('\t').Append(name);
                }
                sb.Append("\tCreateTime: ").Append(item.CreateTime - curTime).AppendLine();
            }
            sb.AppendLine()
              .AppendLine("</pre><h2>Request Info</h2><pre style='overflow: auto;'>")
              .AppendLine($"Url: {p.Url}")
              .AppendLine($"Host: {p.Host}")
              .AppendLine($"BaseStream: {p.myStream}")
              .AppendLine($"EndPoint: {p.epPair}")
              .AppendLine($"Server Time: {DateTime.Now}");
            if (con != null) {
                sb.AppendLine($"Dest: {con.Dest}");
            }
            sb.AppendLine()
              .AppendLine("RawRequest:")
              .Append(HttpUtil.HtmlEncode(p.RawRequest))
              .AppendLine("</pre></body></html>");
            await p.EndResponseAsync(sb.ToString());
        }
    }
}

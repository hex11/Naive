using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Linq;

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

        public List<string> names { get; }

        // Websocket time
        public int CreateTime;

        public Func<InConnection, Task> HandleConnection;

        public NClient(params string[] name)
        {
            this.names = name.ToList();
            names.Insert(0, $"id{idgen.Get()}");
            CreateTime = WebSocket.CurrentTime;
        }

        //public abstract Task HandleConnection(InConnection connection)
        public override string ToString() => $"{{NClient name={string.Join(",", names)}{(WhenDisconnected.IsCompleted ? " disconnected" : "")}}}";
    }

    public class NNetworkAdapter : WebBaseAdapter, ICanReload, INetwork, IHttpRequestAsyncHandler
    {
        public override string ToString() => $"{{NNetwork '{domain}'}}";

        public string domain { get; set; }
        public AdapterRef if_notfound { get; set; }
        public AdapterRef if_notmatch { get; set; }

        public string Domain => domain;

        List<NClient> clients = new List<NClient>();

        protected override void Init()
        {
            base.Init();
            domain = domain.TrimStart('.');
        }

        public bool Reloading(object oldInstance)
        {
            clients = (oldInstance as NNetworkAdapter).clients;
            Logger.info($"reloading with {clients.Count} client(s).");
            return false;
        }

        static Random rd = new Random();

        public override async Task HandleConnection(InConnection connection)
        {
            var host = connection.Dest.Host;
            if (host.EndsWith(domain) && (host == domain || host[host.Length - domain.Length - 1] == '.')) {
                if (host == domain) {
                    if (connection.Dest.Port == 80) {
                        await connection.SetConnectResult(ConnectResults.Conneceted);
                        var httpConnection = CreateHttpConnectionFromMyStream(connection.DataStream, HttpSvr);
                        httpConnection.SetTag("connection", connection);
                        await httpConnection.Process();
                    }
                } else {
                    List<NClient> clis = null;
                    var name = host.Substring(0, host.Length - domain.Length - 1);
                    lock (clients)
                        clis = clients.FindAll(x => x.names.Contains(name));
                    if (clis.Count > 0) {
                        var cli = clis[rd.Next(clis.Count)];
                        await cli.HandleConnection(connection);
                    } else if (if_notfound == null) {
                        if (connection.Dest.Port == 80) {
                            await connection.SetConnectResult(ConnectResults.Conneceted);
                            var httpConnection = CreateHttpConnectionFromMyStream(connection.DataStream, HttpSvr);
                            httpConnection.SetTag("name", name);
                            httpConnection.SetTag("connection", connection);
                            await httpConnection.Process();
                        } else {
                            await connection.SetConnectResult(new ConnectResult(ConnectResults.Failed) {
                                FailedReason = "no such client"
                            });
                        }
                    } else {
                        connection.RedirectTo(if_notfound);
                    }
                }
            } else if (if_notmatch != null) {
                connection.RedirectTo(if_notmatch);
            }
        }

        public void AddClient(NClient client)
        {
            lock (clients)
                clients.Add(client);
            Logger.info($"added: {client}");
            NaiveUtils.RunAsyncTask(async () => {
                try {
                    await client.WhenDisconnected;
                } finally {
                    lock (clients)
                        clients.Remove(client);
                    Logger.info($"removed: {client}");
                }
            });
        }

        public override async Task HandleRequestAsync(HttpConnection p)
        {
            if (p.Url_path == "/" && p.Method == "GET") {
                var con = p.GetTag("connection") as InConnection;
                await ClientList(p, con);
            } else {
                p.setStatusCode("404 Not Found");
                await p.writeLineAsync("<h1>Client not found</h1>");
            }
            await p.EndResponseAsync();
        }

        public async Task ClientList(HttpConnection p, InConnection con)
        {
            p.setStatusCode("200 OK");
            var sb = new StringBuilder(512);
            sb.AppendLine("<html><head><meta name='viewport' content='width=device-width, initial-scale=1'></head>")
              .AppendLine($"<body style='margin: 0 auto; max-width: 100ch; padding: 8px;'>" +
                            $"<h1 style='text-align: center'>NNetwork '{domain}'</h1>");
            sb.AppendLine("<h2>Connected Clients</h2><pre style='overflow: auto;'>");
            foreach (var item in clients) {
                sb.AppendLine($"{string.Join("\t", item.names)}\tCreateTime: {item.CreateTime - WebSocket.CurrentTime}");
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
            await p.writeAsync(sb.ToString());
        }
    }
}

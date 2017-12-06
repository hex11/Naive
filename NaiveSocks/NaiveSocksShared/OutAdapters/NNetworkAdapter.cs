﻿using System.Net;
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
        public override string ToString() => $"{{NClient name={string.Join(",", names)} disconnected={WhenDisconnected.IsCompleted}}}";
    }

    public class NNetworkAdapter : OutAdapter, ICanReloadBetter, INetwork
    {
        public override string ToString() => $"{{NNetwork '{domain}'}}";

        public string domain { get; set; }
        public AdapterRef if_notfound { get; set; }
        public AdapterRef if_notmatch { get; set; }

        public string Domain => domain;

        List<NClient> clients = new List<NClient>();

        HttpSvr httpsvr;

        protected override void Init()
        {
            base.Init();
            domain = domain.TrimStart('.');
            httpsvr = new HttpSvr(this);
        }

        public void Reloading(object oldInstance)
        {
            clients = (oldInstance as NNetworkAdapter).clients;
            Logging.info($"{this} reload with {clients.Count} client(s).");
        }

        public void StopForReloading()
        {
        }

        static Random rd = new Random();

        public override async Task HandleConnection(InConnection connection)
        {
            var host = connection.Dest.Host;
            if (host.EndsWith(domain) && (host == domain || host[host.Length - domain.Length - 1] == '.')) {
                if (host == domain) {
                    if (connection.Dest.Port == 80) {
                        await connection.SetConnectResult(ConnectResults.Conneceted);
                        var httpConnection = new HttpConnection(MyStream.ToStream(connection.DataStream), new EPPair(new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2)), httpsvr);
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
                            var httpConnection = new HttpConnection(MyStream.ToStream(connection.DataStream), new EPPair(new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2)), httpsvr);
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
            Logging.info($"{this} added: {client}");
            Task.WhenAny(client.WhenDisconnected).ContinueWith((x) => {
                lock (clients)
                    clients.Remove(client);
                Logging.info($"{this} removed: {client}");
            });
        }

        class HttpSvr : NaiveHttpServerAsync
        {
            public HttpSvr(NNetworkAdapter adapter)
            {
                Adapter = adapter;
            }

            public NNetworkAdapter Adapter { get; }

            public override async Task HandleRequestAsync(HttpConnection p)
            {
                if (p.Url_path == "/" && p.Method == "GET") {
                    var con = p.GetTag("connection") as InConnection;
                    p.setStatusCode("200 OK");
                    var sb = new StringBuilder(512);
                    sb.AppendLine("<html><head><meta name='viewport' content='width=device-width, initial-scale=1'></head>")
                      .AppendLine($"<body><h1>Test Page of {Adapter.domain}</h1><pre style='overflow: auto'>")
                      .AppendLine($"Url: {p.Url}")
                      .AppendLine($"Host: {p.Host}")
                      .AppendLine($"Server Time: {DateTime.Now}");
                    if (con != null) {
                        sb.AppendLine($"Dest: {con.Dest}");
                    }
                    sb.AppendLine("Clients:");
                    foreach (var item in Adapter.clients) {
                        sb.AppendLine($"{string.Join("\t", item.names)}\tCreateTime: {item.CreateTime - WebSocket.CurrentTime}");
                    }
                    sb.AppendLine();
                    sb.AppendLine("RawRequest:");
                    sb.Append(HttpUtil.HtmlEncode(p.RawRequest));
                    sb.AppendLine("</pre></body></html>");
                    await p.writeAsync(sb.ToString());
                } else {
                    p.setStatusCode("404 Not Found");
                    await p.writeLineAsync("<h1>Client not found</h1>");
                }
                await p.EndResponseAsync();
            }
        }
    }
}

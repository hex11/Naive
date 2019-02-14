using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks
{
    public class DnsInAdapter : InAdapterWithListenField
    {
        UdpClient udpClient;
        IDnsProvider dnsProvider;

        public bool verbose { get; set; } = false;
        public int ttl { get; set; } = 30;
        public string cache { get; set; } = "ram"; // none, ram, db
        public string cache_path { get; set; } = null;
        public int cache_ttl { get; set; } = 120;

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField(nameof(cache), cache);
        }

        public ICacheDns cacheDns;

        protected override void OnStart()
        {
            base.OnStart();
            if (listen == null) {
                Logger.error("No 'listen'!");
                return;
            }
            if (@out?.Adapter is IDnsProvider provider) {
                dnsProvider = provider;
            } else {
                Logger.error("'out' is not a valid DnsProvider.");
                return;
            }
            if (cache == "none") {
                cacheDns = null;
            } else if (cache == "ram") {
                cacheDns = new SimpleCacheDns();
            } else if (cache == "db") {
                if (cache_path == null) {
                    Logger.error("'cache_path' is not specified.");
                    return;
                }
                var db = new DnsDb(Controller.ProcessFilePath(cache_path));
                db.Logger = new Logger("db", Logger);
                db.Init();
                cacheDns = db;
            } else {
                Logger.error("'cache' should be one of: none, ram(default), db");
                return;
            }

            UDPListen();
        }

        protected override void OnStop()
        {
            base.OnStop();
            udpClient?.Close();
            udpClient = null;
        }

        public void HandleRdns(InConnection x)
        {
            if (IPAddress.TryParse(x.Dest.Host, out var ip)) {

                string host = null;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    host = cacheDns.QueryByIp((uint)ip.Address);
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                    host = cacheDns.QueryByIp6(new Ip6(ip));
                if (host != null) {
                    x.DestOriginalName = host;
                }
            }
        }

        private async void UDPListen()
        {
            try {
                udpClient = new UdpClient(listen);
                while (true) {
                    var r = await udpClient.ReceiveAsync();
                    Task.Run(() => HandleUdpReceiveResult(r)).Forget();
                }
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Warning, "UDP listener stopped");
            } finally {
                udpClient?.Close();
                udpClient = null;
            }
        }

        private async void HandleUdpReceiveResult(UdpReceiveResult r)
        {
            if (verbose)
                Logger.debugForce("DNS message received, length: " + r.Buffer.Length);
            byte[] respArray;
            try {
                var req = Request.FromArray(r.Buffer);
                var resp = await HandleDnsRequest(req);
                respArray = resp.ToArray();
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "processing msg from " + r.RemoteEndPoint);
                if (r.Buffer.Length < Header.SIZE)
                    return;
                try {
                    Header header = Header.FromArray(r.Buffer);
                    var resp = new Response();
                    resp.Id = header.Id;
                    resp.ResponseCode = ResponseCode.NotImplemented;
                    respArray = resp.ToArray();
                } catch (Exception e2) {
                    Logger.exception(e, Logging.Level.Error, "responding NotImplemented to " + r.RemoteEndPoint);
                    return;
                }
            }
            if (udpClient == null) { // the server is stopped
                if (verbose)
                    Logger.debugForce("DNS message processed after server stopped, length to send: " + respArray.Length);
                return;
            }
            try {
                if (verbose)
                    Logger.debugForce("DNS message processed, length to send: " + respArray.Length);
                var client = udpClient;
                if (client == null)
                    throw new Exception("UDP server stopped.");
                await client.SendAsync(respArray, respArray.Length, r.RemoteEndPoint);
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "DNS server failed to send response to " + r.RemoteEndPoint);
                return;
            }
        }

        Dictionary<string, Task<DnsResponse>> resolvingNames = new Dictionary<string, Task<DnsResponse>>();

        static readonly IPAddress[] emptyIps = new IPAddress[0];

        private async Task<IResponse> HandleDnsRequest(IRequest request)
        {
            var q = request;
            var r = Response.FromRequest(request);
            r.ResponseCode = ResponseCode.ServerFailure;
            try {
                var questions = q.Questions;
                if (questions.Count == 0) {
                    Logger.warning($"id {q.Id} does not contain any questions." +
                        $"\nAdditionalRecords: {string.Join(", ", q.AdditionalRecords)}");
                }
                // TODO: support AAAA correctly
                var queryNames = new List<Domain>();
                foreach (var item in questions) {
                    if (item.Type == RecordType.A || item.Type == RecordType.AAAA) {
                        if (verbose)
                            Logger.debugForce($"id {q.Id} query: {item}");
                        if (!queryNames.Contains(item.Name)) queryNames.Add(item.Name);
                    } else if (item.Type == RecordType.PTR) {
                    } else {
                        Logger.warning("Unsupported DNS record: " + item);
                    }
                }
                foreach (var name in queryNames) {
                    var strName = name.ToString();
                    IEnumerable<IPAddress> ips = emptyIps;
                    IpRecord val = new IpRecord();
                    bool exist = cacheDns?.QueryByName(strName, out val) ?? false;
                    var ipLongs = val.ipLongs;
                    var ips6 = val.ips6;
                    if (exist && val.expire > DateTime.Now) {
                        if (ipLongs != null) ips = ipLongs.Select(x => new IPAddress(x));
                        if (ips6 != null) ips = ips.Concat(ips6.Select(x => x.ToIPAddress()));
                    } else {
                        if (dnsProvider == null) {
                            throw new Exception("no dns resolver");
                        }
                        bool mainTask = false;
                        Task<DnsResponse> task;
                        var startTime = Logging.getRuntime();
                        lock (resolvingNames) {
                            if (!resolvingNames.TryGetValue(strName, out task)) {
                                task = dnsProvider.ResolveName(new DnsRequest { Name = strName, Type = RequestType.AnAAAA });
                                resolvingNames[strName] = task;
                                mainTask = true;
                            }
                        }
                        try {
                            try {
                                var resp = await task;
                                var iparr = resp.Addresses;
                                ips = iparr;
                                ipFilter(iparr, out ipLongs, out ips6);
                            } catch (Exception e) {
                                if (mainTask) {
                                    Logger.warning("resolving: " + strName + ": " + e.Message + " (" + (Logging.getRuntime() - startTime) + " ms)");
                                }
                                continue;
                            }
                            if (mainTask) {
                                Logger.info("" + strName + " -> " + string.Join("|", ips)
                                    + " (" + (Logging.getRuntime() - startTime) + " ms)");
                                var ir = new IpRecord {
                                    ipLongs = ipLongs,
                                    ips6 = ips6,
                                    expire = DateTime.Now.AddSeconds(cache_ttl)
                                };
                                cacheDns?.Set(strName, ref ir);
                            }
                        } finally {
                            if (mainTask) {
                                lock (resolvingNames)
                                    resolvingNames.Remove(strName);
                            }
                        }
                    }
                    foreach (var ip in ips) {
                        foreach (var que in questions) {
                            if (que.Name == name) {
                                if ((que.Type == RecordType.A && ip.AddressFamily == AddressFamily.InterNetwork)
                                    || (que.Type == RecordType.AAAA && ip.AddressFamily == AddressFamily.InterNetworkV6)) {
                                    r.AnswerRecords.Add(new IPAddressResourceRecord(name, ip, TimeSpan.FromSeconds(ttl)));
                                }
                            }
                        }
                    }
                    r.ResponseCode = ResponseCode.NoError;
                }
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "server");
            }
            return r;
        }

        private static void ipFilter(IPAddress[] ips, out uint[] ipLongs, out Ip6[] ips6)
        {
            int count = 0, count6 = 0;
            for (int i = 0; i < ips.Length; i++) {
                var cur = ips[i];
                if (cur.AddressFamily == AddressFamily.InterNetwork)
                    count++;
                else if (cur.AddressFamily == AddressFamily.InterNetworkV6)
                    count6++;
            }

            ipLongs = count == 0 ? null : new uint[count];
            ips6 = count6 == 0 ? null : new Ip6[count6];
            int ipLongsCur = 0;
            int ips6Cur = 0;
            for (int i = 0; i < ips.Length; i++) {
                var cur = ips[i];
                if (cur.AddressFamily == AddressFamily.InterNetwork) {
                    ipLongs[ipLongsCur++] = (uint)cur.Address;
                } else if (cur.AddressFamily == AddressFamily.InterNetworkV6) {
                    ips6[ips6Cur++] = new Ip6(cur);
                }
            }
        }
    }
}

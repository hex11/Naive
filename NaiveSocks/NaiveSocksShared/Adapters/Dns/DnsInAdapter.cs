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

        Dictionary<string, ResolveTask> resolvingNames = new Dictionary<string, ResolveTask>();
        Dictionary<string, ResolveTask> resolvingNames6 = new Dictionary<string, ResolveTask>();

        class ResolveTask
        {
            public Task<DnsResponse> A, AAAA;
        }

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

                //
                var queryNames = new Dictionary<Domain, DnsRequestType>();
                foreach (var item in questions) {
                    if (item.Type == RecordType.A || item.Type == RecordType.AAAA) {
                        DnsRequestType type = item.Type == RecordType.A ? DnsRequestType.A : DnsRequestType.AAAA;
                        if (verbose) Logger.debugForce($"id {q.Id} query: {item}");
                        if (!queryNames.ContainsKey(item.Name)) {
                            queryNames.Add(item.Name, type);
                        } else {
                            queryNames[item.Name] |= type;
                        }
                    } else if (item.Type == RecordType.PTR) {
                    } else {
                        Logger.warning("Unsupported DNS record: " + item);
                    }
                }

                foreach (var kv in queryNames) {
                    DnsRequestType reqType = kv.Value;
                    bool reqv4 = (reqType & DnsRequestType.A) != 0;
                    bool reqv6 = (reqType & DnsRequestType.AAAA) != 0;
                    var strName = kv.Key.ToString();
                    IEnumerable<IPAddress> ips = emptyIps;
                    IpRecord val = new IpRecord();
                    bool exist = cacheDns?.QueryByName(strName, out val) ?? false;
                    var now = DateTime.Now;
                    if (val.expire < now) val.ipLongs = null;
                    if (val.expire6 < now) val.ips6 = null;
                    var ipLongs = val.ipLongs;
                    var ips6 = val.ips6;
                    if (!exist || (reqv4 && ipLongs == null) || (reqv6 && ips6 == null)) {
                        var startTime = Logging.getRuntime();
                        var task = StartResolve(reqType, strName, out var newTask);
                        try {
                            try {
                                var resp = await task;
                                var iparr = resp.Addresses;
                                ips = iparr;
                                ipFilter(iparr, ref ipLongs, ref ips6);
                            } catch (Exception e) {
                                if (newTask) {
                                    Logger.warning("resolving: " + strName + " (" + reqType + "): " + e.Message + " (" + (Logging.getRuntime() - startTime) + " ms)");
                                }
                                continue;
                            }

                            if (newTask) {
                                Logger.info("" + strName + " (" + reqType + ") -> " + string.Join("|", ips)
                                    + " (" + (Logging.getRuntime() - startTime) + " ms)");
                                var newExpire = DateTime.Now.AddSeconds(cache_ttl);
                                // If we requested only A records (not AnAAAA) and an empty result returned, then there's
                                // really no A records, so we can cache the empty result:
                                if (reqType == DnsRequestType.A && ipLongs == null) ipLongs = new uint[0];
                                if (ipLongs != null) {
                                    val.ipLongs = ipLongs;
                                    val.expire = newExpire;
                                }
                                // The same reason:
                                if (reqType == DnsRequestType.AAAA && ips6 == null) ips6 = new Ip6[0];
                                if (ips6 != null) {
                                    val.ips6 = ips6;
                                    val.expire6 = newExpire;
                                }
                                cacheDns?.Set(strName, ref val);
                            }
                        } finally {
                            if (newTask) {
                                EndResolve(strName, task);
                            }
                        }
                    }

                    if (reqv4 && ipLongs != null) {
                        foreach (var item in ipLongs) {
                            r.AnswerRecords.Add(new ResourceRecord(kv.Key, BitConverter.GetBytes(item), RecordType.A, ttl: TimeSpan.FromSeconds(ttl)));
                        }
                    }
                    if (reqv6 && ips6 != null) {
                        foreach (var item in ips6) {
                            r.AnswerRecords.Add(new ResourceRecord(kv.Key, item.ToBytes(), RecordType.AAAA, ttl: TimeSpan.FromSeconds(ttl)));
                        }
                    }
                    r.ResponseCode = ResponseCode.NoError;
                }
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "server");
            }
            return r;
        }

        private Task<DnsResponse> StartResolve(DnsRequestType type, string strName, out bool newTask)
        {
            if (dnsProvider == null) {
                throw new Exception("no dns resolver");
            }
            lock (resolvingNames) {
                newTask = false;
                if (resolvingNames.TryGetValue(strName, out var rt)) {
                    // try to return a running task
                    if (type == DnsRequestType.A && rt.A != null) return rt.A;
                    if (type == DnsRequestType.AAAA || rt.AAAA != null) return rt.AAAA;
                    if (type == DnsRequestType.AnAAAA) {
                        if (rt.A == rt.AAAA) return rt.A;
                        if (rt.A != null && rt.AAAA != null) return UnionWarpper(rt);
                    }
                } else {
                    rt = new ResolveTask();
                    resolvingNames[strName] = rt;
                }
                var task = dnsProvider.ResolveName(new DnsRequest { Name = strName, Type = type });
                if ((type & DnsRequestType.A) != 0 && rt.A == null) rt.A = task;
                if ((type & DnsRequestType.AAAA) != 0 && rt.AAAA == null) rt.AAAA = task;
                newTask = true;
                return task;
            }
        }

        private void EndResolve(string strName, Task t)
        {
            lock (resolvingNames) {
                var rt = resolvingNames[strName];
                if (rt.A == t) rt.A = null;
                if (rt.AAAA == t) rt.AAAA = null;
                if (rt.A == null && rt.AAAA == null)
                    resolvingNames.Remove(strName);
            }
        }

        async Task<DnsResponse> UnionWarpper(ResolveTask rt)
        {
            return new DnsResponse(((await rt.A).Addresses ?? emptyIps).Union((await rt.AAAA).Addresses ?? emptyIps).ToArray());
        }

        private static void ipFilter(IPAddress[] ips, ref uint[] ipLongs, ref Ip6[] ips6)
        {
            int count = 0, count6 = 0;
            for (int i = 0; i < ips.Length; i++) {
                var cur = ips[i];
                if (cur.AddressFamily == AddressFamily.InterNetwork)
                    count++;
                else if (cur.AddressFamily == AddressFamily.InterNetworkV6)
                    count6++;
            }
            if (count > 0) ipLongs = new uint[count];
            if (count6 > 0) ips6 = new Ip6[count6];
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

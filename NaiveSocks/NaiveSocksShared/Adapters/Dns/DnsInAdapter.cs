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

        public ICacheReverseDns cacheRDns;
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
                cacheRDns = null;
            } else if (cache == "ram") {
                if (!(cacheRDns is SimpleCacheRDns)) {
                    cacheRDns = new SimpleCacheRDns();
                }
                if (!(cacheDns is SimpleCacheDns)) {
                    cacheDns = new SimpleCacheDns();
                }
            } else if (cache == "db") {
                if (cache_path == null) {
                    Logger.error("'cache_path' is not specified.");
                    return;
                }
                var db = new DnsDb(Controller.ProcessFilePath(cache_path));
                db.Logger = new Logger("db", Logger);
                db.Init();
                cacheRDns = db;
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
                var host = cacheRDns.TryGetDomain((uint)ip.Address);
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

        Dictionary<string, Task<IPAddress[]>> resolvingNames = new Dictionary<string, Task<IPAddress[]>>();

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
                foreach (var item in questions) {
                    if (verbose)
                        Logger.debugForce($"id {q.Id} query: {item}");
                    if (item.Type == RecordType.A) {
                        var strName = item.Name.ToString();
                        IEnumerable<IPAddress> ips;
                        IpRecord val = new IpRecord();
                        bool exist = cacheDns?.TryGetIp(strName, out val) ?? false;
                        var ipLongs = val.ipLongs;
                        if (exist && val.expire > DateTime.Now) {
                            if (ipLongs.Length == 0) {
                                throw new Exception("ipLongs.Length == 0");
                            }
                            ips = ipLongs.Select(x => new IPAddress(x));
                        } else {
                            if (dnsProvider == null) {
                                throw new Exception("no dns resolver");
                            }
                            bool mainTask = false;
                            Task<IPAddress[]> task;
                            var startTime = Logging.getRuntime();
                            lock (resolvingNames) {
                                if (!resolvingNames.TryGetValue(strName, out task)) {
                                    task = dnsProvider.ResolveName(strName);
                                    resolvingNames[strName] = task;
                                    mainTask = true;
                                }
                            }
                            try {
                                try {
                                    var iparr = await task;
                                    ips = iparr;
                                    ipLongs = ipv4Filter(iparr);
                                } catch (Exception e) {
                                    if (mainTask) {
                                        Logger.warning("resolving: " + strName + ": " + e.Message + " (" + (Logging.getRuntime() - startTime) + " ms)");
                                    }
                                    continue;
                                }
                                if (mainTask) {
                                    Logger.info("" + strName + " -> " + string.Join("|", ips.Where(x => x.AddressFamily == AddressFamily.InterNetwork))
                                        + " (" + (Logging.getRuntime() - startTime) + " ms)");
                                    cacheDns?.Set(strName, new IpRecord {
                                        ipLongs = ipLongs,
                                        expire = DateTime.Now.AddSeconds(cache_ttl)
                                    });
                                    cacheRDns?.Set(ipLongs, strName);
                                }
                            } finally {
                                if (mainTask) {
                                    lock (resolvingNames)
                                        resolvingNames.Remove(strName);
                                }
                            }
                        }
                        foreach (var ip in ips) {
                            r.AnswerRecords.Add(new IPAddressResourceRecord(item.Name, ip, TimeSpan.FromSeconds(ttl)));
                        }
                        r.ResponseCode = ResponseCode.NoError;
                    } else {
                        Logger.warning("Unsupported DNS record: " + item);
                    }
                }
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "server");
            }
            return r;
        }

        private static uint[] ipv4Filter(IPAddress[] ips)
        {
            uint[] ipLongs;
            int count = 0;
            for (int i = 0; i < ips.Length; i++) {
                var cur = ips[i];
                if (cur.AddressFamily == AddressFamily.InterNetwork)
                    count++;
            }
            if (count == 0)
                throw new Exception("No ipv4 address found.");
            ipLongs = new uint[count];
            int ipLongsCur = 0;
            for (int i = 0; i < ips.Length; i++) {
                var cur = ips[i];
                if (cur.AddressFamily == AddressFamily.InterNetwork) {
                    ipLongs[ipLongsCur++] = (uint)cur.Address;
                }
            }
            return ipLongs;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using LiteDB;
using Naive.HttpSvr;
using NaiveSocks;

namespace NaiveSocksAndroid
{
    partial class VpnHelper
    {
        class LocalDns
        {
            public VpnHelper vpnHelper;
            VpnConfig vpnConfig => vpnHelper.vpnConfig;
            IDnsProvider dnsResolver => vpnHelper.dnsResolver;

            ICacheReverseDns cacheRDns;
            ICacheDns cacheDns;

            string ipPrefix;
            int lastIp;

            public LocalDns(VpnHelper vpnHelper)
            {
                this.vpnHelper = vpnHelper;
            }

            public bool SocksConnectionFilter(InConnection x)
            {
                if (IPAddress.TryParse(x.Dest.Host, out var ip)) {
                    bool isFake = dnsResolver == null && ip.ToString().StartsWith(ipPrefix);
                    var host = cacheRDns.TryGetDomain(ip.Address);
                    if (host != null) {
                        x.DestOriginalName = host;
                    } else {
                        if (isFake)
                            Logging.warning("Fake DNS not found: " + ip);
                    }
                }
                return true;
            }

            public void StartDnsServer()
            {
                if (vpnConfig.DnsDomainDb) {
                    string dbPath = Path.Combine(Application.Context.CacheDir.AbsolutePath, "dns.litedb");
                    var dbcache = new NoSqlCache(dbPath);
                    cacheRDns = dbcache;
                    cacheDns = dbcache;
                } else {
                    if (!(cacheRDns is SimpleCacheRDns)) {
                        cacheRDns = new SimpleCacheRDns();
                    }
                    if (!(cacheDns is SimpleCacheDns)) {
                        cacheDns = new SimpleCacheDns();
                    }
                }

                ipPrefix = vpnConfig.FakeDnsPrefix;

                UDPListen().Forget();
            }

            UdpClient udpClient;

            private async Task UDPListen()
            {
                try {
                    udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, vpnConfig.LocalDnsPort));
                    while (true) {
                        var r = await udpClient.ReceiveAsync();
                        Task.Run(async () => {
                            if (vpnConfig.DnsDebug)
                                Logging.debugForce("DNS message received, length: " + r.Buffer.Length);
                            byte[] respArray;
                            try {
                                var req = Request.FromArray(r.Buffer);
                                var resp = await HandleDnsRequest(req);
                                respArray = resp.ToArray();
                                if (vpnConfig.DnsDebug)
                                    Logging.debugForce("DNS message processed, length to send: " + respArray.Length);
                            } catch (Exception e) {
                                Logging.exception(e, Logging.Level.Error, "DNS server processing msg from " + r.RemoteEndPoint);
                                if (r.Buffer.Length < Header.SIZE)
                                    return;
                                try {
                                    Header header = Header.FromArray(r.Buffer);
                                    var resp = new Response();
                                    resp.Id = header.Id;
                                    resp.ResponseCode = ResponseCode.NotImplemented;
                                    respArray = resp.ToArray();
                                } catch (Exception e2) {
                                    Logging.exception(e, Logging.Level.Error, "DNS server respond NotImplemented to " + r.RemoteEndPoint);
                                    return;
                                }
                            }
                            try {
                                await udpClient.SendAsync(respArray, respArray.Length, r.RemoteEndPoint);
                            } catch (Exception e) {
                                Logging.exception(e, Logging.Level.Error, "DNS server failed to send response to " + r.RemoteEndPoint);
                                return;
                            }
                        }).Forget();
                    }
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Warning, "DNS UDP listener stopped");
                } finally {
                    udpClient.Dispose();
                    udpClient = null;
                }
            }

            public void StopDnsServer()
            {
                udpClient?.Dispose();
            }

            private async Task<IResponse> HandleDnsRequest(IRequest request)
            {
                var q = request;
                var r = Response.FromRequest(request);
                r.ResponseCode = ResponseCode.ServerFailure;
                try {
                    var questions = q.Questions;
                    if (questions.Count == 0) {
                        Logging.warning($"DNS msg id {q.Id} does not contain any questions." +
                            $"\nAdditionalRecords: {string.Join(", ", q.AdditionalRecords)}");
                    }
                    foreach (var item in questions) {
                        if (vpnConfig.DnsDebug)
                            Logging.debugForce($"DNS id {q.Id} query: {item}");
                        if (item.Type == RecordType.A) {
                            var strName = item.Name.ToString();
                            IPAddress ip;
                            bool exist = cacheDns.TryGetIp(strName, out var val);
                            var ipLongs = val.ipLongs;
                            if (exist && val.expire > DateTime.UtcNow) {
                                ip = new IPAddress(ipLongs[NaiveUtils.Random.Next(ipLongs.Length)]);
                            } else {
                                if (dnsResolver == null) {
                                    ip = IPAddress.Parse(ipPrefix + Interlocked.Increment(ref lastIp));
                                    ipLongs = new long[] { ip.Address };
                                    Logging.info("Fake DNS: " + ip.ToString() + " -> " + strName);
                                } else {
                                    IPAddress[] ips;
                                    var startTime = Logging.getRuntime();
                                    try {
                                        ips = await dnsResolver.ResolveName(strName);
                                        ipLongs = ipv4Filter(ips);
                                        ip = ips.First(x => x.AddressFamily == AddressFamily.InterNetwork);
                                    } catch (Exception e) {
                                        Logging.warning("DNS resolving: " + strName + ": " + e.Message + " (" + (Logging.getRuntime() - startTime) + " ms)");
                                        continue;
                                    }
                                    Logging.info("DNS: " + strName + " -> " + string.Join("|", ips.Where(x => x.AddressFamily == AddressFamily.InterNetwork))
                                        + " (" + (Logging.getRuntime() - startTime) + " ms)");
                                }
                                cacheDns.Set(strName, new IpRecord {
                                    ipLongs = ipLongs,
                                    expire = DateTime.UtcNow.AddSeconds(vpnConfig.DnsCacheTtl)
                                });
                                cacheRDns.Set(ipLongs, strName);
                            }
                            r.AnswerRecords.Add(new IPAddressResourceRecord(item.Name, ip, TimeSpan.FromSeconds(vpnConfig.DnsTtl)));
                            r.ResponseCode = ResponseCode.NoError;
                        } else {
                            Logging.warning("Unsupported DNS record: " + item);
                        }
                    }
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "DNS server");
                }
                return r;
            }

            private static long[] ipv4Filter(IPAddress[] ips)
            {
                long[] ipLongs;
                int count = 0;
                for (int i = 0; i < ips.Length; i++) {
                    var cur = ips[i];
                    if (cur.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        count++;
                }
                if (count == 0)
                    throw new Exception("No ipv4 address found.");
                ipLongs = new long[count];
                int ipLongsCur = 0;
                for (int i = 0; i < ips.Length; i++) {
                    var cur = ips[i];
                    if (cur.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) {
                        ipLongs[ipLongsCur++] = cur.Address;
                    }
                }

                return ipLongs;
            }

            interface ICacheDns
            {
                bool TryGetIp(string domain, out IpRecord val);
                void Set(string domain, IpRecord val);
            }

            struct IpRecord
            {
                public DateTime expire;
                public long[] ipLongs;
            }

            interface ICacheReverseDns
            {
                string TryGetDomain(long ip);
                void Set(long ip, string domain);
                void Set(long[] ips, string domain);
            }

            class SimpleCacheDns : ICacheDns
            {
                ReaderWriterLockSlim mapLock = new ReaderWriterLockSlim();
                Dictionary<string, IpRecord> mapHostIp = new Dictionary<string, IpRecord>();

                public void Set(string domain, IpRecord val)
                {
                    if (domain == null)
                        throw new ArgumentNullException(nameof(domain));

                    mapLock.EnterWriteLock();
                    mapHostIp[domain] = val;
                    mapLock.ExitWriteLock();
                }

                public bool TryGetIp(string domain, out IpRecord val)
                {
                    if (domain == null)
                        throw new ArgumentNullException(nameof(domain));

                    mapLock.EnterReadLock();
                    try {
                        return mapHostIp.TryGetValue(domain, out val);
                    } finally {
                        mapLock.ExitReadLock();
                    }
                }
            }

            class SimpleCacheRDns : ICacheReverseDns
            {
                ReaderWriterLockSlim mapLock = new ReaderWriterLockSlim();
                Dictionary<long, string> mapIpHost = new Dictionary<long, string>();

                public void Set(long ip, string domain)
                {
                    if (domain == null)
                        throw new ArgumentNullException(nameof(domain));

                    mapLock.EnterWriteLock();
                    mapIpHost[ip] = domain;
                    mapLock.ExitWriteLock();
                }

                public void Set(long[] ips, string domain)
                {
                    if (domain == null)
                        throw new ArgumentNullException(nameof(domain));

                    mapLock.EnterWriteLock();
                    foreach (var item in ips) {
                        mapIpHost[item] = domain;
                    }
                    mapLock.ExitWriteLock();
                }

                public string TryGetDomain(long ip)
                {
                    mapLock.EnterReadLock();
                    try {
                        if (mapIpHost.TryGetValue(ip, out var host))
                            return host;
                        return null;
                    } finally {
                        mapLock.ExitReadLock();
                    }
                }
            }

            class NoSqlCache : ICacheReverseDns, ICacheDns
            {
                LiteDatabase liteDb;
                LiteCollection<Record> collection;
                LiteCollection<BsonDocument> cfgCollection;

                object syncRoot = new object();

                public NoSqlCache(string dbPath)
                {
                    liteDb = new LiteDatabase(dbPath);
                    cfgCollection = liteDb.GetCollection("meta");
                    collection = liteDb.GetCollection<Record>("dns_records");
                    collection.EnsureIndex("Ips", "$.Ips[*]", false);
                    collection.EnsureIndex("Domain", false);
                    Logging.info($"dns db: {collection.Count()} records.");
                    CheckShrink();
                }

                BsonValue GetConfigValue(string name)
                {
                    return cfgCollection.FindOne(Query.EQ("Key", name))?["Value"];
                }

                void SetConfigValue(string name, BsonValue value)
                {
                    var doc = cfgCollection.FindOne(Query.EQ("Key", name));
                    if (doc == null) {
                        doc = new BsonDocument();
                        doc.Add("Key", name);
                    }
                    doc["Value"] = value;
                    cfgCollection.Upsert(doc);
                }

                private void CheckShrink()
                {
                    const string ls = "LastShrink";
                    var lastShrink = GetConfigValue(ls);
                    Logging.info("dns db: last shrink: " + (lastShrink?.AsDateTime.ToString() ?? "(null)"));
                    if (lastShrink == null || DateTime.UtcNow - lastShrink.AsDateTime > TimeSpan.FromDays(1)) {
                        SetConfigValue(ls, DateTime.UtcNow);
                        Shrink();
                    }
                }

                private void Shrink()
                {
                    Logging.info("dns db: start shrinking...");
                    var reduced = liteDb.Shrink();
                    if (reduced > 0) {
                        Logging.info("dns db: shrinked and reduced " + reduced + " bytes.");
                    } else if (reduced < 0) {
                        Logging.info("dns db: shrinked and \"reduced\" " + reduced + " bytes.");
                    } else {
                        Logging.info("dns db: shrinked and nothing happended.");
                    }
                }

                public void Set(string domain, IpRecord val)
                {
                    lock (syncRoot) {
                        var r = FindDocByDomain(domain).SingleOrDefault();
                        if (r == null)
                            r = new Record() { Domain = domain };
                        r.Ips = val.ipLongs;
                        r.Date = DateTime.UtcNow;
                        r.Expire = val.expire;
                        collection.Upsert(r);
                    }
                }

                public void Set(long[] ips, string domain)
                {
                    // done by Set(string, IpRecord)
                }

                public void Set(long ip, string domain)
                {
                }

                public string TryGetDomain(long ip)
                {
                    var docs = FindDocByIp(ip);
                    var r = GetFirstOrNull(ip, docs, out var m);
                    if (m) {
                        var domainsSb = new StringBuilder();
                        foreach (var item in docs) {
                            if (domainsSb.Length != 0)
                                domainsSb.Append('|');
                            domainsSb.Append(item.Domain);
                            if (r.Expire < item.Expire) {
                                r = item;
                            }
                        }
                        Logging.warning($"dns db: multiple domains ({domainsSb}) resovle to a ip address ({new IPAddress(ip)}).");
                    }
                    return r?.Domain;
                }

                public bool TryGetIp(string domain, out IpRecord val)
                {
                    var r = FindDocByDomain(domain).SingleOrDefault();
                    if (r != null) {
                        val = new IpRecord { ipLongs = r.Ips, expire = r.Expire };
                        return true;
                    }
                    val = default(IpRecord);
                    return false;
                }


                private IEnumerable<Record> FindDocByIp(long ip)
                {
                    return collection.Find(Query.EQ("Ips[*]", new BsonValue(ip)));
                }

                private IEnumerable<Record> FindDocByDomain(string domain)
                {
                    return collection.Find(Query.EQ("Domain", new BsonValue(domain)));
                }

                private Record GetFirstOrNull(long ip, IEnumerable<Record> docs, out bool multipleItems)
                {
                    Record doc;
                    var e = docs.GetEnumerator();
                    if (e.MoveNext()) {
                        doc = e.Current;
                        if (e.MoveNext()) {
                            multipleItems = true;
                            return doc;
                        }
                    } else {
                        doc = null;
                    }
                    multipleItems = false;
                    return doc;
                }

                class Record
                {
                    public int Id { get; set; }
                    public string Domain { get; set; }
                    public long[] Ips { get; set; }
                    public DateTime Date { get; set; }
                    public DateTime Expire { get; set; }

                    public override string ToString()
                    {
                        return $"{{Id={Id}, Domain={Domain}, Ips={string.Join("|", Ips)}}}";
                    }
                }
            }
        }
    }
}
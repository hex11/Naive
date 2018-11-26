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
            ICacheDns cacheDns = new SimpleCacheDns();

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
                    cacheRDns = new NoSqlCacheRDns(dbPath);
                } else {
                    if (!(cacheRDns is SimpleCacheRDns)) {
                        cacheRDns = new SimpleCacheRDns();
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
                            try {
                                var req = Request.FromArray(r.Buffer);
                                var resp = await HandleDnsRequest(req);
                                var respArray = resp.ToArray();
                                if (vpnConfig.DnsDebug)
                                    Logging.debugForce("DNS message processed, length to send: " + respArray.Length);
                                try {
                                    await udpClient.SendAsync(respArray, respArray.Length, r.RemoteEndPoint);
                                } catch (Exception e) {
                                    Logging.exception(e, Logging.Level.Error, "DNS server failed to send response to " + r.RemoteEndPoint);
                                    return;
                                }
                            } catch (Exception e) {
                                Logging.exception(e, Logging.Level.Error, "DNS server processing msg from " + r.RemoteEndPoint);
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
                            if (exist && val.expire > Logging.getRuntime()) {
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
                                    expire = Logging.getRuntime() + vpnConfig.DnsCacheTtl * 1000
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

            class NoSqlCacheRDns : ICacheReverseDns
            {
                LiteDatabase liteDb;
                LiteCollection<Record> collection;

                object syncLock = new object();

                public NoSqlCacheRDns(string dbPath)
                {
                    liteDb = new LiteDatabase(dbPath);
                    collection = liteDb.GetCollection<Record>("rdns_records");
                    collection.EnsureIndex("Ip", false);
                    Logging.info("rdns db: start shrinking...");
                    var reduced = liteDb.Shrink();
                    if (reduced > 0) {
                        Logging.info("rdns db: shrinked and reduced " + reduced + " bytes.");
                    } else if (reduced < 0) {
                        Logging.info("rdns db: shrinked and \"reduced\" " + reduced + " bytes.");
                    } else {
                        Logging.info("rdns db: shrinked and nothing happended.");
                    }
                }

                public void Set(long ip, string domain)
                {
                    if (domain == null)
                        throw new ArgumentNullException(nameof(domain));

                    lock (syncLock) {
                        Record doc = GetFirstOrNull(ip, FindDocByIp(ip));
                        if (doc == null) {
                            doc = new Record { Ip = ip, Domain = domain };
                        } else {
                            if (domain == doc.Domain)
                                return;
                            Logging.warning($"rdns db: set [{new IPAddress(ip)}] = {domain} overriding {doc.Domain}");
                            doc.Domain = domain;
                        }

                        collection.Upsert(doc);
                    }
                }

                private Record GetFirstOrNull(long ip, IEnumerable<Record> docs)
                {
                    Record doc;
                    var e = docs.GetEnumerator();
                    if (e.MoveNext()) {
                        doc = e.Current;
                        if (e.MoveNext()) {
                            var docsList = docs.ToList();
                            string domainsStr = string.Join("|", docsList.Select(x => x.Domain));
                            Logging.warning($"rdns db: multiple domains ({domainsStr}) resovles to a ip address ({new IPAddress(ip)}).");
                            for (int i = 0; i < docsList.Count; i++) {
                                var d = docsList[i];
                                if (d.Domain == null) // marked as deleted
                                    continue;
                                for (int j = i + 1; j < docsList.Count; j++) {
                                    Record d2 = docsList[j];
                                    if (d.Domain == d2.Domain) {
                                        var ok = collection.Delete(d2.Id);
                                        Logging.warning($"rdns db: {(ok ? "deleted" : "failed to delete")} duplicated " + d2);
                                        d2.Domain = null;
                                    }
                                }
                            }
                        }
                    } else {
                        doc = null;
                    }

                    return doc;
                }

                public void Set(long[] ips, string domain)
                {
                    foreach (var ip in ips) {
                        Set(ip, domain);
                    }
                }

                public string TryGetDomain(long ip)
                {
                    var doc = GetFirstOrNull(ip, FindDocByIp(ip));
                    return doc?.Domain;
                }

                private IEnumerable<Record> FindDocByIp(long ip)
                {
                    return collection.Find(Query.EQ("Ip", new BsonValue(ip)));
                }

                class Record
                {
                    public int Id { get; set; }
                    public long Ip { get; set; }
                    public string Domain { get; set; }

                    public override string ToString()
                    {
                        return $"{{Id={Id}, Ip={Ip}, Domain={Domain}}}";
                    }
                }
            }
        }
    }
}
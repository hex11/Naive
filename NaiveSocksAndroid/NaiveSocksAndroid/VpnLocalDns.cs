﻿using System;
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
using ARSoft.Tools.Net.Dns;
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

            ICacheDomain cacheDomain;
            ICacheIp cacheIp = new SimpleCacheIp();

            DnsServer dnsServer;
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
                    var host = cacheDomain.TryGetDomain(ip.Address);
                    if (host != null) {
                        x.Dest.Host = host;
                        x.DestIp = ip;
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
                    string dbPath = Path.Combine(Application.Context.CacheDir.AbsolutePath, "cache_domain.litedb");
                    cacheDomain = new NoSqlCacheDomain(dbPath);
                } else {
                    if (!(cacheDomain is SimpleCacheDomain)) {
                        cacheDomain = new SimpleCacheDomain();
                    }
                }

                ipPrefix = vpnConfig.FakeDnsPrefix;
                dnsServer = new DnsServer(new IPEndPoint(IPAddress.Any, vpnConfig.LocalDnsPort), vpnConfig.DnsListenerCount, 0);
                dnsServer.ExceptionThrown += DnsServer_ExceptionThrown;
                dnsServer.QueryReceived += DnsServer_QueryReceived;
                dnsServer.Start();
            }

            public void StopDnsServer()
            {
                if (dnsServer == null)
                    return;
                dnsServer.Stop();
                dnsServer = null;
            }

            private Task DnsServer_ExceptionThrown(object sender, ExceptionEventArgs eventArgs)
            {
                Logging.exception(eventArgs.Exception, Logging.Level.Error, "DnsServer exception");
                return NaiveUtils.CompletedTask;
            }

            private async Task DnsServer_QueryReceived(object sender, QueryReceivedEventArgs eventArgs)
            {
                try {
                    var q = eventArgs.Query as DnsMessage;
                    var r = q.CreateResponseInstance();
                    eventArgs.Response = r;
                    r.ReturnCode = ReturnCode.ServerFailure;
                    foreach (var item in q.Questions) {
                        if (vpnConfig.DnsDebug)
                            Logging.debugForce("DNS query: " + item);
                        if (item.RecordType == RecordType.A) {
                            var strName = item.Name.ToString();
                            strName = strName.Substring(0, strName.Length - 1); // remove the trailing '.'
                            IPAddress ip;
                            bool exist = cacheIp.TryGetIp(strName, out var val);
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
                                cacheIp.Set(strName, new IpRecord {
                                    ipLongs = ipLongs,
                                    expire = Logging.getRuntime() + vpnConfig.DnsCacheTtl * 1000
                                });
                                cacheDomain.Set(ipLongs, strName);
                            }
                            r.AnswerRecords.Add(new ARecord(item.Name, vpnConfig.DnsTtl, ip));
                            r.ReturnCode = ReturnCode.NoError;
                        } else {
                            Logging.warning("Unsupported DNS record: " + item);
                        }
                    }
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "DNS server");
                }
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

            interface ICacheIp
            {
                bool TryGetIp(string domain, out IpRecord val);
                void Set(string domain, IpRecord val);
            }

            interface ICacheDomain
            {
                string TryGetDomain(long ip);
                void Set(long ip, string domain);
                void Set(long[] ips, string domain);
            }

            class SimpleCacheIp : ICacheIp
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

            class SimpleCacheDomain : ICacheDomain
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

            class NoSqlCacheDomain : ICacheDomain
            {
                LiteDatabase liteDb;
                LiteCollection<Record> collection;

                public NoSqlCacheDomain(string dbPath)
                {
                    liteDb = new LiteDatabase(dbPath);
                    collection = liteDb.GetCollection<Record>("dns_records");
                    collection.EnsureIndex("Ip");
                }

                public void Set(long ip, string domain)
                {
                    if (domain == null)
                        throw new ArgumentNullException(nameof(domain));

                    collection.Insert(new Record { Ip = ip, Domain = domain });
                }

                public void Set(long[] ips, string domain)
                {
                    foreach (var ip in ips) {
                        Set(ip, domain);
                    }
                }

                public string TryGetDomain(long ip)
                {
                    var bsonVal = new BsonValue(ip);
                    foreach (var item in collection.Find(Query.EQ("ip", bsonVal))) {
                        return item.Domain;
                    }
                    return null;
                }

                class Record
                {
                    public int Id { get; set; }
                    public long Ip { get; set; }
                    public string Domain { get; set; }
                }
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Widget;
using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using Naive.HttpSvr;
using NaiveSocks;

namespace NaiveSocksAndroid
{
    partial class VpnHelper
    {
        partial class LocalDns
        {
            public VpnHelper vpnHelper;
            VpnConfig vpnConfig => vpnHelper.VpnConfig;
            IDnsProvider dnsResolver => vpnHelper.dnsResolver;

            public DnsDb DnsDb => cacheDns as DnsDb;

            ICacheReverseDns cacheRDns;
            ICacheDns cacheDns;

            public LocalDns(VpnHelper vpnHelper)
            {
                this.vpnHelper = vpnHelper;
            }

            public bool SocksConnectionFilter(InConnection x)
            {
                if (IPAddress.TryParse(x.Dest.Host, out var ip)) {
                    var host = cacheRDns.TryGetDomain((uint)ip.Address);
                    if (host != null) {
                        x.DestOriginalName = host;
                    }
                }
                return true;
            }

            public void StartDnsServer()
            {
                if (vpnConfig.DnsDomainDb) {
                    var db = new DnsDb(App.DnsDbFile);
                    cacheRDns = db;
                    cacheDns = db;
                } else {
                    if (!(cacheRDns is SimpleCacheRDns)) {
                        cacheRDns = new SimpleCacheRDns();
                    }
                    if (!(cacheDns is SimpleCacheDns)) {
                        cacheDns = new SimpleCacheDns();
                    }
                }

                UDPListen().Forget();
            }

            UdpClient udpClient;
            IPEndPoint udpBindEp;

            private async Task UDPListen()
            {
                try {
                    udpBindEp = new IPEndPoint(IPAddress.Loopback, vpnConfig.LocalDnsPort);
                    udpClient = new UdpClient(udpBindEp);
                    while (true) {
                        var r = await udpClient.ReceiveAsync();
                        Task.Run(() => HandleUdpReceiveResult(r)).Forget();
                    }
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Warning, "DNS UDP listener stopped");
                } finally {
                    udpClient.Dispose();
                    udpClient = null;
                }
            }

            private async Task HandleUdpReceiveResult(UdpReceiveResult r)
            {
                if (vpnConfig.DnsDebug)
                    Logging.debugForce("DNS message received, length: " + r.Buffer.Length);
                byte[] respArray;
                try {
                    var req = Request.FromArray(r.Buffer);
                    var resp = await HandleDnsRequest(req);
                    respArray = resp.ToArray();
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
                        Logging.exception(e, Logging.Level.Error, "DNS server responding NotImplemented to " + r.RemoteEndPoint);
                        return;
                    }
                }
                if (udpClient == null) { // the server is stopped
                    if (vpnConfig.DnsDebug)
                        Logging.debugForce("DNS message processed after server stopped, length to send: " + respArray.Length);
                    return;
                }
                try {
                    if (vpnConfig.DnsDebug)
                        Logging.debugForce("DNS message processed, length to send: " + respArray.Length);
                    using (var udpc = new UdpClient(udpBindEp)) {
                        await udpc.SendAsync(respArray, respArray.Length, r.RemoteEndPoint);
                    }
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "DNS server failed to send response to " + r.RemoteEndPoint);
                    return;
                }
            }

            public void StopDnsServer()
            {
                udpClient?.Dispose();
                udpClient = null;
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
                            if (exist && val.expire > DateTime.Now) {
                                ip = new IPAddress(ipLongs[NaiveUtils.Random.Next(ipLongs.Length)]);
                            } else {
                                if (dnsResolver == null) {
                                    throw new Exception("no dns resolver");
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
                                    expire = DateTime.Now.AddSeconds(vpnConfig.DnsCacheTtl)
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

            private static uint[] ipv4Filter(IPAddress[] ips)
            {
                uint[] ipLongs;
                int count = 0;
                for (int i = 0; i < ips.Length; i++) {
                    var cur = ips[i];
                    if (cur.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        count++;
                }
                if (count == 0)
                    throw new Exception("No ipv4 address found.");
                ipLongs = new uint[count];
                int ipLongsCur = 0;
                for (int i = 0; i < ips.Length; i++) {
                    var cur = ips[i];
                    if (cur.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) {
                        ipLongs[ipLongsCur++] = (uint)cur.Address;
                    }
                }

                return ipLongs;
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
                Dictionary<uint, string> mapIpHost = new Dictionary<uint, string>();

                public void Set(uint ip, string domain)
                {
                    if (domain == null)
                        throw new ArgumentNullException(nameof(domain));

                    mapLock.EnterWriteLock();
                    mapIpHost[ip] = domain;
                    mapLock.ExitWriteLock();
                }

                public void Set(uint[] ips, string domain)
                {
                    if (domain == null)
                        throw new ArgumentNullException(nameof(domain));

                    mapLock.EnterWriteLock();
                    foreach (var item in ips) {
                        mapIpHost[item] = domain;
                    }
                    mapLock.ExitWriteLock();
                }

                public string TryGetDomain(uint ip)
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
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace NaiveSocks
{
    public interface ICacheDns
    {
        bool TryGetIp(string domain, out IpRecord val);
        void Set(string domain, IpRecord val);
    }

    public struct IpRecord
    {
        public DateTime expire;
        public uint[] ipLongs;

        public override string ToString()
        {
            return $"{{expire={expire}, ips={string.Join("|", ipLongs.Select(x => new IPAddress(x)))}}}";
        }
    }

    public interface ICacheReverseDns
    {
        string TryGetDomain(uint ip);
        void Set(uint ip, string domain);
        void Set(uint[] ips, string domain);
    }

    public class SimpleCacheDns : ICacheDns
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

    public class SimpleCacheRDns : ICacheReverseDns
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

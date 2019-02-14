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
        bool QueryByName(string name, out IpRecord val);
        string QueryByIp(uint ip);
        string QueryByIp6(Ip6 ip);
        void Set(string domain, ref IpRecord val);
    }

    public struct IpRecord
    {
        public uint[] ipLongs;
        public Ip6[] ips6;
        public DateTime expire;
        public DateTime expire6;

        public override string ToString()
        {
            return $"{{expire={expire}, ips={string.Join("|", ipLongs.Select(x => new IPAddress(x)))}}}";
        }
    }

    public struct Ip6
    {
        public static readonly IEqualityComparer<Ip6> EqualityComparer = new Ip6Comparer();

        public byte[] bytes;

        public Ip6(IPAddress ip)
        {
            bytes = ip.GetAddressBytes();
        }

        public Ip6(byte[] bytes)
        {
            this.bytes = bytes;
        }

        public byte[] ToBytes()
        {
            return bytes;
        }

        public IPAddress ToIPAddress()
        {
            return new IPAddress(bytes);
        }

        class Ip6Comparer : EqualityComparer<Ip6>
        {
            public override bool Equals(Ip6 ipl, Ip6 ipr)
            {
                var left = ipl.bytes;
                var right = ipr.bytes;
                if (left == null || right == null) {
                    return left == right;
                }
                if (left.Length != right.Length) {
                    return false;
                }
                for (int i = 0; i < left.Length; i++) {
                    if (left[i] != right[i]) {
                        return false;
                    }
                }
                return true;
            }

            public override int GetHashCode(Ip6 key)
            {
                if (key.bytes == null) return 0;
                int count = 8;
                int sum = 0;
                foreach (byte cur in key.bytes) {
                    sum = sum * 17 + cur;
                    if (--count == 0) break;
                }
                return sum;
            }
        }
    }

    public class SimpleCacheDns : ICacheDns
    {
        ReaderWriterLockSlim mapLock = new ReaderWriterLockSlim();
        Dictionary<string, IpRecord> mapHostIp = new Dictionary<string, IpRecord>();

        public void Set(string domain, ref IpRecord val)
        {
            if (domain == null)
                throw new ArgumentNullException(nameof(domain));

            mapLock.EnterWriteLock();
            mapHostIp[domain] = val;
            mapLock.ExitWriteLock();

            SetReverseMap(ref val, domain);
        }

        public bool QueryByName(string name, out IpRecord val)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            mapLock.EnterReadLock();
            try {
                return mapHostIp.TryGetValue(name, out val);
            } finally {
                mapLock.ExitReadLock();
            }
        }

        ReaderWriterLockSlim mapLock2 = new ReaderWriterLockSlim();
        Dictionary<uint, string> mapIpHost = new Dictionary<uint, string>();
        Dictionary<Ip6, string> mapIp6Host = new Dictionary<Ip6, string>(Ip6.EqualityComparer);

        void SetReverseMap(ref IpRecord r, string domain)
        {
            if (domain == null)
                throw new ArgumentNullException(nameof(domain));

            mapLock2.EnterWriteLock();
            if (r.ipLongs != null)
                foreach (var item in r.ipLongs) {
                    mapIpHost[item] = domain;
                }
            if (r.ips6 != null)
                foreach (var item in r.ips6) {
                    mapIp6Host[item] = domain;
                }
            mapLock2.ExitWriteLock();
        }

        public string QueryByIp(uint ip)
        {
            mapLock2.EnterReadLock();
            try {
                if (mapIpHost.TryGetValue(ip, out var host))
                    return host;
                return null;
            } finally {
                mapLock2.ExitReadLock();
            }
        }

        public string QueryByIp6(Ip6 ip)
        {
            mapLock2.EnterReadLock();
            try {
                if (mapIp6Host.TryGetValue(ip, out var host))
                    return host;
                return null;
            } finally {
                mapLock2.ExitReadLock();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Android.Views;
using LiteDB;
using Naive.HttpSvr;

namespace NaiveSocksAndroid
{
    interface ICacheDns
    {
        bool TryGetIp(string domain, out IpRecord val);
        void Set(string domain, IpRecord val);
    }

    struct IpRecord
    {
        public DateTime expire;
        public uint[] ipLongs;

        public override string ToString()
        {
            return $"{{expire={expire}, ips={string.Join("|", ipLongs.Select(x => new IPAddress(x)))}}}";
        }
    }

    interface ICacheReverseDns
    {
        string TryGetDomain(uint ip);
        void Set(uint ip, string domain);
        void Set(uint[] ips, string domain);
    }

    internal class DnsDb : ICacheReverseDns, ICacheDns
    {
        const string LogStamp = "DnsDb: ";

        LiteDatabase liteDb;
        LiteCollection<Record> collection;
        LiteCollection<BsonDocument> cfgCollection;

        public string FilePath { get; }

        object syncRoot = new object();

        public int queryByIps;
        public int queryByIpTotalTime;
        public int queryByDomains;
        public int queryByDomainTotalTime;
        public int inserts;
        public int insertTotalTime;

        public DnsDb(string dbPath)
        {
            FilePath = dbPath;
            Logging.info(LogStamp + "initializing...");
            bool retrying = false;
        BEGIN:
            try {
                liteDb = new LiteDatabase(dbPath);
                cfgCollection = liteDb.GetCollection("meta");

                CheckVersion();

                collection = liteDb.GetCollection<Record>("dns_records_v2");
                collection.EnsureIndex("idx_ips", "$.Ips[*]", false);
                collection.EnsureIndex("idx_oldips", "$.OldIps[*]", false);
                collection.EnsureIndex("Domain", true);
                Logging.info(LogStamp + $"{collection.Count()} records.");
                CheckShrink();
            } catch (Exception e) {
                Logging.exception(e, Logging.Level.Error, LogStamp + "failed to initialize");
                if (!retrying) {
                    retrying = true;
                    Logging.info(LogStamp + "delete current db file and retry...");
                    liteDb.Dispose();
                    System.IO.File.Delete(dbPath);
                    goto BEGIN;
                }
            }
        }

        private void CheckVersion()
        {
            BsonValue ver = GetConfigValue("Version");
            if (ver == null && liteDb.CollectionExists("dns_records")) {
                Logging.info(LogStamp + "upgrading from v0 to v1...");
                var col = liteDb.GetCollection("dns_records");
                var colNew = liteDb.GetCollection("dns_records_v1");
                int id = 1;
                foreach (var doc in col.FindAll()) {
                    BsonArray arr = doc["Ips"].AsArray;
                    var newArr = new BsonValue[arr.Count];
                    for (int i = 0; i < arr.Count; i++) {
                        newArr[i] = new BsonValue((int)arr[i].AsInt64);
                    }
                    doc["Ips"] = new BsonArray(newArr);
                    doc["_id"] = new BsonValue(id++);
                    colNew.Insert(doc);
                }
                ver = 1;
                SetConfigValue("Version", ver);
                liteDb.DropCollection("dns_records");
                Logging.info(LogStamp + "finished upgrade.");
            }
            if (ver == null) {
                ver = 2;
                SetConfigValue("Version", ver);
                return;
            }
            if (ver == 1) {
                Logging.info(LogStamp + "upgrading from v1 to v2...");
                var col = liteDb.GetCollection("dns_records_v1");
                col.DropIndex("Ips");
                liteDb.RenameCollection("dns_records_v1", "dns_records_v2");
                ver = 2;
                SetConfigValue("Version", ver);
                Logging.info(LogStamp + "finished upgrade.");
            }
            if (ver != 2) {
                throw new Exception(LogStamp + "db version " + GetConfigValue("Version").AsInt32 + " is not supported.");
            }
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
            Logging.info(LogStamp + "last shrink: " + (lastShrink?.AsDateTime.ToString() ?? "(null)"));
            if (lastShrink == null || DateTime.Now - lastShrink.AsDateTime > TimeSpan.FromDays(1)) {
                SetConfigValue(ls, DateTime.Now);
                Clean(DateTime.Now.AddDays(-3));
                Shrink();
            }
        }

        public int RecordCount() => collection.Count();

        public long Shrink()
        {
            Logging.info(LogStamp + "start shrinking...");
            var reduced = liteDb.Shrink();
            if (reduced > 0) {
                Logging.info(LogStamp + "shrinked and reduced " + reduced + " bytes.");
            } else if (reduced < 0) {
                Logging.info(LogStamp + "shrinked and \"reduced\" " + reduced + " bytes.");
            } else {
                Logging.info(LogStamp + "shrinked and nothing happended.");
            }
            return reduced;
        }

        public void Set(string domain, IpRecord val)
        {
            lock (syncRoot) {
                var begin = Logging.getRuntime();
                inserts++;
                var r = FindDocByDomain(domain).SingleOrDefault();
                if (r == null)
                    r = new Record() { Domain = domain };
                int[] ipints = new int[val.ipLongs.Length];
                for (int i = 0; i < ipints.Length; i++) {
                    ipints[i] = (int)val.ipLongs[i];
                }
                if (r.Ips != null && r.Ips.Length > 0) {
                    if (r.OldIps != null) {
                        r.OldIps = r.OldIps.Union(r.Ips).Except(ipints).ToArray();
                    } else {
                        r.OldIps = r.Ips.Except(ipints).ToArray();
                    }
                }
                r.Ips = ipints;
                r.Date = DateTime.Now;
                r.Expire = val.expire;
                collection.Upsert(r);
                insertTotalTime += (int)(Logging.getRuntime() - begin);
            }
        }

        public void Set(uint[] ips, string domain)
        {
            // done by Set(string, IpRecord)
        }

        public void Set(uint ip, string domain)
        {
        }

        public string TryGetDomain(uint ip)
        {
            Interlocked.Increment(ref queryByIps);
            var begin = Logging.getRuntime();
            try {
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
                    Logging.warning($"{LogStamp}multiple domains ({domainsSb}) resolve to a ip address ({new IPAddress(ip)}).");
                } else if (r == null) {
                    docs = FindDocByOldIp(ip);
                    r = GetFirstOrNull(ip, docs, out m);
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
                        Logging.warning($"{LogStamp}multiple domains ({domainsSb}) were (but not now) resolving to a ip address ({new IPAddress(ip)}).");
                    } else if (r != null) {
                        Logging.warning($"{LogStamp}domain ({r.Domain}) was (but not now) resolving to ip ({new IPAddress(ip)}).");
                    }
                }
                return r?.Domain;
            } finally {
                Interlocked.Add(ref queryByIpTotalTime, (int)(Logging.getRuntime() - begin));
            }
        }

        public bool TryGetIp(string domain, out IpRecord val)
        {
            Interlocked.Increment(ref queryByDomains);
            var begin = Logging.getRuntime();
            try {
                var r = FindDocByDomain(domain).SingleOrDefault();
                if (r != null) {
                    val = new IpRecord();
                    val.ipLongs = new uint[r.Ips.Length];
                    for (int i = 0; i < val.ipLongs.Length; i++) {
                        val.ipLongs[i] = (uint)r.Ips[i];
                    }
                    val.expire = r.Expire;
                    return true;
                }
                val = default(IpRecord);
                return false;
            } finally {
                Interlocked.Add(ref queryByDomainTotalTime, (int)(Logging.getRuntime() - begin));
            }
        }

        public bool Delete(string domain)
        {
            lock (syncRoot) {
                var r = FindDocByDomain(domain).SingleOrDefault();
                if (r == null)
                    return false;
                return collection.Delete(r.Id);
            }
        }

        public int Clean(DateTime expiredBefore)
        {
            Logging.info($"{LogStamp}deleting records expired before {expiredBefore}...");
            var r = collection.Delete(Query.LT("Expire", expiredBefore));
            Logging.info($"{LogStamp}deleted {r}.");
            return r;
        }

        private IEnumerable<Record> FindDocByIp(uint ip)
        {
            int ipInt = (int)ip;
            return collection.Find(Query.EQ("idx_ips", ipInt));
        }

        private IEnumerable<Record> FindDocByOldIp(uint ip)
        {
            int ipInt = (int)ip;
            return collection.Find(Query.EQ("idx_oldips", ipInt));
        }

        private IEnumerable<Record> FindDocByDomain(string domain)
        {
            return collection.Find(Query.EQ("Domain", new BsonValue(domain)));
        }

        private Record GetFirstOrNull(uint ip, IEnumerable<Record> docs, out bool multipleItems)
        {
            Record doc;
            using (var e = docs.GetEnumerator()) {
                if (e.MoveNext()) {
                    doc = e.Current;
                    if (e.MoveNext()) {
                        multipleItems = true;
                        return doc;
                    }
                } else {
                    doc = null;
                }
            }
            multipleItems = false;
            return doc;
        }

        public class Record
        {
            public int Id { get; set; }
            public string Domain { get; set; }
            public int[] Ips { get; set; }
            public int[] OldIps { get; set; }
            public DateTime Date { get; set; }
            public DateTime Expire { get; set; }

            public override string ToString()
            {
                return $"{{Id={Id}, Domain={Domain}, Ips={string.Join("|", Ips)}, OldIps={string.Join("|", OldIps)}, Date={Date}, Expire={Expire}}}";
            }
        }
    }
}
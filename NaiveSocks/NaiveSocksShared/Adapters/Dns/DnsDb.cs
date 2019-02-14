using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using LiteDB;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class DnsDb : ICacheReverseDns, ICacheDns
    {
        public Naive.HttpSvr.Logger Logger;

        LiteDatabase liteDb;
        LiteEngine engine => liteDb.Engine;
        LiteCollection<BsonDocument> cfgCollection;

        const string ColRecords = "dns_records_v3";
        const int LatestVersion = 3;

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
        }

        public void Init()
        {
            Logger?.info("initializing...");
            bool retrying = false;
            BEGIN:
            try {
                liteDb = new LiteDatabase(FilePath);
                cfgCollection = liteDb.GetCollection("meta");

                CheckVersion();

                engine.EnsureIndex(ColRecords, "idx_ips", "$.Ips[*]", false);
                engine.EnsureIndex(ColRecords, "idx_oldips", "$.OldIps[*]", false);
                Logger?.info($"{engine.Count(ColRecords)} records.");
                CheckShrink();
            } catch (Exception e) {
                Logger?.exception(e, Logging.Level.Error, "failed to initialize");
                if (!retrying) {
                    retrying = true;
                    Logger?.info("delete current db file and retry...");
                    liteDb.Dispose();
                    System.IO.File.Delete(FilePath);
                    goto BEGIN;
                } else {
                    throw;
                }
            }
        }

        private void CheckVersion()
        {
            BsonValue ver = GetConfigValue("Version");
            if (ver == null && !liteDb.CollectionExists("dns_records")) {
                // it's a new database
                ver = LatestVersion;
                SetConfigValue("Version", ver);
                return;
            }
            if (ver == null) {
                Logger?.info("upgrading from v0 to v1...");
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
            }
            if (ver == 1) {
                Logger?.info("upgrading from v1 to v2...");
                var col = liteDb.GetCollection("dns_records_v1");
                col.DropIndex("Ips");
                liteDb.RenameCollection("dns_records_v1", "dns_records_v2");
                ver = 2;
                SetConfigValue("Version", ver);
            }
            if (ver == 2) {
                Logger?.info("upgrading from v2 to v3...");
                var oldcol = "dns_records_v2";
                var newcol = "dns_records_v3";
                engine.DropCollection(newcol); // in case there was unfinished upgrading
                var newdocs = engine.FindAll(oldcol).Select(x => {
                    x["_id"] = x["Domain"];
                    x.Remove("Domain");
                    return x;
                });
                engine.InsertBulk(newcol, newdocs, autoId: BsonType.Null);
                ver = 3;
                SetConfigValue("Version", ver);
                engine.DropCollection(oldcol);
                Logger?.info("finished upgrade.");
            }
            if (ver != LatestVersion) {
                throw new Exception("db version " + ver.ToString() + " is not supported.");
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
            Logger?.info("last shrink: " + (lastShrink?.AsDateTime.ToString() ?? "(null)"));
            if (lastShrink == null || DateTime.Now - lastShrink.AsDateTime > TimeSpan.FromDays(1)) {
                SetConfigValue(ls, DateTime.Now);
                Clean(DateTime.Now.AddDays(-3));
                Shrink();
            }
        }

        public int RecordCount() => (int)engine.Count(ColRecords);

        public long Shrink()
        {
            Logger?.info("start shrinking...");
            var reduced = liteDb.Shrink();
            if (reduced > 0) {
                Logger?.info("shrinked and reduced " + reduced + " bytes.");
            } else if (reduced < 0) {
                Logger?.info("shrinked and \"reduced\" " + reduced + " bytes.");
            } else {
                Logger?.info("shrinked and nothing happended.");
            }
            return reduced;
        }

        public void Set(string domain, IpRecord val)
        {
            lock (syncRoot) {
                var begin = Logging.getRuntime();
                inserts++;
                var doc = FindDocByDomain(domain).SingleOrDefault();
                Record r = null;
                if (doc != null)
                    r = Record.FromDocument(doc);
                else
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
                engine.Upsert(ColRecords, r.ToDocument(), BsonType.Int32);
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
                        domainsSb.Append(item["_id"].AsString);
                        if (r.Expire < item["Expire"].AsDateTime) {
                            r = Record.FromDocument(item);
                        }
                    }
                    Logger?.warning($"multiple domains ({domainsSb}) resolve to a ip address ({new IPAddress(ip)}).");
                } else if (r == null) {
                    docs = FindDocByOldIp(ip);
                    r = GetFirstOrNull(ip, docs, out m);
                    if (m) {
                        var domainsSb = new StringBuilder();
                        foreach (var item in docs) {
                            if (domainsSb.Length != 0)
                                domainsSb.Append('|');
                            domainsSb.Append(item["_id"].AsString);
                            if (r.Expire < item["Expire"].AsDateTime) {
                                r = Record.FromDocument(item);
                            }
                        }
                        Logger?.warning($"multiple domains ({domainsSb}) were (but not now) resolving to a ip address ({new IPAddress(ip)}).");
                    } else if (r != null) {
                        Logger?.warning($"domain ({r.Domain}) was (but not now) resolving to ip ({new IPAddress(ip)}).");
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
                var doc = FindDocByDomain(domain).SingleOrDefault();
                if (doc != null) {
                    var r = Record.FromDocument(doc);
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
                return engine.Delete(ColRecords, r["_id"]);
            }
        }

        public int Clean(DateTime expiredBefore)
        {
            Logger?.info($"deleting records expired before {expiredBefore}...");
            var r = engine.Delete(ColRecords, Query.LT("Expire", expiredBefore));
            Logger?.info($"deleted {r}.");
            return r;
        }

        private IEnumerable<BsonDocument> FindDocByIp(uint ip)
        {
            int ipInt = (int)ip;
            return engine.Find(ColRecords, Query.EQ("idx_ips", ipInt));
        }

        private IEnumerable<BsonDocument> FindDocByOldIp(uint ip)
        {
            int ipInt = (int)ip;
            return engine.Find(ColRecords, Query.EQ("idx_oldips", ipInt));
        }

        private IEnumerable<BsonDocument> FindDocByDomain(string domain)
        {
            return engine.Find(ColRecords, Query.EQ("_id", new BsonValue(domain)));
        }

        private Record GetFirstOrNull(uint ip, IEnumerable<BsonDocument> docs, out bool multipleItems)
        {
            Record doc;
            using (var e = docs.GetEnumerator()) {
                if (e.MoveNext()) {
                    doc = Record.FromDocument(e.Current);
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
            // Domain is primary key
            public string Domain { get; set; }
            public int[] Ips { get; set; }
            public int[] OldIps { get; set; }
            public DateTime Date { get; set; }
            public DateTime Expire { get; set; }

            public override string ToString()
            {
                return $"{{Domain={Domain}, Ips={string.Join("|", Ips)}, OldIps={string.Join("|", OldIps)}, Date={Date}, Expire={Expire}}}";
            }

            public static Record FromDocument(BsonDocument doc)
            {
                var r = new Record();
                if (doc.TryGetValue("_id", out var id)) r.Domain = id.AsString;
                if (doc.TryGetValue("Ips", out var ips)) r.Ips = ips.AsArray.Select(x => x.AsInt32).ToArray();
                if (doc.TryGetValue("OldIps", out var oldips)) r.OldIps = oldips.AsArray.Select(x => x.AsInt32).ToArray();
                if (doc.TryGetValue("Date", out var date)) r.Date = date.AsDateTime;
                if (doc.TryGetValue("Expire", out var expire)) r.Expire = expire.AsDateTime;
                return r;
            }

            public BsonDocument ToDocument()
            {
                var doc = new BsonDocument();
                doc.Add("_id", Domain);
                if (Ips != null) doc.Add("Ips", new BsonArray(Ips.Select(x => new BsonValue(x))));
                if (OldIps != null) doc.Add("OldIps", new BsonArray(OldIps.Select(x => new BsonValue(x))));
                doc.Add("Date", Date);
                doc.Add("Expire", Expire);
                return doc;
            }
        }
    }
}
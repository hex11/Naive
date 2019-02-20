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
    public class DnsDb : ICacheDns
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
                engine.EnsureIndex(ColRecords, "idx_ips6", "$.Ips6[*]", false);
                engine.EnsureIndex(ColRecords, "idx_oldips6", "$.OldIps6[*]", false);
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

        public void Set(string domain, ref IpRecord val)
        {
            lock (syncRoot) {
                var begin = Logging.getRuntime();
                inserts++;
                var doc = FindDocByDomain(domain).SingleOrDefault();
                Record r = null;
                if (doc != null) r = Record.FromDocument(doc);
                else r = new Record() { Domain = domain };

                // update IPv4/v6 records separately
                if (val.ipLongs != null) {
                    if (r.Ips != null && r.Ips.Length > 0) {
                        if (r.OldIps != null) {
                            r.OldIps = r.OldIps.Union(r.Ips).Except(val.ipLongs).ToArray();
                        } else {
                            r.OldIps = r.Ips.Except(val.ipLongs).ToArray();
                            if (r.OldIps.Length == 0) r.OldIps = null;
                        }
                    }
                    r.Ips = val.ipLongs;
                    r.Date = DateTime.Now;
                    r.Expire = val.expire;
                }
                if (val.ips6 != null) {
                    if (r.Ips6 != null && r.Ips6.Length > 0) {
                        var ec = Ip6.EqualityComparer;
                        if (r.OldIps6 != null) {
                            r.OldIps6 = r.OldIps6.Union(r.Ips6, ec).Except(val.ips6, ec).ToArray();
                        } else {
                            r.OldIps6 = r.Ips6.Except(val.ips6, ec).ToArray();
                            if (r.OldIps6.Length == 0) r.OldIps6 = null;
                        }
                    }
                    r.Ips6 = val.ips6;
                    r.Date6 = DateTime.Now;
                    r.Expire6 = val.expire6;
                }

                engine.Upsert(ColRecords, r.ToDocument(), BsonType.Int32);
                insertTotalTime += (int)(Logging.getRuntime() - begin);
            }
        }

        public string QueryByIp(uint ip)
        {
            return QueryByIpCore(ip, FindDocByIp, x => new IPAddress(x));
        }

        public string QueryByIp6(Ip6 ip)
        {
            return QueryByIpCore(ip, FindDocByIp6, x => x.ToIPAddress());
        }

        private string QueryByIpCore<T>(T ip, Func<T, bool, IEnumerable<BsonDocument>> getDocs, Func<T, IPAddress> getIp)
        {
            Interlocked.Increment(ref queryByIps);
            var begin = Logging.getRuntime();
            try {
                var r = GetFirstOrNull(getDocs(ip, false), out var enumerator);
                if (enumerator != null) {
                    HandleMultipleItems(enumerator, ref r, out var domains);
                    Logger?.warning($"multiple domains ({domains}) are resolved to ip ({getIp(ip)}).");
                } else if (r == null) {
                    r = GetFirstOrNull(getDocs(ip, true), out enumerator);
                    if (enumerator != null) {
                        HandleMultipleItems(enumerator, ref r, out var domains);
                        Logger?.warning($"multiple domains ({domains}) were (but not now) resolved to ip ({getIp(ip)}).");
                    } else if (r != null) {
                        Logger?.warning($"domain ({r.Domain}) was (but not now) resolved to ip ({getIp(ip)}).");
                    }
                }
                return r?.Domain;
            } finally {
                Interlocked.Add(ref queryByIpTotalTime, (int)(Logging.getRuntime() - begin));
            }
        }

        private static void HandleMultipleItems(IEnumerator<BsonDocument> docs, ref Record r, out string domainList)
        {
            // current enumerator index = 1
            var domains = new List<string>();
            int selected = 0;
            DateTime selectedExpire = r.Expire;
            BsonDocument selectedDoc = null;
            domains.Add(r.Domain);
            do {
                var item = docs.Current;
                DateTime newExpire = item["Expire"].AsDateTime;
                if (selectedExpire < newExpire) {
                    selectedDoc = item;
                    selectedExpire = newExpire;
                    selected = domains.Count;
                }
                domains.Add(item["_id"].AsString);
            } while (docs.MoveNext());
            docs.Dispose();
            if (selectedDoc != null) r = Record.FromDocument(selectedDoc);

            var sb = new StringBuilder();
            for (int i = 0; i < domains.Count; i++) {
                if (i != 0) sb.Append('|');
                if (i == selected) {
                    sb.Append('[').Append(domains[i]).Append(']');
                } else {
                    sb.Append(domains[i]);
                }
            }
            domainList = sb.ToString();
        }

        public bool QueryByName(string domain, out IpRecord val)
        {
            Interlocked.Increment(ref queryByDomains);
            var begin = Logging.getRuntime();
            try {
                var doc = FindDocByDomain(domain).SingleOrDefault();
                if (doc != null) {
                    var r = Record.FromDocument(doc);
                    val = new IpRecord();
                    val.ipLongs = r.Ips;
                    val.ips6 = r.Ips6;
                    val.expire = r.Expire;
                    val.expire6 = r.Expire6;
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

        private IEnumerable<BsonDocument> FindDocByIp(uint ip, bool old)
        {
            return engine.Find(ColRecords, Query.EQ(old ? "idx_oldips" : "idx_ips", (int)ip));
        }

        private IEnumerable<BsonDocument> FindDocByIp6(Ip6 ip, bool old)
        {
            return engine.Find(ColRecords, Query.EQ(old ? "idx_oldips6" : "idx_ips6", ip.ToBytes()));
        }

        private IEnumerable<BsonDocument> FindDocByDomain(string domain)
        {
            return engine.Find(ColRecords, Query.EQ("_id", new BsonValue(domain)));
        }

        private Record GetFirstOrNull(IEnumerable<BsonDocument> docs, out IEnumerator<BsonDocument> enumerator)
        {
            Record doc;
            var e = docs.GetEnumerator();
            if (e.MoveNext()) {
                doc = Record.FromDocument(e.Current);
                if (e.MoveNext()) {
                    enumerator = e;
                    return doc;
                }
            } else {
                doc = null;
            }
            enumerator = null;
            return doc;
        }

        public class Record
        {
            // Domain is primary key
            public string Domain { get; set; }

            public uint[] Ips { get; set; }
            public uint[] OldIps { get; set; }
            public DateTime Date { get; set; } = DateTime.MinValue;
            public DateTime Expire { get; set; } = DateTime.MinValue;

            public Ip6[] Ips6 { get; set; }
            public Ip6[] OldIps6 { get; set; }
            public DateTime Date6 { get; set; } = DateTime.MinValue;
            public DateTime Expire6 { get; set; } = DateTime.MinValue;

            public override string ToString()
            {
                return $"{{Domain={Domain}, Ips={string.Join("|", Ips)}, OldIps={string.Join("|", OldIps)}, Date={Date}, Expire={Expire}}}";
            }

            public static Record FromDocument(BsonDocument doc)
            {
                var r = new Record();
                if (doc.TryGetValue("_id", out var id)) r.Domain = id.AsString;
                if (doc.TryGetValue("Ips", out var ips)) r.Ips = ips.AsArray.Select(x => (uint)x.AsInt32).ToArray();
                if (doc.TryGetValue("OldIps", out var oldips)) r.OldIps = oldips.AsArray.Select(x => (uint)x.AsInt32).ToArray();
                r.Date = TryGetDateTime(doc, "Date");
                r.Expire = TryGetDateTime(doc, "Expire");
                if (doc.TryGetValue("Ips6", out var ips6)) r.Ips6 = ips6.AsArray.Select(x => new Ip6(x.AsBinary)).ToArray();
                if (doc.TryGetValue("OldIps6", out var oldips6)) r.OldIps6 = oldips6.AsArray.Select(x => new Ip6(x.AsBinary)).ToArray();
                r.Date6 = TryGetDateTime(doc, "Date6");
                r.Expire6 = TryGetDateTime(doc, "Expire6");
                return r;
            }

            static DateTime TryGetDateTime(BsonDocument doc, string key)
            {
                if (doc.TryGetValue(key, out var dt)) return dt.AsDateTime;
                else return DateTime.MinValue;
            }

            public BsonDocument ToDocument()
            {
                var doc = new BsonDocument();
                doc.Add("_id", Domain);
                if (Ips != null) doc.Add("Ips", new BsonArray(Ips.Select(x => new BsonValue((int)x))));
                if (OldIps != null) doc.Add("OldIps", new BsonArray(OldIps.Select(x => new BsonValue((int)x))));
                if (Date != DateTime.MinValue) doc.Add("Date", Date);
                if (Expire != DateTime.MinValue) doc.Add("Expire", Expire);
                if (Ips6 != null) doc.Add("Ips6", new BsonArray(Ips6.Select(x => new BsonValue(x.ToBytes()))));
                if (OldIps6 != null) doc.Add("OldIps6", new BsonArray(OldIps6.Select(x => new BsonValue(x.ToBytes()))));
                if (Date6 != DateTime.MinValue) doc.Add("Date6", Date6);
                if (Expire6 != DateTime.MinValue) doc.Add("Expire6", Expire6);
                return doc;
            }
        }
    }
}
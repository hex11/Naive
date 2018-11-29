using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using Android.Views;
using LiteDB;
using Naive.HttpSvr;

namespace NaiveSocksAndroid
{
    partial class VpnHelper
    {
        partial class LocalDns
        {
            class DnsDb : ICacheReverseDns, ICacheDns
            {
                LiteDatabase liteDb;
                LiteCollection<Record> collection;
                LiteCollection<BsonDocument> cfgCollection;

                object syncRoot = new object();

                public DnsDb(string dbPath)
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
                    if (lastShrink == null || DateTime.Now - lastShrink.AsDateTime > TimeSpan.FromDays(1)) {
                        SetConfigValue(ls, DateTime.Now);
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
                        r.Date = DateTime.Now;
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
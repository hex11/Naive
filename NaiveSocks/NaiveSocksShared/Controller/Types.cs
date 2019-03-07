using Nett;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NaiveSocks
{
    public class Types
    {
        public static Types Current = new Types();

        public Types()
        {
            RegisterBuiltInTypes();
        }

        public Dictionary<string, Type> RegisteredInTypes = new Dictionary<string, Type>();
        public Dictionary<string, Type> RegisteredOutTypes = new Dictionary<string, Type>();
        public Dictionary<string, Func<TomlTable, InAdapter>> RegisteredInCreators = new Dictionary<string, Func<TomlTable, InAdapter>>();
        public Dictionary<string, Func<TomlTable, OutAdapter>> RegisteredOutCreators = new Dictionary<string, Func<TomlTable, OutAdapter>>();

        private void RegisterBuiltInTypes()
        {
            RegisteredInTypes.Add("direct", typeof(DirectInAdapter));
            RegisteredInTypes.Add("tproxy", typeof(TProxyInAdapter));
            RegisteredInTypes.Add("socks", typeof(SocksInAdapter));
            RegisteredInTypes.Add("socks5", typeof(SocksInAdapter));
            RegisteredInTypes.Add("http", typeof(HttpInAdapter));
            RegisteredInTypes.Add("tlssni", typeof(TlsSniInAdapter));
            RegisteredInTypes.Add("naive", typeof(NaiveMInAdapter));
            RegisteredInTypes.Add("naivec", typeof(NaiveMInAdapter));
            RegisteredInTypes.Add("naive0", typeof(Naive0InAdapter));
            RegisteredInTypes.Add("ss", typeof(SsInAdapter));
            RegisteredInTypes.Add("dns", typeof(DnsInAdapter));
            RegisteredInTypes.Add("udprelay", typeof(UdpRelay));

            RegisteredOutTypes.Add("direct", typeof(DirectOutAdapter));
            RegisteredOutTypes.Add("socks", typeof(SocksOutAdapter));
            RegisteredOutTypes.Add("socks5", typeof(SocksOutAdapter));
            RegisteredOutTypes.Add("http", typeof(HttpOutAdapter));
            RegisteredOutTypes.Add("naive", typeof(NaiveMOutAdapter));
            RegisteredOutTypes.Add("naivec", typeof(NaiveMOutAdapter));
            RegisteredOutTypes.Add("naive0", typeof(Naive0OutAdapter));
            RegisteredOutTypes.Add("ss", typeof(SsOutAdapter));
            RegisteredOutTypes.Add("dns", typeof(DnsOutAdapter));
            RegisteredOutTypes.Add("webcon", typeof(WebConAdapter));
            RegisteredOutTypes.Add("webfile", typeof(WebFileAdapter));
            RegisteredOutTypes.Add("webtest", typeof(WebTestAdapter));

            RegisteredOutTypes.Add("router", typeof(RouterAdapter));
            RegisteredOutTypes.Add("fail", typeof(FailAdapter));
            RegisteredOutTypes.Add("nnetwork", typeof(NNetworkAdapter));
        }

        public void GenerateDocument(TextWriter tw)
        {
            List<Type> types = new List<Type>();
            foreach (var item in RegisteredInTypes.Keys.Union(RegisteredOutTypes.Keys)) {
                {
                    if (RegisteredInTypes.TryGetValue(item, out var type)) {
                        if (types.Contains(type)) {
                            tw.WriteLine($"[InAdatper alias '{item}' - {type.Name}]");
                        } else {
                            types.Add(type);
                            tw.WriteLine($"[InAdatper type '{item}' - {type.Name}]");
                            GenerateDocument(tw, type);
                        }
                        tw.WriteLine();
                    }
                }
                {
                    if (RegisteredOutTypes.TryGetValue(item, out var type)) {
                        if (types.Contains(type)) {
                            tw.WriteLine($"[OutAdatper alias '{item}' - {type.Name}]");
                        } else {
                            types.Add(type);
                            tw.WriteLine($"[OutAdatper type '{item}' - {type.Name}]");
                            GenerateDocument(tw, type);
                        }
                        tw.WriteLine();
                    }
                }
            }
        }

        public void GenerateDocument(TextWriter tw, Type type)
        {
            var props = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            foreach (var item in props) {
                if (item.CanWrite && item.GetCustomAttributes(typeof(NotConfAttribute), false).Any() == false) {
                    tw.WriteLine($"({item.PropertyType.Name})\t{item.Name}");
                }
            }
        }
    }
}

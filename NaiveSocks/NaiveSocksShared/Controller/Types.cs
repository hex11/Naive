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

        public Dictionary<string, Type> RegisteredTypes = new Dictionary<string, Type>();

        public Dictionary<string, Type> RegisteredInTypes = new Dictionary<string, Type>();
        public Dictionary<string, Type> RegisteredOutTypes = new Dictionary<string, Type>();
        public Dictionary<string, Func<TomlTable, InAdapter>> RegisteredInCreators = new Dictionary<string, Func<TomlTable, InAdapter>>();
        public Dictionary<string, Func<TomlTable, OutAdapter>> RegisteredOutCreators = new Dictionary<string, Func<TomlTable, OutAdapter>>();

        private void RegisterBuiltInTypes()
        {
            AddInType("direct", typeof(DirectInAdapter));
            AddOutType("direct", typeof(DirectOutAdapter));

            AddInType("tproxy", typeof(TProxyInAdapter));

            AddOutType("socks", typeof(SocksOutAdapter));
            AddOutType("socks5", typeof(SocksOutAdapter));
            AddInType("socks", typeof(SocksInAdapter));
            AddInType("socks5", typeof(SocksInAdapter));

            AddOutType("http", typeof(HttpOutAdapter));
            AddInType("http", typeof(HttpInAdapter));

            AddInType("tlssni", typeof(TlsSniInAdapter));

            AddOutType("naive", typeof(NaiveMOutAdapter));
            AddOutType("naivec", typeof(NaiveMOutAdapter));
            AddOutType("naive0", typeof(Naive0OutAdapter));
            AddInType("naive", typeof(NaiveMInAdapter));
            AddInType("naivec", typeof(NaiveMInAdapter));
            AddInType("naive0", typeof(Naive0InAdapter));

            AddOutType("ss", typeof(SsOutAdapter));
            AddInType("ss", typeof(SsInAdapter));

            AddOutType("dns", typeof(DnsOutAdapter));
            AddInType("dns", typeof(DnsInAdapter));

            AddOutType("webcon", typeof(WebConAdapter));
            AddOutType("webfile", typeof(WebFileAdapter));
            AddOutType("webtest", typeof(WebTestAdapter));
            AddOutType("webauth", typeof(WebAuthAdapter));

            AddInType("udprelay", typeof(UdpRelay));

            RegisteredTypes.Add("router", typeof(RouterAdapter));
            RegisteredTypes.Add("fail", typeof(FailAdapter));
            RegisteredTypes.Add("nnetwork", typeof(NNetworkAdapter));
        }

        void AddInType(string name, Type type)
        {
            RegisteredInTypes.Add(name, type);
            RegisteredTypes.Add(name + "-in", type);
        }

        void AddOutType(string name, Type type)
        {
            RegisteredOutTypes.Add(name, type);
            RegisteredTypes.Add(name + "-out", type);
        }

        public void GenerateDocument(TextWriter tw)
        {
            List<Type> types = new List<Type>();
            foreach (var item in RegisteredTypes) {
                var name = item.Key;
                var type = item.Value;
                if (types.Contains(type)) {
                    tw.WriteLine($"[Adatper alias '{name}' - {type.Name}]");
                } else {
                    types.Add(type);
                    tw.WriteLine($"[Adatper type '{name}' - {type.Name}]");
                    GenerateDocument(tw, type);
                }
                tw.WriteLine();
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

using Naive.Console;
using Nett;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        public void GenerateDocument(CmdConsole tw)
        {
            var types = from x in RegisteredTypes.Keys group x by RegisteredTypes[x];
            var othertypes = new List<Type>();
            foreach (var item in types) {
                var type = item.Key;
                tw.Write($"[Adatper type {type.Name} - '{string.Join("\', \'", item)}']\n", ConsoleColor.White);
                GenerateDocument(tw, type, othertypes);
                tw.WriteLine("");
            }
            foreach (var type in othertypes) {
                tw.Write($"[type {type.Name}]\n", ConsoleColor.White);
                GenerateDocument(tw, type, othertypes);
                tw.WriteLine("");
            }
        }

        private static void GenerateDocument(CmdConsole tw, Type type, List<Type> othertypes)
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props) {
                if (prop.CanWrite && prop.GetCustomAttributes(typeof(NotConfAttribute), false).Any() == false) {
                    PrintProperty(tw, prop);
                    var propType = prop.PropertyType;
                    if (othertypes != null) {
                        CheckType(othertypes, propType);
                    }
                }
            }
        }

        private static void CheckType(List<Type> othertypes, Type type, int depth = 5)
        {
            if (type.GetCustomAttributes(typeof(ConfTypeAttribute), false).Any()) {
                if (othertypes.Contains(type) == false) {
                    othertypes.Add(type);
                }
            }
            if (depth < 0) return;
            if (type.IsGenericType) {
                foreach (var item in type.GenericTypeArguments) {
                    CheckType(othertypes, item, depth - 1);
                }
            }
            if (type.HasElementType) {
                CheckType(othertypes, type.GetElementType(), depth - 1);
            }
        }

        public void GenerateDocument(CmdConsole tw, Type type)
        {
            GenerateDocument(tw, type, null);
        }

        private static void PrintProperty(CmdConsole tw, PropertyInfo item)
        {
            tw.Write($"  {item.Name,-18}  ");
            tw.Write(TypeToString(item.PropertyType) + "\n", ConsoleColor.Cyan);
        }

        private static string TypeToString(Type type)
        {
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "long";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";

            if (type.IsGenericType) {
                string name;
                if (type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) {
                    name = "Dict";
                } else {
                    name = type.Name;
                    name = name.Substring(0, name.IndexOf('`'));
                }
                var sb = new StringBuilder();
                sb.Append(name).Append('<');
                int i = 0;
                foreach (var item in type.GetGenericArguments()) {
                    if (i++ > 0) sb.Append(", ");
                    sb.Append(TypeToString(item));
                }
                sb.Append('>');
                return sb.ToString();
            }

            if (type.IsArray) {
                return TypeToString(type.GetElementType()) + "[]";
            }

            return type.Name;
        }
    }
}

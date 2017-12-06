using System.Threading.Tasks;
using Nett;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class SocksOutAdapter : OutAdapter2
    {
        public AddrPort server { get; set; }
        public string username { get; set; }
        public string password { get; set; }

        public override async Task<ConnectResult> ProtectedConnect(ConnectArgument arg)
        {
            var dest = arg.Dest;
            var socket = await Socks5Client.Connect(server.Host, server.Port,
                dest.Host, dest.Port, username, password);
            //await InConnection.SetConnectResult(ConnectResults.Conneceted, new IPEndPoint(0, 0));
            //await MyStream.FromSocket(Socket).RelayWith(InConnection.DataStream);
            return new ConnectResult(ConnectResults.Conneceted, MyStream.FromSocket(socket));
        }

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            if (toml.ContainsKey("socks"))
                server = toml.Get<AddrPort>("socks");
        }

        public override string ToString() => $"{{SocksOut server={server}}}";
    }
}

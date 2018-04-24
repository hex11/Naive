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
            var stream = await Socks5Client.Connect(server.Host, server.Port,
                dest.Host, dest.Port, username, password);
            return CreateConnectResultWithStream(stream);
        }

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            if (toml.ContainsKey("socks"))
                server = toml.Get<AddrPort>("socks");
        }

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("server", server);
        }
    }
}

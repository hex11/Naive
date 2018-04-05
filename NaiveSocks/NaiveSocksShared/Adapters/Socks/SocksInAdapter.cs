using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class SocksInAdapter : InAdapterWithListener
    {
        public bool fastopen { get; set; } = false;

        public override void OnNewConnection(TcpClient client)
        {
            new SocksInConnection(client, this).Start().Forget();
        }

        private class SocksInConnection : InConnection
        {
            private Socks5Server socks5svr;
            private readonly EPPair _eppair;
            private readonly SocksInAdapter _adapter;

            public SocksInConnection(TcpClient tcp, SocksInAdapter adapter) : base(adapter)
            {
                _eppair = EPPair.FromSocket(tcp.Client);
                _adapter = adapter;
                socks5svr = new Socks5Server(tcp);
                socks5svr.RequestingToConnect = async (s) => {
                    this.Dest.Host = s.TargetAddr;
                    this.Dest.Port = s.TargetPort;
                    this.DataStream = s.Stream;
                    if (adapter.fastopen)
                        await OnConnectionResult(new ConnectResult(ConnectResults.Conneceted)).CAF();
                    NaiveUtils.RunAsyncTask(async () => {
                        using (tcp) {
                            await _adapter.Controller.HandleInConnection(this);
                        }
                    }).Forget();
                };
            }

            public async Task Start()
            {
                await socks5svr.ProcessAsync();
            }

            protected override async Task OnConnectionResult(ConnectResult result)
            {
                if (socks5svr == null)
                    return;
                var tmp = socks5svr;
                socks5svr = null;
                await tmp.WriteConnectionResult(new IPEndPoint(0, Dest.Port), (result.Ok) ? Socks5Server.Rep.succeeded : Socks5Server.Rep.Connection_refused);
            }

            public override string GetInfoStr() => _eppair.ToString();
        }
    }
}

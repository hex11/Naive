using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System;
using System.Text;

namespace NaiveSocks
{
    public class DirectOutAdapter : OutAdapter2, IDnsProvider
    {
        public AddrPort force_dest { get; set; }
        public int connect_timeout { get; set; } = 10;

        public override Task<ConnectResult> ProtectedConnect(ConnectArgument arg)
        {
            var dest = force_dest.IsDefault ? arg.Dest : force_dest;
            return ConnectHelper.Connect(this, dest, connect_timeout);
        }

        public async Task<IPAddress[]> ResolveName(string name)
        {
            return await Dns.GetHostAddressesAsync(name);
        }

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            if(force_dest.IsDefault == false)
                ctx.AddField("force_dest", force_dest);
        }
    }

    public class ConnectHelper
    {
        public static async Task<ConnectResult> Connect(IAdapter adapter, AddrPort dest, int timeoutSeconds)
        {
            try {
                var socket = await NaiveUtils.ConnectTcpAsync(dest, timeoutSeconds * 1000);
                return new ConnectResult(adapter, MyStream.FromSocket(socket));
            } catch (Exception e) {
                return new ConnectResult(adapter, ConnectResultEnum.Failed) {
                    FailedReason = e.Message,
                    Exception = e
                };
            }
        }
    }
}

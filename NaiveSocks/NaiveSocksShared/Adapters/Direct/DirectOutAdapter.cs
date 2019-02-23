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

        public AdapterRef redirect_dns { get; set; }

        public override Task<ConnectResult> ProtectedConnect(ConnectArgument arg)
        {
            var dest = force_dest.IsDefault ? arg.Dest : force_dest;
            return ConnectHelper.Connect(this, arg.Dest, connect_timeout);
        }

        public Task<DnsResponse> ResolveName(DnsRequest req)
        {
            if (redirect_dns != null) return req.RedirectTo((IDnsProvider)redirect_dns.Adapter);
            return ResolveNameCore(req);
        }

        private static async Task<DnsResponse> ResolveNameCore(DnsRequest req)
        {
            return new DnsResponse { Addresses = await Dns.GetHostAddressesAsync(req.Name) };
        }

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            if (force_dest.IsDefault == false)
                ctx.AddField("force_dest", force_dest);
        }
    }

    public class ConnectHelper
    {
        public static async Task<ConnectResult> Connect(IAdapter adapter, AddrPort dest, int timeoutSeconds)
        {
            Socket socket;
            try {
                socket = await NaiveUtils.ConnectTcpAsync(dest, timeoutSeconds * 1000);
            } catch (Exception e) {
                return new ConnectResult(adapter, ConnectResultEnum.Failed) {
                    FailedReason = e.Message,
                    Exception = e
                };
            }
            return new ConnectResult(adapter, MyStream.FromSocket(socket, adapter.GetAdapter().socket_impl));
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System;

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

        public override string ToString() => "{DirectOut}";
    }

    public class ConnectHelper
    {
        public static async Task<ConnectResult> Connect(IAdapter adapter, AddrPort dest, int timeoutSeconds)
        {
            try {
                var socket = await NaiveUtils.ConnectTCPAsync(dest, timeoutSeconds * 1000);
                return new ConnectResult(MyStream.FromSocket(socket));
            } catch (Exception e) {
                return new ConnectResult(ConnectResults.Failed) {
                    FailedReason = e.Message,
                    Exception = e
                };
            }
        }
    }
}

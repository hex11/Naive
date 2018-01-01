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
            var addrs = await Dns.GetHostAddressesAsync(dest.Host);
            if (addrs.Length == 0)
                throw new Exception("no address resolved");
            var addr = addrs[0];
            var destTcp = new TcpClient(addr.AddressFamily);
            try {
                destTcp.NoDelay = true;
                var connectTask = destTcp.ConnectAsync(addr, dest.Port);
                if (timeoutSeconds > 0) {
                    if (await Task.WhenAny(connectTask, Task.Delay(timeoutSeconds * 1000)) != connectTask) {
                        destTcp.Close();
                        return new ConnectResult(ConnectResults.Failed) {
                            FailedReason = $"Connection timed out ({timeoutSeconds} seconds)"
                        };
                    }
                }
                await connectTask;
                return new ConnectResult(ConnectResults.Conneceted, MyStream.FromSocket(destTcp.Client));
            } catch (Exception) {
                destTcp.Close();
                throw;
            }
        }
    }
}

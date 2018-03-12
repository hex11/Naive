using System.Net;
using System.Net.Sockets;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class DirectInAdapter : InAdapterWithListener
    {
        public AddrPort dest { get; set; }

        public override string ToString() => $"{{DirectIn listen={listen} dest={dest}}}";

        public override void OnNewConnection(TcpClient tcpClient)
        {
            var epPair = EPPair.FromSocket(tcpClient.Client);
            var dataStream = MyStream.FromSocket(tcpClient.Client);
            var dest = this.dest.WithDefaultPort(listen.Port);
            Controller.HandleInConnection(InConnection.Create(this, dest, dataStream, epPair.ToString()));
        }
    }
}

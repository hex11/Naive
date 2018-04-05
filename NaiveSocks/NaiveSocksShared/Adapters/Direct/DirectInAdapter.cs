using System.Net;
using System.Net.Sockets;
using System.Text;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class DirectInAdapter : InAdapterWithListener
    {
        public AddrPort dest { get; set; }

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("dest", dest);
        }

        public override void OnNewConnection(TcpClient tcpClient)
        {
            var epPair = EPPair.FromSocket(tcpClient.Client);
            var dataStream = MyStream.FromSocket(tcpClient.Client);
            var dest = this.dest.WithDefaultPort(listen.Port);
            Controller.HandleInConnection(InConnection.Create(this, dest, dataStream, epPair.ToString()));
        }
    }
}

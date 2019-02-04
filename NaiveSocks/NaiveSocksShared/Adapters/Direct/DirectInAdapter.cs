using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Naive.HttpSvr;
using Nett;

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
            Socket socket = tcpClient.Client;
            var epPair = EPPair.FromSocket(socket);
            var dataStream = GetMyStreamFromSocket(socket);
            var dest = this.dest.WithDefaultPort(listen.Port);
            HandleIncommingConnection(InConnection.Create(this, dest, dataStream, epPair.ToString()));
        }
    }
}

using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace NaiveSocks
{
    class TlsSniInAdapter : InAdapterWithListener
    {
        public int dest_port { get; set; } = 443;

        public override async void OnNewConnection(TcpClient client)
        {
            var stream = GetMyStreamFromSocket(client.Client);
            try {
                var bs = BufferPool.GlobalGetBs(8 * 1024, false);
                var r = await stream.ReadAsyncR(bs);
                if (r <= 0)
                    return;
                bs.Len = r;
                var ch = new TlsStream.ClientHello();
                TlsStream.ParseClientHelloRecord(bs, ref ch, out _);
                if (ch.Sni == null)
                    return;
                var conn = InConnection.Create(this, new AddrPort(ch.Sni, dest_port), new MyStreamWrapper(stream) { Queue = bs });
                await HandleIncommingConnection(conn);
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "OnNewConnection");
            } finally {
                MyStream.CloseWithTimeout(stream).Forget();
            }
        }
    }
}

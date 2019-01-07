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
                ushort ver = 0;
                TlsStream.ParseClientHelloRecord(bs, ref ver, out var name);
                if (name == null)
                    return;
                var conn = InConnection.Create(this, new AddrPort(name, dest_port), new MyStreamWrapper(stream) { Queue = bs });
                await HandleIncommingConnection(conn);
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "OnNewConnection");
            } finally {
                MyStream.CloseWithTimeout(stream).Forget();
            }
        }
    }
}

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
            try {
                var stream = MyStream.FromSocket(client.Client);
                var bs = BufferPool.GlobalGetBs(8 * 1024, false);
                var r = await stream.ReadAsyncR(bs);
                if (r <= 0)
                    return;
                bs.Len = r;
                TlsStream.ParseClientHelloRecord(bs, out _, out var names);
                var name = names.Single();
                var conn = InConnection.Create(this, new AddrPort(name, dest_port), new MyStreamWrapperWithQueue(stream) { Queue = bs });
                HandleIncommingConnection(conn).Forget();
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "OnNewConnection");
            }
        }
    }
}

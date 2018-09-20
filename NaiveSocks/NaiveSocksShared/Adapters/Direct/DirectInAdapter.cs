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

        public bool tproxy { get; set; }

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            if (tproxy && Environment.OSVersion.Platform != PlatformID.Unix)
                throw new Exception("'tproxy' is only available on Unix.");
        }

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            if (tproxy) {
                ctx.AddTag("tproxy");
            } else {
                ctx.AddField("dest", dest);
            }
        }

        public override void OnNewConnection(TcpClient tcpClient)
        {
            Socket socket = tcpClient.Client;
            var epPair = EPPair.FromSocket(socket);
            var dataStream = GetMyStreamFromSocket(socket);
            AddrPort dest;
            if (tproxy) {
                dest = Unsafe.GetOriginalDst(socket, Logger);
            } else {
                dest = this.dest.WithDefaultPort(listen.Port);
            }
            Controller.HandleInConnection(InConnection.Create(this, dest, dataStream, epPair.ToString()));
        }

        private unsafe static class Unsafe
        {
            public const int SOL_IP = 0;

            public const int SO_ORIGINAL_DST = 80;

            [DllImport("libc")]
            public static extern int getsockopt(int sockfd, int level, int optname,
                          void* optval, int* optlen);

            public struct sockaddr_in
            {
                public short family;
                public ushort port;
                public int addr;

                private fixed byte _pad[16 - 2 - 2 - 4];
            }

            public static AddrPort GetOriginalDst(Socket socket, Logger logger)
            {
                AddrPort dest;
                Unsafe.sockaddr_in addr;
                int optLen = sizeof(Unsafe.sockaddr_in);
                var ret = Unsafe.getsockopt(socket.Handle.ToInt32(), Unsafe.SOL_IP, Unsafe.SO_ORIGINAL_DST, &addr, &optLen);
                var port = (ushort)(addr.port << 8) + (ushort)(addr.port >> 8);
                logger.debug($"family={addr.family} port={port} addr={addr.addr}");
                if (ret != 0)
                    throw new Exception("getsockopt returns " + ret);
                dest = new AddrPort(new IPAddress(addr.addr).ToString(), port);
                return dest;
            }
        }
    }
}

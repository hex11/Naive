using Naive.HttpSvr;
using Nett;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using static NaiveSocks.Socks5Server;

namespace NaiveSocks
{
    class Naive0InAdapter : InAdapterWithListener
    {
        public string key { get; set; }
        public int timeout { get; set; } = 10;

        public bool per_session_iv { get; set; } = true;

        public bool logging { get; set; }

        private Func<bool, IIVEncryptor> enc;

        public override string AdapterType => "Naive0In";

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            this.enc = SS.GetCipherByName("aes-256-ctr").GetEncryptorFunc(key);
        }

        public override void OnNewConnection(TcpClient client)
        {
            Handle(client).Forget();
        }

        private async Task Handle(TcpClient client)
        {
            try {
                SocketStream socketStream = MyStream.FromSocket(client.Client);
                var ws = new WebSocket(socketStream.ToStream(), false, true);
                if (timeout > 0)
                    ws.AddToManaged(timeout / 2, timeout);
                using (ws) {
                    var stream = new Naive0.Connection(ws, enc) { PerSessionIV = per_session_iv };
                    while (true) {
                        var s = stream.Open();
                        AddrPort dest;
                        try {
                            dest = await s.ReadHeader();
                        } catch (Exception) {
                            return;
                        }
                        if (logging) {
                            Logger.info($"{socketStream} dest={dest} used={stream.UsedCount}");
                        }
                        var conn = InConnection.Create(this, dest, s.AsMyStream);
                        await this.HandleIncommingConnection(conn);
                        if (!await s.TryShutdownForReuse())
                            break;
                    }
                }
            } catch (Exception e) {
                Logger.exception(e);
            }
        }

    }
}

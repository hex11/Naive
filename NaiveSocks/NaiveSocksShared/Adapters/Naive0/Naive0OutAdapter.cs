using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;
using Nett;

namespace NaiveSocks
{
    using Connection = Naive0.Connection;

    class Naive0OutAdapter : OutAdapter
    {
        public AddrPort server { get; set; }
        public int timeout { get; set; } = 10;
        public string key { get; set; }

        private Func<bool, IIVEncryptor> enc;

        Queue<Connection> pool = new Queue<Naive0.Connection>();

        public override string ToString() => $"{{Naive0Out server={server}}}";

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            this.enc = SS.GetCipherByName("aes-256-ctr").GetEncryptorFunc(key);
        }

        Connection GetStream()
        {
            lock (pool) {
                if (pool.Count > 0) {
                    return pool.Dequeue();
                } else {
                    return null;
                }
            }
        }

        async Task<Connection> NewStream()
        {
            var r = await ConnectHelper.Connect(this, server, timeout);
            r.ThrowIfFailed();
            var ws = new WebSocket(r.Stream.ToStream(), true);
            var stream = new Connection(ws, enc);
            return stream;
        }

        private async Task TryPut(Connection x)
        {
            try {
                if (!x.CanOpen) {
                    if (!await x.CurrentSession.TryShutdownForReuse())
                        return;
                }
            } catch (Exception) {
                x.ws.Close();
                return;
            }
            if (x.CanOpen) {
                lock (pool) {
                    pool.Enqueue(x);
                }
            } else {
                x.ws.Close();
            }
        }

        //public override async Task<ConnectResult> ProtectedConnect(ConnectArgument arg)
        //{
        //    var stream = GetStream() ?? await NewStream();
        //    await stream.OpenAndWriteHeader(arg.Dest);
        //    return new ConnectResult(stream.AsMyStream);
        //}

        public override async Task HandleConnection(InConnection connection)
        {
            var stream = GetStream() ?? await NewStream();
            stream.Open();
            var s = stream.CurrentSession;
            await s.WriteHeader(connection.Dest);
            await connection.RelayWith(s.AsMyStream);
            TryPut(stream).Forget();
        }
    }
}

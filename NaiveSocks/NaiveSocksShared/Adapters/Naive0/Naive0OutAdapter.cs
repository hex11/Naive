using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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

        public int min_free { get; set; } = 3;
        public int max_free { get; set; } = 10;

        public bool connect_on_start { get; set; } = false;

        public bool per_session_iv { get; set; } = true;

        private Func<bool, IIVEncryptor> enc;

        List<Connection> pool = new List<Connection>();

        int poolConnecting = 0;
        void EnterPutting() => Interlocked.Increment(ref poolConnecting);
        void ExitPutting() => Interlocked.Decrement(ref poolConnecting);

        int free => poolConnecting + pool.Count;

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("server", server);
        }

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            this.enc = Ss.GetCipherByName("aes-256-ctr").GetEncryptorFunc(key);
        }

        public override void Start()
        {
            base.Start();
            if (connect_on_start) {
                CheckPool();
            }
        }

        void CheckPool()
        {
            lock (pool) {
                var free = this.free;
                if (free < min_free) {
                    for (int i = 0; i < min_free - free; i++) {
                        NewConnectionIntoPool().Forget();
                    }
                }
            }
        }

        Connection TryGetConnection()
        {
            lock (pool) {
                if (pool.Count > 0) {
                    var x = pool[0];
                    pool.RemoveAt(0);
                    return x;
                } else {
                    return null;
                }
            }
        }

        async Task NewConnectionIntoPool()
        {
            Connection conn = null;
            try {
                EnterPutting();
                conn = await NewConnection();
            } finally {
                lock (pool) {
                    ExitPutting();
                    if (conn != null)
                        Put(conn);
                }
            }
        }

        async Task<Connection> NewConnection()
        {
            try {
                var r = await ConnectHelper.Connect(this, server, timeout);
                r.ThrowIfFailed();
                var ws = new WebSocket(r.Stream.ToStream(), true);
                if (timeout > 0)
                    ws.AddToManaged(timeout / 2, timeout);
                var conn = new Connection(ws, enc) { PerSessionIV = per_session_iv };
                ws.Closed += (_) => {
                    // when websocket is closed
                    lock (pool)
                        pool.Remove(conn); // remove from pool if it's in
                };
                return conn;
            } catch (Exception e) {
                Logger.error("connecting to server: " + e.Message);
                throw;
            }
        }

        void Put(Connection x)
        {
            if (IsRunning && free < max_free) {
                x.ws.StartPrereadForControlFrame();
                pool.Add(x);
            } else {
                x.Close();
            }
        }

        private async Task TryPut(Connection x)
        {
            try {
                EnterPutting();
                if (!x.CanOpen) {
                    if (!await x.CurrentSession.TryShutdownForReuse())
                        return;
                }
            } catch (Exception) {
                ExitPutting();
                x.Close();
                return;
            }
            lock (pool) {
                ExitPutting();
                if (x.CanOpen) {
                    Put(x);
                } else {
                    x.Close();
                }
            }
        }

        public override async Task HandleConnection(InConnection connection)
        {
            RETRY:
            CheckPool();
            bool isConnFromPool = true;
            var conn = TryGetConnection();
            if (conn == null) {
                isConnFromPool = false;
                conn = await NewConnection();
            }
            var usedCount = conn.UsedCount;
            Connection.SessionStream s;
            try {
                conn.Open();
                s = conn.CurrentSession;
                await s.WriteHeader(connection.Dest);
            } catch (Exception e) {
                Logger.error($"{(usedCount > 0 ? $"reusing({usedCount}) " : "")}" +
                    $"connection error{(isConnFromPool ? " (will retry)" : "")}:" +
                    $" {e.Message} on {conn.ws.BaseStream}");
                conn.Close();
                if (isConnFromPool)
                    goto RETRY;
                throw;
            }
            await connection.RelayWith(s.AsMyStream);
            TryPut(conn).Forget();
        }
    }
}

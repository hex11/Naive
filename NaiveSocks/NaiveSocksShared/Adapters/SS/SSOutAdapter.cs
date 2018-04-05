using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.IO;

namespace NaiveSocks
{
    public class SsOutAdapter : OutAdapter2
    {
        public AddrPort server { get; set; }
        public string key { get; set; }
        public string encryption { get; set; } = "aes-128-ctr";
        public int connect_timeout { get; set; } = 10;

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("server", server);
        }

        private Func<IMyStream, IMyStream> getEncryptionStream;

        public override void Start()
        {
            base.Start();
            getEncryptionStream = Ss.GetCipherByName(encryption).GetEncryptionStreamFunc(key);
        }

        public override async Task<ConnectResult> ProtectedConnect(ConnectArgument arg)
        {
            var dest = arg.Dest;
            var baseResult = await ConnectHelper.Connect(this, server, connect_timeout);
            if (!baseResult.Ok)
                return baseResult;
            try {
                var dataStream = getEncryptionStream(baseResult.Stream);
                var bytes = dest.ToSocks5Bytes();
                await dataStream.WriteAsync(bytes);
                return new ConnectResult(ConnectResults.Conneceted, dataStream);
            } catch (Exception) {
                MyStream.CloseWithTimeout(baseResult.Stream);
                throw;
            }
        }
    }
}

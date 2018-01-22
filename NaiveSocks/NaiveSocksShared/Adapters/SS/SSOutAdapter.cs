﻿using Naive.HttpSvr;
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
    public class SSOutAdapter : OutAdapter2
    {
        public AddrPort server { get; set; }
        public string key { get; set; }
        public string encryption { get; set; } = "aes-128-ctr";
        public int connect_timeout { get; set; } = 10;

        private Func<IMyStream, IMyStream> getEncryptionStream;

        public override void Start()
        {
            base.Start();
            getEncryptionStream = SS.GetCipherByName(encryption).GetEncryptionStreamFunc(key);
        }

        public override async Task<ConnectResult> ProtectedConnect(ConnectArgument arg)
        {
            var dest = arg.Dest;
            var baseResult = await ConnectHelper.Connect(this, server, connect_timeout);
            if (!baseResult.Ok)
                return baseResult;
            try {
                var dataStream = getEncryptionStream(baseResult.Stream);
                var bytes = new byte[1 + dest.BytesLength];
                var cur = 0;
                bytes[cur++] = (byte)Socks5Server.AddrType.DomainName;
                dest.ToBytes(bytes, ref cur);
                await dataStream.WriteAsync(bytes);
                return new ConnectResult(ConnectResults.Conneceted, dataStream);
            } catch (Exception) {
                MyStream.CloseWithTimeout(baseResult.Stream);
                throw;
            }
        }
    }
}
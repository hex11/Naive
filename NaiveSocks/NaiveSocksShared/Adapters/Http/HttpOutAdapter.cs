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
    public class HttpOutAdapter : OutAdapter2
    {
        public AddrPort server { get; set; } // default port is 80

        public int connect_timeout { get; set; } = 10;

        public override async Task<ConnectResult> ProtectedConnect(ConnectArgument arg)
        {
            var dest = arg.Dest;
            var baseResult = await ConnectHelper.Connect(this, server.WithDefaultPort(80), connect_timeout);
            if (!baseResult.Ok)
                return baseResult;
            try {
                var dataStream = baseResult.Stream;
                var asStream = MyStream.ToStream(dataStream);
                var sw = new StringWriter(new StringBuilder(1024));
                var destStr = dest.ToString();
                HttpClient.WriteHttpRequestHeader(sw, "CONNECT", destStr, new Dictionary<string, string> {
                    ["Host"] = destStr
                });
                await dataStream.WriteAsync(NaiveUtils.GetUTF8Bytes(sw.ToString()));
                var responseStr = await NaiveUtils.ReadStringUntil(asStream, NaiveUtils.DoubleCRLFBytes);
                var sr = new StringReader(responseStr);
                var response = HttpClient.ReadHttpResponseHeader(sr);
                if (response.StatusCode != "200") {
                    throw new Exception($"remote server response '{response.StatusCode} {response.ReasonPhrase}'");
                }
                return CreateConnectResultWithStream(dataStream);
            } catch (Exception) {
                MyStream.CloseWithTimeout(baseResult.Stream);
                throw;
            }
        }
    }
}

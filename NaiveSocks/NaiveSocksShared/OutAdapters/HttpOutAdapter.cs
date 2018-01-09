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

    public class HttpOutAdapter : OutAdapter2
    {
        public AddrPort server { get; set; }
        public int connect_timeout { get; set; } = 10;

        public override async Task<ConnectResult> ProtectedConnect(ConnectArgument arg)
        {
            var dest = arg.Dest;
            var baseResult = await ConnectHelper.Connect(this, server, connect_timeout);
            if (!baseResult.Ok)
                return baseResult;
            try {
                var dataStream = baseResult.Stream;
                var asStream = MyStream.ToStream(dataStream);
                var sw = new StreamWriter(asStream, NaiveUtils.UTF8Encoding, 1440, true);
                var destStr = dest.ToString();
                await HttpClient.WriteHttpRequestHeaderAsync(sw, "CONNECT", destStr, new Dictionary<string, string> {
                    ["Host"] = destStr
                });
                await sw.FlushAsync();
                sw.Dispose();
                var responseStr = await NaiveUtils.ReadStringUntil(asStream, NaiveUtils.DoubleCRLFBytes);
                var sr = new StringReader(responseStr);
                var response = HttpClient.ReadHttpResponseHeader(sr);
                if (response.StatusCode != "200") {
                    throw new Exception($"remote server returns response '{response.StatusCode} {response.ReasonPhrase}'");
                }
                return new ConnectResult(ConnectResults.Conneceted, dataStream);
            } catch (Exception) {
                MyStream.CloseWithTimeout(baseResult.Stream);
                throw;
            }
        }
    }
}

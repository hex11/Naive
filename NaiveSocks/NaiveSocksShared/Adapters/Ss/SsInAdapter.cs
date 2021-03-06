﻿using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks
{
    public class SsInAdapter : InAdapterWithListener
    {
        public string key { get; set; }
        private Func<IMyStream, IMyStream> getEncryptionStream;
        public string encryption { get; set; } = "aes-128-ctr";

        protected override void OnStart()
        {
            getEncryptionStream = Ss.GetCipherByName(encryption).GetEncryptionStreamFunc(key);
            base.OnStart();
        }

        public override async void OnNewConnection(TcpClient client)
        {
            try {
                using (client) {
                    var socket = client.Client;
                    var remoteEP = socket.RemoteEndPoint as IPEndPoint;
                    var dataStream = getEncryptionStream(GetMyStreamFromSocket(socket));
                    var buf = new BytesSegment(new byte[3]);
                    await dataStream.ReadFullAsyncR(buf).CAF(); // read ahead
                    var addrType = (Socks5Server.AddrType)buf[0];
                    string addrString = null;
                    switch (addrType) {
                        case Socks5Server.AddrType.IPv4Address:
                        case Socks5Server.AddrType.IPv6Address:
                            var buf2 = new byte[addrType == Socks5Server.AddrType.IPv4Address ? 4 : 16];
                            buf2[0] = buf[1];
                            buf2[1] = buf[2];
                            await dataStream.ReadFullAsyncR(new BytesSegment(buf2, 2, buf2.Length - 2)).CAF();
                            var ip = new IPAddress(buf2);
                            addrString = ip.ToString();
                            break;
                        case Socks5Server.AddrType.DomainName:
                            var length = buf[1];
                            if (length == 0) {
                                Logger.warning($"zero addr length ({remoteEP})");
                                await Task.Delay(10 * 1000).CAF();
                                return;
                            }
                            var dnBuf = new byte[length];
                            dnBuf[0] = buf[2];
                            await dataStream.ReadFullAsyncR(new BytesSegment(dnBuf, 1, length - 1)).CAF();
                            addrString = Encoding.ASCII.GetString(dnBuf, 0, length);
                            break;
                        default:
                            Logger.warning($"unknown addr type {addrType} ({remoteEP})");
                            await Task.Delay(10 * 1000 + NaiveUtils.Random.Next(20 * 1000)).CAF();
                            return;
                    }
                    await dataStream.ReadFullAsyncR(buf.Sub(0, 2)).CAF();
                    int port = buf[0] << 8 | buf[1];
                    var dest = new AddrPort(addrString, port);
                    await Controller.HandleInConnection(InConnectionTcp.Create(this, dest, dataStream, $"remote={remoteEP}")).CAF();
                }
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, "handling connection");
            }
        }
    }
}

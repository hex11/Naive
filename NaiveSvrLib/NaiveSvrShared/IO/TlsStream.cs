using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class TlsStream : MyStream.StreamWrapperBase
    {
        public IMyStream RealBaseStream => MyStreamWrapper.BaseStream;
        public SslStream SslStream { get; }

        public override Stream BaseStream => SslStream;

        public MyStreamWrapper MyStreamWrapper { get; }

        public override string ToString() => $"{{Tls on {RealBaseStream}}}";

        public TlsStream(IMyStream baseStream)
        {
            MyStreamWrapper = new MyStreamWrapper(baseStream);
            SslStream = new SslStream(MyStreamWrapper.ToStream(), false);
        }

        public Task AuthAsClient(string targetHost)
        {
            return AuthAsClient(targetHost, NaiveUtils.TlsProtocols);
        }

        public Task AuthAsClient(string targetHost, SslProtocols protocols)
        {
            return SslStream.AuthenticateAsClientAsync(targetHost, null, protocols, false);
        }

        public async Task<ClientHello> ReadClientHello()
        {
            var recordHeader = new BytesSegment(new byte[5]);
            await RealBaseStream.ReadFullAsyncR(recordHeader);
            MyStreamWrapper.Queue = recordHeader;
            ushort ver = 0;
            int recordPayloadLength = GetRecordPayloadLength(recordHeader, ref ver);

            var record = new BytesSegment(new byte[5 + recordPayloadLength]);
            recordHeader.CopyTo(record);
            var msg = record.Sub(5);
            await RealBaseStream.ReadFullAsyncR(msg);
            MyStreamWrapper.Queue = record;
            var ch = new ClientHello();
            ParseClientHello(msg, ref ch);
            return ch;
        }

        public struct ClientHello
        {
            public ushort Version;
            public string Sni;
            public string Alpn;
        }

        public struct ServerHello
        {
            public ushort Version;
            public bool SniUsed;
            public string Alpn;
        }

        public static void ParseClientHelloRecord(BytesSegment bs, ref ClientHello ch, out int size)
        {
            var payloadLen = GetRecordPayloadLength(bs, ref ch.Version);
            size = 5 + payloadLen;
            if (bs.Len < size)
                return;
            bs.SubSelf(5);
            bs.Len = Math.Min(bs.Len, payloadLen);
            ParseClientHello(bs, ref ch);
        }

        public static void ParseServerHelloRecord(BytesSegment bs, ref ServerHello ch, out int size)
        {
            var payloadLen = GetRecordPayloadLength(bs, ref ch.Version);
            size = 5 + payloadLen;
            if (bs.Len < size)
                return;
            bs.SubSelf(5);
            bs.Len = Math.Min(bs.Len, payloadLen);
            ParseServerHello(bs, ref ch);
        }

        private static int GetRecordPayloadLength(BytesSegment recordHeader, ref ushort ver)
        {
            if (recordHeader.Len < 5)
                throw new Exception("recordHeader length < 5");
            if (recordHeader[0] != 22) // content type: handshake
                throw new Exception("Expected handshake (22), got " + recordHeader[0]);
            var versionMajor = recordHeader[1];
            var versionMinor = recordHeader[2];
            if (versionMajor != 3)
                throw new Exception($"Not supported version ({versionMajor}, {versionMinor})");
            ver = (ushort)(versionMajor << 8 | versionMinor);
            return recordHeader[3] << 8 | recordHeader[4];
        }

        private static void ParseClientHello(BytesSegment msg, ref ClientHello ch)
        {
            var cur = 0;
            if (msg[cur] != 1)
                throw new Exception("Expected client hello (1), got " + msg[cur]);
            cur++;
            var msgLength = msg[cur] << 16 | msg[cur + 1] << 8 | msg[cur + 2]; cur += 3;
            msg.SubSelf(4); cur = 0;

            ch.Version = (ushort)(msg[cur] << 8 | msg[cur + 1]); cur += 2;

            cur += 32; // skip random
            cur += 1 + msg[cur]; // skip session_id
            cur += 2 + (msg[cur] << 8 | msg[cur + 1]); // skip cipher_suites
            cur += 1 + msg[cur]; // skip compression_methods
            if (cur >= msgLength)
                throw new Exception("extensionsBegin >= msgLength");

            var extensionsLength = msg[cur] << 8 | msg[cur + 1]; cur += 2;
            var extensionsEnd = cur + extensionsLength;
            if (extensionsEnd > msgLength)
                throw new Exception("extensionsEnd > msgLength");
            while (cur < extensionsEnd) {
                var extType = (msg[cur] << 8 | msg[cur + 1]); cur += 2;
                var extLen = (msg[cur] << 8 | msg[cur + 1]); cur += 2;
                var extEnd = cur + extLen;
                if (extEnd > extensionsEnd)
                    throw new Exception("extEnd > extensionsEnd");
                if (extType == 0) { // server_name
                    var nameListLen = (msg[cur] << 8 | msg[cur + 1]); cur += 2;
                    var nameListEnd = cur + nameListLen;
                    if (nameListEnd > extEnd)
                        throw new Exception("nameListEnd > extEnd");
                    var nameList = new List<string>();
                    if (cur < nameListEnd) { // read the first item only
                        if (msg[cur++] != 0) // name_type: host_name
                            throw new Exception("Not supported name type " + msg[cur]);
                        var nameLen = (msg[cur] << 8 | msg[cur + 1]); cur += 2;
                        if (cur + nameLen > nameListEnd)
                            throw new Exception("nameEnd > nameListEnd");
                        var str = NaiveUtils.UTF8Encoding.GetString(msg.Bytes, msg.Offset + cur, nameLen);
                        // TODO: check encoding
                        ch.Sni = str;
                    }
                } else if (extType == 16) { // ALPN
                    var listLen = (msg[cur] << 8 | msg[cur + 1]); cur += 2;
                    var listEnd = cur + listLen;
                    if (listEnd > extEnd)
                        throw new Exception("alpnListEnd > extEnd");
                    if (cur < listEnd) { // read the first item only
                        var strLen = msg[cur++];
                        if (cur + strLen > listEnd)
                            throw new Exception("alpnStrEnd > nameListEnd");
                        ch.Alpn = Encoding.ASCII.GetString(msg.Bytes, msg.Offset + cur, strLen);
                    }
                } else if (extType == 43) { // supported_versions
                    var listLen = msg[cur++];
                    if (listLen < 2)
                        throw new Exception("listLen < 2");
                    var listEnd = cur + listLen;
                    if (listEnd > extEnd)
                        throw new Exception("supported_versions listEnd > extEnd");
                    while (cur < listEnd) {
                        var ver = (ushort)(msg[cur] << 8 | msg[cur + 1]); cur += 2;
                        if (ver > ch.Version)
                            ch.Version = ver;
                    }
                }
                cur = extEnd;
            }
            return;
        }

        private static void ParseServerHello(BytesSegment msg, ref ServerHello hello)
        {
            var cur = 0;
            if (msg[cur] != 2)
                throw new Exception("Expected server hello (2), got " + msg[cur]);
            cur++;
            var msgLength = msg[cur] << 16 | msg[cur + 1] << 8 | msg[cur + 2]; cur += 3;
            msg.SubSelf(4); cur = 0;

            hello.Version = (ushort)(msg[cur] << 8 | msg[cur + 1]); cur += 2;

            cur += 32; // skip random
            cur += 1 + msg[cur]; // skip session_id
            cur += 2; // skip cipher suite
            cur += 1; // compression_methods
            if (cur >= msgLength)
                throw new Exception("extensionsBegin >= msgLength");

            var extensionsLength = msg[cur] << 8 | msg[cur + 1]; cur += 2;
            var extensionsEnd = cur + extensionsLength;
            if (extensionsEnd > msgLength)
                throw new Exception("extensionsEnd > msgLength");
            while (cur < extensionsEnd) {
                var extType = (msg[cur] << 8 | msg[cur + 1]); cur += 2;
                var extLen = (msg[cur] << 8 | msg[cur + 1]); cur += 2;
                var extEnd = cur + extLen;
                if (extEnd > extensionsEnd)
                    throw new Exception("extEnd > extensionsEnd");
                if (extType == 0) { // server_name
                    hello.SniUsed = true;
                } else if (extType == 16) { // ALPN
                    var listLen = (msg[cur] << 8 | msg[cur + 1]); cur += 2;
                    var listEnd = cur + listLen;
                    if (listEnd > extEnd)
                        throw new Exception("alpnListEnd > extEnd");
                    if (cur < listEnd) { // read the first item only
                        var strLen = msg[cur++];
                        if (cur + strLen > listEnd)
                            throw new Exception("alpnStrEnd > nameListEnd");
                        hello.Alpn = Encoding.ASCII.GetString(msg.Bytes, msg.Offset + cur, strLen);
                    }
                } else if (extType == 43) { // supported_versions
                    if (extLen != 2)
                        throw new Exception("supported_versions extLen != 2");
                    var ver = (ushort)(msg[cur] << 8 | msg[cur + 1]); cur += 2;
                    hello.Version = ver;
                }
                cur = extEnd;
            }
            return;
        }

        public Task AuthAsServer(System.Security.Cryptography.X509Certificates.X509Certificate certificate)
        {
            return SslStream.AuthenticateAsServerAsync(certificate);
        }
    }
}

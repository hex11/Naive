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
        public IMyStream RealBaseStream => streamWrapper.BaseStream;
        public SslStream SslStream { get; }

        public override Stream BaseStream => SslStream;

        MyStreamWrapperWithQueue streamWrapper;

        public override string ToString() => $"{{Tls on {RealBaseStream}}}";

        public TlsStream(IMyStream baseStream)
        {
            streamWrapper = new MyStreamWrapperWithQueue(baseStream);
            SslStream = new SslStream(streamWrapper.ToStream(), false);
        }

        public Task AuthAsClient(string targetHost)
        {
            return AuthAsClient(targetHost, NaiveUtils.TlsProtocols);
        }

        public Task AuthAsClient(string targetHost, SslProtocols protocols)
        {
            return SslStream.AuthenticateAsClientAsync(targetHost, null, protocols, false);
        }

        public async Task<string> GetSniAsServer()
        {
            var recordHeader = new BytesSegment(new byte[5]);
            await RealBaseStream.ReadFullAsync(recordHeader);
            streamWrapper.Queue = recordHeader;
            ushort ver = 0;
            int recordPayloadLength = GetRecordPayloadLength(recordHeader, ref ver);

            var record = new BytesSegment(new byte[5 + recordPayloadLength]);
            recordHeader.CopyTo(record);
            var msg = record.Sub(5);
            await RealBaseStream.ReadFullAsync(msg);
            streamWrapper.Queue = record;
            ushort ver2 = 0;
            ParseClientHello(msg, ref ver2, out var sni);
            return sni;
        }

        public static void ParseClientHelloRecord(BytesSegment bs, ref ushort ver, out string sni)
        {
            var payloadLen = GetRecordPayloadLength(bs, ref ver);
            bs.SubSelf(5);
            bs.Len = Math.Min(bs.Len, payloadLen);
            ParseClientHello(bs, ref ver, out sni);
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

        private static void ParseClientHello(BytesSegment msg, ref ushort version, out string sni)
        {
            var cur = 0;
            if (msg[cur++] != 1)
                throw new Exception("Expected client hello (1), got " + msg[cur]);
            var msgLength = msg[cur] << 16 | msg[cur + 1] << 8 | msg[cur + 2]; cur += 3;
            msg.SubSelf(4); cur = 0;

            version = (ushort)(msg[cur] << 8 | msg[cur + 1]); cur += 2;

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
                    while (cur < nameListEnd) {
                        if (msg[cur++] != 0) // name_type: host_name
                            throw new Exception("Not supported name type " + msg[cur]);
                        var nameLen = (msg[cur] << 8 | msg[cur + 1]); cur += 2;
                        if (cur + nameLen > nameListEnd)
                            throw new Exception("nameEnd > nameListEnd");
                        var str = NaiveUtils.UTF8Encoding.GetString(msg.Bytes, msg.Offset + cur, nameLen);
                        // TODO: check encoding
                        sni = str;
                        return;
                    }
                    sni = null;
                    return;
                }
                cur = extEnd;
            }
            sni = null;
            return;
        }

        public Task AuthAsServer(System.Security.Cryptography.X509Certificates.X509Certificate certificate)
        {
            return SslStream.AuthenticateAsServerAsync(certificate);
        }
    }
}

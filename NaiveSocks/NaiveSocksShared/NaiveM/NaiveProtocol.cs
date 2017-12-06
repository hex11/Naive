using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public partial class NaiveProtocol
    {
        private static Encoding Encoding => NaiveUtils.UTF8Encoding;
        private static Random rd = new Random();

        private const string CONNECT = "";
        private static readonly byte[] iv128 = NaiveUtils.UTF8Encoding.GetBytes("2333366666123456");
        public class Request
        {
            public AddrPort dest;
            public string additionalString = CONNECT;

            public Request() { }

            public Request(AddrPort dest)
            {
                this.dest = dest;
            }

            public Request(AddrPort dest, string addStr)
            {
                this.dest = dest;
                this.additionalString = addStr;
            }

            public byte[] ToBytes()
            {
                var len = 1 + dest.BytesLength + 1 + additionalString.BytesLength(Encoding);
                var buf = new byte[len];
                var c = 0;
                buf[c++] = (byte)((rd.Next() % 128) * 2);
                dest.ToBytes(buf, ref c);
                byte sum = 233;
                for (int i = 0; i < c; i++)
                    sum += buf[i];
                buf[c++] = sum;
                additionalString.ToBytes(Encoding, buf, ref c);
                return buf;
            }

            public static Request Parse(byte[] buf)
            {
                var offset = 0;
                return Parse(buf, ref offset);
            }

            public static Request Parse(byte[] buf, ref int c)
            {
                int begin = c;
                c++;
                var result = new Request(AddrPort.Parse(buf, ref c));
                byte sum = buf[c++];
                for (int i = begin; i < c - 1; i++)
                    sum -= buf[i];
                if (sum != 233)
                    throw new Exception("checksum failed");
                if (c < buf.Length)
                    result.additionalString = Pack.ParseString(Encoding, buf, ref c);
                return result;
            }
        }

        public class Reply
        {
            public AddrPort remoteEP;
            public byte status;
            public string additionalString = CONNECT;
            
            public Reply()
            {
            }

            public Reply(AddrPort remoteEP, byte status)
            {
                this.remoteEP = remoteEP;
                this.status = status;
            }

            public Reply(AddrPort remoteEP, byte status, string addStr)
            {
                this.remoteEP = remoteEP;
                this.status = status;
                this.additionalString = addStr;
            }

            public byte[] ToBytes()
            {
                var len = 1 + remoteEP.BytesLength + 1 + 1 + additionalString.BytesLength(Encoding);
                var buf = new byte[len];
                var c = 0;
                buf[c++] = 0;
                remoteEP.ToBytes(buf, ref c);
                buf[c++] = status;
                byte sum = 233;
                for (int i = 0; i < c; i++) {
                    sum += buf[i];
                }
                buf[c++] = sum;
                additionalString.ToBytes(Encoding, buf, ref c);
                return buf;
            }

            public static Reply Parse(byte[] buf)
            {
                var c = 0;
                var result = new Reply();
                c++;
                result.remoteEP = AddrPort.Parse(buf, ref c);
                result.status = buf[c++];
                byte sum = buf[c++];
                for (int i = 0; i < c - 1; i++)
                    sum -= buf[i];
                if (sum != 233)
                    throw new Exception("checksum failed");
                if (c < buf.Length)
                    result.additionalString = Pack.ParseString(Encoding, buf, ref c);
                return result;
            }
        }

        public static byte[] GetRealKeyFromString(string str)
        {
            return GetRealKeyFromString(str, 16);
        }
        
        public static byte[] GetRealKeyFromString(string str, int length)
        {
            using (var hash = SHA512.Create())
                return hash.ComputeHash(NaiveUtils.UTF8Encoding.GetBytes(str + "233334566666"))
                        .Take(length).ToArray();
        }

        public static byte[] EncryptOrDecryptBytes(bool isEncrypting, byte[] key, byte[] buf)
        {
            var bv = new BytesView(buf);
            if (key.Length > 16)
                key = key.Take(16).ToArray();
            WebSocket.GetAesFilter2(isEncrypting, key)(bv);
            return bv.GetBytes();
        }
    }
}
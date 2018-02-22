using System;
using System.Collections.Generic;
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

            public static readonly string[] EmptyStringArray = new string[0];

            public string[] extraStrings = EmptyStringArray;

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
                if (extraStrings.Length > 0) {
                    if (extraStrings.Length > 255)
                        throw new Exception("extraString.Length > 255");
                    len += 1 // strings count
                           + 1; // check sum
                    foreach (var item in extraStrings) {
                        len += item.BytesLength(Encoding);
                    }
                }
                var buf = new byte[len];
                var c = 0;
                buf[c++] = (byte)((rd.Next() % 128) * 2);
                dest.ToBytes(buf, ref c);
                byte sum = 233;
                for (int i = 0; i < c; i++)
                    sum += buf[i];
                buf[c++] = sum;
                var sumBegin = c;
                sum = 66;
                additionalString.ToBytes(Encoding, buf, ref c);
                if (extraStrings.Length > 0) {
                    buf[c++] = (byte)extraStrings.Length;
                    foreach (var item in extraStrings) {
                        item.ToBytes(Encoding, buf, ref c);
                    }
                    for (int i = sumBegin; i < c; i++)
                        sum += buf[i];
                    buf[c++] = sum;
                }
                return buf;
            }

            public static Request Parse(byte[] buf)
            {
                var offset = 0;
                return Parse(buf, ref offset);
            }

            public static Request Parse(byte[] buf, ref int c)
            {
                int sumbegin = c;
                c++;
                var result = new Request(AddrPort.Parse(buf, ref c));
                byte sum = buf[c++];
                for (int i = sumbegin; i < c - 1; i++)
                    sum -= buf[i];
                if (sum != 233)
                    throw new Exception("checksum failed");
                sumbegin = c;
                if (c < buf.Length) // version 2
                    result.additionalString = Pack.ParseString(Encoding, buf, ref c);
                if (c < buf.Length) { // version 3
                    int stringsCount = buf[c++];
                    var exStrs = result.extraStrings = new string[stringsCount];
                    for (int i = 0; i < stringsCount; i++) {
                        exStrs[i] = Pack.ParseString(Encoding, buf, ref c);
                    }
                    sum = buf[c++];
                    for (int i = sumbegin; i < c - 1; i++)
                        sum -= buf[i];
                    if (sum != 66)
                        throw new Exception("checksum failed");
                }
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

        public const string EncryptionAesOfb128 = "aes-128-ofb";
        public const string EncryptionChacha20Ietf = "chacha20-ietf";
        public const string EncryptionSpeck0 = "speck0";
        public const string EncryptionSpeck064 = "speck064";
        public const string CompressionLz4_0 = "lz4-0";

        public static void ApplyEncryption(FilterBase filterable, byte[] key, string parameter = "")
        {
            IEnumerable<string> types;
            if (parameter.IsNullOrEmpty())
                types = new[] { EncryptionAesOfb128 };
            else
                types = parameter.Split(',').Select(x => x.Trim());
            foreach (var type in types) {
                if (string.IsNullOrEmpty(type) || type == EncryptionAesOfb128) {
                    if (key.Length > 16)
                        key = key.Take(16).ToArray();
                    filterable.ApplyAesStreamFilter(key);
                } else if (type == EncryptionChacha20Ietf) {
                    if (key.Length > 32)
                        key = key.Take(32).ToArray();
                    filterable.ApplyFilterFromEncryptor(new ChaCha20IetfEncryptor(key), new ChaCha20IetfEncryptor(key));
                } else if (type == EncryptionSpeck0) {
                    filterable.ApplyFilterFromEncryptor(new Speck.Ctr128128(key), new Speck.Ctr128128(key));
                } else if (type == EncryptionSpeck064) {
                    filterable.ApplyFilterFromEncryptor(new Speck.Ctr64128(key), new Speck.Ctr64128(key));
                } else if (type == CompressionLz4_0) {
                    filterable.ApplyFilterFromFilterCreator(LZ4pn.LZ4Filter.GetFilter);
                } else {
                    throw new Exception($"unknown encryption '{type}'");
                }
            }
        }
    }
}
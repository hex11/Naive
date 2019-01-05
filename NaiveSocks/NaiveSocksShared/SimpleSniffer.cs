using System;
using System.Linq;
using System.Net;
using System.Text;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class SimpleSniffer
    {
        public SimpleSniffer(MyStream.Copier clientSide, MyStream.Copier serverSide)
        {
            if (clientSide != null)
                clientSide.OnRead += ClientData;
            if (serverSide != null)
                serverSide.OnRead += ServerData;
        }

        readonly string[] httpMethods = new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "CONNECT", "OPTIONS", "TRACE", "PATCH" };

        private void ClientData(MyStream.Copier c, BytesSegment _bs)
        {
            if (clientDone)
                return;
            if (c.Progress == 0) {
                clientDone = true;
                try {
                    TlsStream.ParseClientHelloRecord(_bs, out var ver, out var sni);
                    TlsVer = ver;
                    TlsSni = sni.SingleOrDefault() ?? "";
                } catch (Exception e) {
                }

                try {
                    var bs = _bs;
                    if (c.Progress == 0) {
                        foreach (var item in httpMethods) {
                            if (Match(bs, item) && bs.GetOrZero(item.Length) == ' ') {
                                bs.SubSelf(item.Length + 1);
                                Http = "HTTP?";
                                break;
                            }
                        }
                        if (Http != null) {
                            var begin = Find(bs, (byte)' ');
                            if (begin != -1) {
                                begin += 1;
                                bs.SubSelf(begin);
                                var len = Find(bs.Sub(0, Math.Min(bs.Len, 10)), (byte)'\r');
                                if (len == -1)
                                    len = Find(bs.Sub(0, Math.Min(bs.Len, 10)), (byte)'\n');
                                if (len != -1) {
                                    Http = Encoding.ASCII.GetString(bs.Bytes, bs.Offset, len);
                                }
                            }
                        }
                    }
                } catch (Exception e) {
                }
            }
        }

        private void ServerData(MyStream.Copier c, BytesSegment bs)
        {

        }

        bool clientDone;

        string Http;

        ushort TlsVer;

        string TlsSni;

        public string GetInfo()
        {
            var sb = new StringBuilder();
            GetInfo(sb, null);
            return sb.ToString();
        }

        public void GetInfo(StringBuilder sb, string noSniValueIf)
        {
            if (TlsVer != 0) {
                sb.Append("TLS");
                sb.Append(TlsVer == 0x0301 ? " 1.0" :
                    TlsVer == 0x0302 ? " 1.1" :
                    TlsVer == 0x0303 ? " 1.2" :
                    $"(Version 0x{TlsVer:x})");
                if (TlsSni != null) {
                    if (TlsSni == noSniValueIf)
                        sb.Append(" (SNI)");
                    else
                        sb.Append(" (SNI=").Append(TlsSni).Append(")");
                }
            } else if (Http != null) {
                sb.Append(Http);
            } else if (!clientDone) {
                sb.Append("(No Data)");
            } else {
                sb.Append("(Unknown)");
            }
        }

        bool Match(BytesSegment bs, string pattern)
        {
            if (bs.Len < pattern.Length)
                return false;
            for (int i = 0; i < pattern.Length; i++) {
                if (bs[i] != pattern[i])
                    return false;
            }
            return true;
        }

        int Find(BytesSegment bs, byte b)
        {
            int end = bs.Offset + bs.Len;
            for (int i = bs.Offset; i < end; i++) {
                if (bs.Bytes[i] == b)
                    return i - bs.Offset;
            }
            return -1;
        }
    }
}

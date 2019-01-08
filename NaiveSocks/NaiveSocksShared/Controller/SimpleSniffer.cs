using System;
using System.Linq;
using System.Net;
using System.Text;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class SimpleSniffer
    {
        public void ListenToCopier(MyStream.Copier clientSide, MyStream.Copier serverSide)
        {
            if (clientSide != null)
                clientSide.OnRead += ClientData;
            if (serverSide != null)
                serverSide.OnRead += ServerData;
        }

        readonly string[] httpMethods = new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "CONNECT", "OPTIONS", "TRACE", "PATCH" };

        public void ClientData(object sender, BytesSegment _bs)
        {
            if (clientDone)
                return;
            clientDone = true;
            try {
                TlsStream.ParseClientHelloRecord(_bs, ref Tls);
            } catch (Exception e) {
                if (Tls.Version != 0) {
                    TlsError = true;
                    Logging.exception(e, Logging.Level.Warning, "parsing tls handshake" + sender);
                }
            }

            try {
                var bs = _bs;
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
                        if (Match(bs, "HTTP")) {
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

        public void ServerData(object sender, BytesSegment bs)
        {
            // TODO
        }

        bool clientDone;

        bool TlsError;

        string Http;

        TlsStream.ClientHello Tls;

        public string GetInfo()
        {
            var sb = new StringBuilder();
            GetInfo(sb, null);
            return sb.ToString();
        }

        public void GetInfo(StringBuilder sb, string noSniValueIf)
        {
            if (Tls.Version != 0) {
                sb.Append("TLS(");
                if (Tls.Version == 0x0303 && Tls.Alpn == "h2" && (noSniValueIf != null && Tls.Sni == noSniValueIf)) {
                    sb.Append("'h2'");
                } else {
                    sb.Append(Tls.Version == 0x0301 ? "1.0" :
                        Tls.Version == 0x0302 ? "1.1" :
                        Tls.Version == 0x0303 ? "1.2" :
                        $"0x{Tls.Version:x}");
                    if (Tls.Sni != null) {
                        if (Tls.Sni == noSniValueIf)
                            sb.Append(",SNI");
                        else
                            sb.Append(",SNI=").Append(Tls.Sni);
                    }
                    if (Tls.Alpn != null && !string.Equals(Tls.Alpn, "http/1.1", StringComparison.OrdinalIgnoreCase)) {
                        sb.Append(",'").Append(Tls.Alpn).Append('\'');
                    }
                }
                if (TlsError) {
                    sb.Append(",Error");
                }
                sb.Append(')');
            } else if (Http != null) {
                sb.Append(Http);
            } else if (!clientDone) {
                sb.Append("(No Data)");
            } else {
                sb.Append("---");
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

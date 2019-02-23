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
            if (clientDone || _bs.Len == 0)
                return;
            clientDone = true;
            try {
                TlsStream.ParseClientHelloRecord(_bs, ref Tls, out var size);
            } catch (Exception e) {
                if (Tls.Version != 0) {
                    TlsError = true;
                    Logging.exception(e, Logging.Level.Warning, "parsing tls client hello from " + sender);
                }
            }

            try {
                var bs = _bs;
                var http = false;
                foreach (var item in httpMethods) {
                    if (Match(bs, item) && bs.GetOrZero(item.Length) == ' ') {
                        bs.SubSelf(item.Length + 1);
                        http = true;
                        break;
                    }
                }
                if (http) {
                    Protocol = "HTTP?";
                    var begin = Find(bs, (byte)' ');
                    if (begin != -1) {
                        begin += 1;
                        bs.SubSelf(begin);
                        if (Match(bs, "HTTP")) {
                            var len = Find(bs.Sub(0, Math.Min(bs.Len, 10)), (byte)'\r');
                            if (len == -1)
                                len = Find(bs.Sub(0, Math.Min(bs.Len, 10)), (byte)'\n');
                            if (len != -1) {
                                Protocol = Encoding.ASCII.GetString(bs.Bytes, bs.Offset, len);
                            }
                        }
                    }
                }
            } catch (Exception) {
            }

            if (Protocol != null) return;

            if (Match(_bs, "SSH-")) {
                Protocol = "SSH";
            } else if (Match(_bs, "\x13BitTorrent protocol")) {
                Protocol = "BitTorrent";
            }
        }

        public void ServerData(object sender, BytesSegment bs)
        {
            if (serverDone || bs.Len == 0)
                return;
            serverDone = true;
            if (sBuf != null) {
                bs.CopyTo(sBuf, 0, bs.Len, sProg);
                sProg += bs.Len;
                if (sProg < sBuf.Length) {
                    goto CONTINUE_READ;
                } else {
                    bs = new BytesSegment(sBuf, 0, sProg);
                }
            }
            if (Tls.Version != 0) {
                var hello = new TlsStream.ServerHello();
                try {
                    TlsStream.ParseServerHelloRecord(bs, ref hello, out var size);
                    if (size > bs.Len) {
                        if (sBuf != null)
                            throw new Exception("sBuf != null");
                        sBuf = new byte[size];
                        bs.CopyTo(sBuf);
                        sProg = bs.Len;
                        goto CONTINUE_READ;
                    }
                } catch (Exception e) {
                    if (hello.Version != 0) {
                        TlsError = true;
                        Logging.exception(e, Logging.Level.Warning, "parsing tls server hello from " + sender);
                    }
                }
                Tls.Version = Math.Min(Tls.Version, hello.Version);
                Tls.Alpn = hello.Alpn;
            }
            sBuf = null;
            return;
            CONTINUE_READ:
            serverDone = false;
            return;
        }

        byte[] sBuf;
        int sProg;

        bool clientDone;

        bool TlsError;

        string Protocol;

        TlsStream.ClientHello Tls;

        bool serverDone;

        public string GetInfo()
        {
            var sb = new StringBuilder();
            GetInfo(sb, null);
            return sb.ToString();
        }

        public void GetInfo(StringBuilder sb, string probablySNI)
        {
            if (Tls.Version != 0) {
                sb.Append("TLS(");
                if (!(Tls.Alpn == "h2" && Tls.Version == 0x0303)) {
                    sb.Append(Tls.Version == 0x0301 ? "1.0" :
                        Tls.Version == 0x0302 ? "1.1" :
                        Tls.Version == 0x0303 ? "1.2" :
                        Tls.Version == 0x0304 ? "1.3" :
                        $"0x{Tls.Version:x4}");
                    sb.Append(',');
                }
                if (Tls.Sni == null) {
                    sb.Append("noSNI,");
                } else if (Tls.Sni == probablySNI) {
                    //sb.Append(",SNI");
                } else {
                    sb.Append("SNI=").Append(Tls.Sni).Append(',');
                }
                if (Tls.Alpn != null && !string.Equals(Tls.Alpn, "http/1.1", StringComparison.OrdinalIgnoreCase)) {
                    sb.Append("'").Append(Tls.Alpn).Append('\'').Append(',');
                }
                if (TlsError) {
                    sb.Append("Error,");
                }
                if (!serverDone) {
                    sb.Append("...,");
                }
                sb[sb.Length - 1] = ')';
            } else if (Protocol != null) {
                sb.Append(Protocol);
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

using Naive.HttpSvr;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks
{
    internal class Socks5Server
    {
        public IMyStream Stream { get; }
        public byte[] buf;

        public Func<Socks5Server, Task> RequestingToConnect;
        public Func<Socks5Server, bool> Auth;

        public string TargetAddr { get; private set; }
        public int TargetPort { get; private set; }

        public string Username { get; private set; }
        public string Password { get; private set; }

        public Socks5Status Status { get; private set; } = Socks5Status.OpenningHandshake;

        public Methods AcceptMethods { get; set; } = Methods.NoAuth;

        [Flags]
        public enum Methods
        {
            None,
            NoAuth = 1,
            UsernamePassword = 2
        }

        public enum Socks5Status
        {
            OpenningHandshake,
            WaitingForRequest,
            ProcessingRequest,
            ReplySent,
            Disconnected
        }

        public Socks5Server(IMyStream stream)
        {
            Stream = stream;
        }

        public async Task<bool> ProcessAsync()
        {
            buf = new byte[256];
            var read = await Stream.ReadAsyncR(buf);
            if (read < 3)
                return false;

            var version = buf[0];
            var nmethod = buf[1];
            checkVersion(version);
            if (nmethod == 0)
                throw getException("nmethod is zero");
            var correctLen = 2 + nmethod;
            if (read > correctLen)
                throw getException("unexpected packet length");
            if (read < correctLen) {
                await Stream.ReadFullAsyncR(new BytesSegment(buf, read, correctLen - read));
            }
            byte succeedMethod = 0xff; // NO ACCEPTABLE METHODS
            for (int i = 0; i < nmethod; i++) {
                var method = buf[2 + i];
                if (method == 0 && succeedMethod != 2 && (AcceptMethods & Methods.NoAuth) != 0) { // NO AUTHENTICATION REQUIRED
                    succeedMethod = 0;
                } else if (method == 2 && (AcceptMethods & Methods.UsernamePassword) != 0) { // USERNAME/PASSWORD
                    succeedMethod = 2;
                }
            }

            buf[0] = 0x05;
            buf[1] = succeedMethod;
            await Stream.WriteAsyncR(new BytesSegment(buf, 0, 2));
            Status = Socks5Status.WaitingForRequest;

            if (succeedMethod == 0xff)
                return false;
            if (succeedMethod == 2) {
                // https://tools.ietf.org/html/rfc1929
                await ReadFullAsyncR(buf, 2);
                var ver = buf[0];
                var ulen = buf[1];

                await ReadFullAsyncR(buf, ulen);
                Username = Encoding.ASCII.GetString(buf, 0, ulen);

                await ReadFullAsyncR(buf, 1);
                var plen = buf[0];

                await ReadFullAsyncR(buf, plen);
                Password = Encoding.ASCII.GetString(buf, 0, plen);

                var ok = Auth?.Invoke(this) ?? false;
                buf[0] = 1;
                buf[1] = (byte)(ok ? 0 : 1);
                await Stream.WriteAsyncR(new BytesSegment(buf, 0, 2));
                if (!ok)
                    return false;
            }
            //Console.WriteLine($"(socks5) {Client.Client.RemoteEndPoint} handshake.");

            await ReadFullAsyncR(buf, 4);
            checkVersion(buf[0]);
            var cmd = (ReqCmd)buf[1];
            var rsv = buf[2];
            var addrType = (AddrType)buf[3];
            string addrString = null;
            switch (addrType) {
                case AddrType.IPv4Address:
                case AddrType.IPv6Address:
                    var ipbytes = new byte[addrType == AddrType.IPv4Address ? 4 : 16];
                    await Stream.ReadFullAsyncR(ipbytes);
                    var ip = new IPAddress(ipbytes);
                    addrString = ip.ToString();
                    break;
                case AddrType.DomainName:
                    var length = await readByteAsync();
                    if (length == 0)
                        throw getException("length of domain name cannot be zero");
                    await ReadSharedBufferAsyncR(length);
                    addrString = Encoding.ASCII.GetString(buf, 0, length);
                    break;
            }
            //Console.WriteLine($"(socks5) request Cmd={cmd} AddrType={addrType} Addr={addrString}");
            TargetAddr = addrString;
            if (addrString == null) {
                await WriteReply(Rep.Address_type_not_supported);
                return false;
            }
            if (cmd != ReqCmd.Connect) {
                await WriteReply(Rep.Command_not_supported);
                return false;
            }

            await ReadFullAsyncR(buf, 2);
            TargetPort = buf[0] << 8;
            TargetPort |= buf[1];
            Status = Socks5Status.ProcessingRequest;

            return true;
        }

        private void checkVersion(byte version)
        {
            if (version != 5)
                throw getException("not supported socks version");
        }

        public async Task WriteConnectionResult(IPEndPoint ep, Rep rep)
        {
            var addr = ep.Address.GetAddressBytes();
            if (addr.Length == 4) {
                await WriteReply(rep, AddrType.IPv4Address, addr, ep.Port);
            } else if (addr.Length == 16) {
                await WriteReply(rep, AddrType.IPv6Address, addr, ep.Port);
            } else {
                throw new Exception("unexcepted addr.length in WriteConnectionResult()");
            }
        }

        public AwaitableWrapper WriteReply(Rep rep) => WriteReply(rep, AddrType.IPv4Address, null, 0);

        public AwaitableWrapper WriteReply(Rep rep, AddrType atyp, byte[] addr, int port)
        {
            if (Status == Socks5Status.ReplySent) {
                throw getException("Socks5 reply has been already sent.");
            }
            Status = Socks5Status.ReplySent;
            var buf = this.buf;
            this.buf = null;
            buf[0] = (0x05);
            buf[1] = ((byte)rep);
            buf[2] = (0x00);
            buf[3] = ((byte)AddrType.IPv4Address);
            for (int i = 0; i < 4; i++) {
                buf[4 + i] = addr[i];
            }
            buf[8] = (0x00);
            buf[9] = (0x00);
            return Stream.WriteAsyncR(new BytesSegment(buf, 0, 10));
        }

        private async Task<byte> readByteAsync()
        {
            var oneByteBuf = new byte[1];
            var b = await Stream.ReadAsync(oneByteBuf, 0, 1);
            if (b == 0)
                throw getEOFException();
            return oneByteBuf[0];
        }

        private AwaitableWrapper ReadSharedBufferAsyncR(int count)
        {
            return ReadFullAsyncR(buf, count);
        }

        private AwaitableWrapper ReadFullAsyncR(byte[] bytes, int count)
        {
            return Stream.ReadFullAsyncR(new BytesSegment(bytes, 0, count));
        }

        private Exception getException(string msg) => new Exception(msg);
        private Exception getEOFException() => getException("unexpected EOF");

        public void FailedToConnect(SocketException ex)
        {
            var rep = Rep.Connection_refused;
            if (ex != null) {
                rep = GetRepFromSocketErrorCode(ex.SocketErrorCode);
            }
            WriteReply(rep);
        }

        private static Rep GetRepFromSocketErrorCode(SocketError se)
        {
            var rep = Rep.Connection_refused;
            if (se == SocketError.HostUnreachable) {
                rep = Rep.Host_unreachable;
            } else if (se == SocketError.NetworkUnreachable) {
                rep = Rep.Network_unreachable;
            } else if (se == SocketError.AddressFamilyNotSupported) {
                rep = Rep.Address_type_not_supported;
            } else if (se == SocketError.HostNotFound) {
                rep = Rep.Host_unreachable;
            }
            return rep;
        }

        private enum ReqCmd : byte
        {
            Connect = 0x01,
            Bind = 0x02,
            UdpAssociate = 0x03
        }

        public enum Rep : byte
        {
            succeeded = 0x00,
            general_SOCKS_server_failure = 0x01,
            connection_not_allowed_by_ruleset = 0x02,
            Network_unreachable = 0x03,
            Host_unreachable = 0x04,
            Connection_refused = 0x05,
            TTL_expired = 0x06,
            Command_not_supported = 0x07,
            Address_type_not_supported = 0x08
        }

        public enum AddrType : byte
        {
            IPv4Address = 0x01,
            DomainName = 0x03,
            IPv6Address = 0x04
        }
    }
}

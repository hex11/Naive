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
        public TcpClient Client { get; }
        public IMyStream Stream { get; }
        public byte[] buf;

        public Func<Socks5Server, Task> RequestingToConnect;

        public string TargetAddr { get; private set; }
        public int TargetPort { get; private set; }

        public Socks5Status Status { get; private set; } = Socks5Status.OpenningHandshake;

        public enum Socks5Status
        {
            OpenningHandshake,
            WaitingForRequest,
            ProcessingRequest,
            ReplySent,
            Disconnected
        }

        public Socks5Server(TcpClient socket)
        {
            this.Client = socket;
            Stream = MyStream.FromSocket(socket.Client);
        }

        public async Task ProcessAsync()
        {
            buf = new byte[256];
            var read = await Stream.ReadAsync(buf, 0, buf.Length);
            if (read >= 3) {
                var version = buf[0];
                var nmethod = buf[1];
                checkVersion(version);
                if (nmethod == 0)
                    throw getException("nmethod is zero");
                byte succeedMethod = 0xff; // NO ACCEPTABLE METHODS
                for (int i = 0; i < nmethod; i++) {
                    var method = buf[2 + i];
                    if (method == 0) { // NO AUTHENTICATION REQUIRED
                        succeedMethod = 0;
                        break;
                    }
                }
                await WriteMethodSelectionMessage(succeedMethod);
                if (succeedMethod == 0xff)
                    return;
                //Console.WriteLine($"(socks5) {Client.Client.RemoteEndPoint} handshake.");
                await processRequests();
            }
        }

        private void checkVersion(byte version)
        {
            if (version != 5)
                throw getException("not supported socks version");
        }

        private async Task processRequests()
        {
            var b = new byte[4];
            await readBytesAsync(b, 4);
            checkVersion(b[0]);
            var cmd = (ReqCmd)b[1];
            var rsv = b[2];
            var addrType = (AddrType)b[3];
            string addrString = null;
            switch (addrType) {
            case AddrType.IPv4Address:
            case AddrType.IPv6Address:
                var ip = new IPAddress(await readNewBytesAsync(addrType == AddrType.IPv4Address ? 4 : 16));
                addrString = ip.ToString();
                break;
            case AddrType.DomainName:
                var length = await readByteAsync();
                if (length == 0)
                    throw getException("length of domain name cannot be zero");
                await readBytesAsync(length);
                addrString = Encoding.ASCII.GetString(buf, 0, length);
                break;
            }
            //Console.WriteLine($"(socks5) request Cmd={cmd} AddrType={addrType} Addr={addrString}");
            TargetAddr = addrString;
            if (addrString == null) {
                await WriteReply(Rep.Address_type_not_supported);
            } else if (cmd == ReqCmd.Connect) {
                await readBytesAsync(b, 2);
                TargetPort = b[0] << 8;
                TargetPort |= b[1];
                Status = Socks5Status.ProcessingRequest;
                await Naive.HttpSvr.NaiveUtils.RunAsyncTask(() => RequestingToConnect?.Invoke(this));
            } else {
                await WriteReply(Rep.Command_not_supported);
            }
        }

        public Task<int> ReadAsync(byte[] bytes, int offset, int size)
        {
            return Stream.ReadAsync(bytes, offset, size);
        }

        public Task WriteAsync(byte[] bytes, int offset, int size)
        {
            return Stream.WriteAsync(bytes, offset, size);
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

        private async Task WriteMethodSelectionMessage(byte method)
        {
            if (Status >= Socks5Status.WaitingForRequest)
                throw getException("Socks5 Method Selection Message has been already sent.");
            buf[0] = 0x05;
            buf[1] = method;
            await Stream.WriteAsync(buf, 0, 2);
            Status = Socks5Status.WaitingForRequest;
        }

        public async Task WriteReply(Rep rep) => await WriteReply(rep, AddrType.IPv4Address, null, 0);

        public async Task WriteReply(Rep rep, AddrType atyp, byte[] addr, int port)
        {
            if (Status == Socks5Status.ReplySent) {
                throw getException("Socks5 reply has been already sent.");
            }
            Status = Socks5Status.ReplySent;
            buf[0] = (0x05);
            buf[1] = ((byte)rep);
            buf[2] = (0x00);
            buf[3] = ((byte)AddrType.IPv4Address);
            for (int i = 0; i < 4; i++) {
                buf[4 + i] = addr[i];
            }
            buf[8] = (0x00);
            buf[9] = (0x00);
            await Stream.WriteAsync(buf, 0, 10);
        }

        private async Task<byte> readByteAsync()
        {
            var oneByteBuf = new byte[1];
            var b = await Stream.ReadAsync(oneByteBuf, 0, 1);
            if (b == 0)
                throw getEOFException();
            return oneByteBuf[0];
        }

        private Task readBytesAsync(int count)
        {
            return readBytesAsync(buf, count);
        }

        private async Task<byte[]> readNewBytesAsync(int length)
        {
            var b = new byte[length];
            await readBytesAsync(b, length);
            return b;
        }

        private async Task readBytesAsync(byte[] bytes, int count)
        {
            var pos = 0;
            while (pos < count) {
                int read;
                pos += read = await Stream.ReadAsync(bytes, pos, count - pos);
                if (read == 0)
                    throw getEOFException();
            }
        }

        private Exception getException(string msg) => new Exception(msg);
        private Exception getEOFException() => getException("unexpected EOF");

        public void FailedToConnect(SocketException ex)
        {
            var rep = Rep.Connection_refused;
            if (ex != null) {
                rep = GetRepFromSocketErrorCode(ex.SocketErrorCode);
            }
            WriteReply(rep).Forget();
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

// https://github.com/gingray/Socks5Client/blob/master/Socks5Client/Socks5Client/Socks5Client.cs

using Naive.HttpSvr;
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Socket = System.Net.Sockets.Socket;

namespace NaiveSocks
{
    public class Socks5Client
    {
        private string _socksAddr;
        private int _socksPort;
        private string _destAddr;
        private int _destPort;
        private string _username;
        private string _password;
        private Socket _socket;
        private const int SOCKS_VER = 0x05;
        private const int AUTH_METH_SUPPORT = 0x02;
        private const int USER_PASS_AUTH = 0x02;
        private const int NOAUTH = 0x00;
        private const int CMD_CONNECT = 0x01;
        private const int SOCKS_ADDR_TYPE_IPV4 = 0x01;
        private const int SOCKS_ADDR_TYPE_IPV6 = 0x04;
        private const int SOCKS_ADDR_TYPE_DOMAIN_NAME = 0x03;
        private const int AUTH_METHOD_NOT_SUPPORTED = 0xff;
        private const int SOCKS_CMD_SUCCSESS = 0x00;

        private Socks5Client(string socksAddress, int socksPort, string destAddress, int destPort, string username, string password)
        {
            _socksAddr = socksAddress;
            _socksPort = socksPort;
            _destAddr = destAddress;
            _destPort = destPort;
            _username = username;
            _password = password;
        }

        public async Task<SocketStream> ConnectAsync()
        {
            _socket = await NaiveUtils.ConnectTcpAsync(new AddrPort(_socksAddr, _socksPort), 0);
            var _ns = MyStream.FromSocket(_socket);
            Task write(byte[] buf) => _ns.WriteAsync(buf, 0, buf.Length);
            //Task read(byte[] buf) => _ns.ReadAsync(buf, 0, buf.Length);
            async Task read2(byte[] buf, int offset, int len)
            {
                var pos = 0;
                while (pos < len) {
                    var read = await _ns.ReadAsync(buf, offset + pos, len - pos);
                    if (read == 0)
                        throw new DisconnectedException("unexpected EOF");
                    pos += read;
                }
            }
            byte[] buffer =
                _username == null
                ? new byte[] { SOCKS_VER, 1, NOAUTH }
                : new byte[] { SOCKS_VER, AUTH_METH_SUPPORT, NOAUTH, USER_PASS_AUTH };
            await _ns.WriteAsync(buffer, 0, buffer.Length);
            await read2(buffer, 0, 2);
            if (buffer[1] == AUTH_METHOD_NOT_SUPPORTED) {
                _socket.Close();
                throw new SocksAuthException();
            }

            if (buffer[1] == USER_PASS_AUTH && (_username == null || _password == null))
                throw new ArgumentException("No username or password provided");

            if (buffer[1] == USER_PASS_AUTH) {
                byte[] credentials = new byte[_username.Length + _password.Length + 3];
                credentials[0] = 1;
                credentials[1] = (byte)_username.Length;
                Encoding.ASCII.GetBytes(_username).CopyTo(credentials, 2);
                credentials[_username.Length + 2] = (byte)_password.Length;
                Encoding.ASCII.GetBytes(_password).CopyTo(credentials, _username.Length + 3);

                await write(credentials);
                await read2(buffer, 0, 2);
                if (buffer[1] != SOCKS_CMD_SUCCSESS)
                    throw new SocksRefuseException("Username or password invalid");
            }

            byte addrType = GetAddressType();
            byte[] address = GetDestAddressBytes(addrType, _destAddr);
            byte[] port = GetDestPortBytes(_destPort);
            buffer = new byte[4 + port.Length + address.Length];
            buffer[0] = SOCKS_VER;
            buffer[1] = CMD_CONNECT;
            buffer[2] = 0x00; //reserved
            buffer[3] = addrType;
            address.CopyTo(buffer, 4);
            port.CopyTo(buffer, 4 + address.Length);
            await write(buffer);
            buffer = new byte[255];
            await read2(buffer, 0, 4);
            if (buffer[1] != SOCKS_CMD_SUCCSESS)
                throw new SocksRefuseException($"remote socks5 server returns {new BytesView(buffer, 0, 4)}");
            switch (buffer[3]) {
            case 1:
                await read2(buffer, 0, 4 + 2);
                break;
            case 3:
                await read2(buffer, 0, 1);
                await read2(buffer, 0, buffer[0]);
                break;
            case 4:
                await read2(buffer, 0, 16 + 2);
                break;
            default:
                throw new Exception("Not supported addr type: " + buffer[3]);
            }
            return _ns;
        }

        private byte GetAddressType()
        {
            IPAddress ipAddr;
            bool result = IPAddress.TryParse(_destAddr, out ipAddr);

            if (!result)
                return SOCKS_ADDR_TYPE_DOMAIN_NAME;

            switch (ipAddr.AddressFamily) {
            case AddressFamily.InterNetwork:
                return SOCKS_ADDR_TYPE_IPV4;
            case AddressFamily.InterNetworkV6:
                return SOCKS_ADDR_TYPE_IPV6;
            default:
                throw new BadDistanationAddrException();
            }
        }

        private byte[] GetDestAddressBytes(byte addressType, string host)
        {
            switch (addressType) {
            case SOCKS_ADDR_TYPE_IPV4:
            case SOCKS_ADDR_TYPE_IPV6:
                return IPAddress.Parse(host).GetAddressBytes();
            case SOCKS_ADDR_TYPE_DOMAIN_NAME:
                byte[] bytes = new byte[host.Length + 1];
                bytes[0] = Convert.ToByte(host.Length);
                Encoding.ASCII.GetBytes(host).CopyTo(bytes, 1);
                return bytes;
            default:
                return null;
            }
        }

        private byte[] GetDestPortBytes(int value)
        {
            byte[] array = new byte[2];
            array[0] = (byte)(value >> 8);
            array[1] = (byte)value;
            return array;
        }

        public static Task<SocketStream> Connect(string socksAddress, int socksPort, string destAddress, int destPort, string username, string password)
        {
            Socks5Client client = new Socks5Client(socksAddress, socksPort, destAddress, destPort, username, password);
            return client.ConnectAsync();
        }
    }

    [Serializable]
    public class SocksAuthException : Exception
    {
        public SocksAuthException()
        {
        }

        public SocksAuthException(string message)
            : base(message)
        {
        }

        public SocksAuthException(string message, Exception inner)
            : base(message, inner)
        {
        }

        protected SocksAuthException(
            SerializationInfo info,
            StreamingContext context)
            : base(info, context)
        {
        }
    }

    [Serializable]
    public class BadDistanationAddrException : Exception
    {
        public BadDistanationAddrException()
        {
        }

        public BadDistanationAddrException(string message)
            : base(message)
        {
        }

        public BadDistanationAddrException(string message, Exception inner)
            : base(message, inner)
        {
        }

        protected BadDistanationAddrException(
            SerializationInfo info,
            StreamingContext context)
            : base(info, context)
        {
        }
    }

    [Serializable]
    public class SocksRefuseException : Exception
    {
        public SocksRefuseException()
        {
        }

        public SocksRefuseException(string message)
            : base(message)
        {
        }

        public SocksRefuseException(string message, Exception inner)
            : base(message, inner)
        {
        }

        protected SocksRefuseException(
            SerializationInfo info,
            StreamingContext context)
            : base(info, context)
        {
        }
    }
}
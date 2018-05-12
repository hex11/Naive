using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NaiveSocks
{

    public abstract class SocketStream : MyStream, IMyStreamSync, IMyStreamBeginEnd
    {
        static SocketStream()
        {
            InitUnderlyingRead();
        }

        protected SocketStream(Socket socket)
        {
            this.Socket = socket;
            this.EPPair = EPPair.FromSocket(socket);
            this.fd = socket.Handle;
        }

        public Socket Socket { get; }

        private IntPtr fd { get; }

        public EPPair EPPair { get; }

        public override string ToString() => $"{{Socket {State} {EPPair.ToString()}}}";

        public override Task Close()
        {
            Logging.debug($"{this}: close");
            this.State = MyStreamState.Closed;
            Socket.Close();
            return NaiveUtils.CompletedTask;
        }

        public override Task Shutdown(SocketShutdown direction)
        {
            Logging.debug($"{this}: local shutdown");
            this.State |= MyStreamState.LocalShutdown;
            Socket.Shutdown(direction);
            return NaiveUtils.CompletedTask;
        }

        public virtual void Write(BytesSegment bs)
        {
            Socket.Send(bs.Bytes, bs.Offset, bs.Len, SocketFlags.None);
        }

        public virtual int Read(BytesSegment bs)
        {
            int r;
            if (EnableUnderlyingCalls && underlyingReadImpl != null) {
                r = underlyingReadImpl(this, bs);
            } else {
                r = Socket.Receive(bs.Bytes, bs.Offset, bs.Len, SocketFlags.None);
            }
            if (r == 0)
                State |= MyStreamState.RemoteShutdown;
            if (r < 0)
                throw new Exception($"socket read() returned a unexpected value: {r} (underlyingcalls={EnableUnderlyingCalls}, platform={osPlatform})");
            return r;
        }

        public IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            return Socket.BeginSend(buffer, offset, count, SocketFlags.None, callback, state);
        }

        public void EndWrite(IAsyncResult asyncResult)
        {
            Socket.EndSend(asyncResult);
        }

        public IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            return Socket.BeginReceive(buffer, offset, count, SocketFlags.None, callback, state);
        }

        public int EndRead(IAsyncResult asyncResult)
        {
            return Socket.EndReceive(asyncResult);
        }

        public static bool EnableUnderlyingCalls { get; set; } = false;

        static void InitUnderlyingRead()
        {
            if (EnableUnderlyingCalls && osPlatform == PlatformID.Unix) {
                underlyingReadImpl = (s, bs) => {
                    bs.CheckAsParameter();
                    unsafe {
                        fixed (byte* ptr = bs.Bytes) {
                            return (int)unix_read((int)s.fd, ptr + bs.Offset, (IntPtr)bs.Len);
                            // TODO: Handle errors
                        }
                    }
                };
            } else if (EnableUnderlyingCalls && osPlatform == PlatformID.Win32NT) {
                underlyingReadImpl = (s, bs) => {
                    bs.CheckAsParameter();
                    int r;
                    unsafe {
                        fixed (byte* ptr = bs.Bytes) {
                            r = win_recv(s.fd, ptr + bs.Offset, bs.Len, 0);
                        }
                    }
                    if (r < 0) {
                        var errCode = Marshal.GetLastWin32Error();
                        throw new SocketException(errCode);
                    }
                    return r;
                };
            }
        }

        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        private unsafe static extern IntPtr unix_read([In]int fileDescriptor, [In]byte* buf, [In]IntPtr count);

        [DllImport("Ws2_32", EntryPoint = "recv", SetLastError = true)]
        private unsafe static extern int win_recv([In]IntPtr s, [In]byte* buf, [In]int len, [In]int flags);

        //[System.Runtime.InteropServices.DllImport("Ws2_32", EntryPoint = "WSAGetLastError")]
        //private unsafe static extern int win_WSAGetLastError();

        static PlatformID osPlatform = Environment.OSVersion.Platform;

        static Func<SocketStream, BytesSegment, int> underlyingReadImpl;
    }
}

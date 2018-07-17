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

    public abstract class SocketStream : MyStream, IMyStreamSync, IMyStreamBeginEnd, IMyStreamReadFull
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

        public override string ToString() => $"{{Socket {State.ToString()} {EPPair.ToString()}}}";

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


        protected static Counters ctr;

        public static Counters GlobalCounters => ctr;

        public struct Counters
        {
            public int Rasync, Rsync, Rbuffer, Wasync, Wsync;

            public string StringRead => $"Read {Rsync + Rasync + Rbuffer} (async {Rasync}, sync completed {Rsync}, from buffer {Rbuffer})";
            public string StringWrite => $"Write {Wsync + Wasync} (async {Wasync}, sync completed {Wsync})";
        }

        const int readaheadScoreMax = 3;
        int readaheadScore = readaheadScoreMax;

        public static int DefaultReadaheadBufferSize { get; set; } = 4096;
        public int ReadaheadBufferSize { get; set; } = DefaultReadaheadBufferSize;
        BytesSegment readaheadBuffer;

        int socketAvailable;

        void removeAvailable(int read)
        {
            if (this.socketAvailable > read)
                this.socketAvailable -= read;
            else
                this.socketAvailable = 0;
        }

        public int LengthCanSyncRead => readaheadBuffer.Len + socketAvailable;

        public bool EnableReadaheadBuffer { get; set; } = true;
        public bool EnableSmartSyncRead { get; set; } = true;
        public bool Enable2ndBufferCheck { get; set; } = false;


        public Task ReadFullAsync(BytesSegment bs)
        {
            if (LengthCanSyncRead >= bs.Len) {
                var task = ReadAsync(bs, true);
                if (!(task.IsCompleted && task.Result == bs.Len)) {
                    Logging.errorAndThrow($"BUG in ReadAsync(bs, true)! (bs.Len={bs.Len})");
                }
                return task;
            } else {
                return ReadFullAsyncImpl(bs);
            }
        }

        public virtual Task ReadFullAsyncImpl(BytesSegment bs)
        {
            return MyStreamExt.ReadFullImpl(this, bs);
        }

        public override Task<int> ReadAsync(BytesSegment bs)
        {
            return ReadAsync(bs, false);
        }

        private Task<int> ReadAsync(BytesSegment bs, bool full)
        {
            // We use an internal read-ahead buffer to reduce many read() syscalls when bs.Len is very small.
            // It's important since there is a hardware bug named 'Meltdown' in Intel CPUs.
            // TESTED: This optimization made ReadAsync 20x faster when bs.Len == 4, 
            //         on Windows 10 x64 16299.192 with a laptop Haswell CPU.

            var bufRead = TryReadInternalBuffer(bs);
            if (bufRead > 0) {
                if ((full || (EnableSmartSyncRead && Enable2ndBufferCheck)) && bs.Len > bufRead && socketAvailable > 0) {
                    int read = ReadSync_SocketHaveAvailableData(bs.Sub(bufRead));
                    return NaiveUtils.GetCachedTaskInt(bufRead + read);
                } else {
                    return NaiveUtils.GetCachedTaskInt(bufRead);
                }
            }

            if (full) {
                // when bufRead == 0 && socketAvailable > 0
                int read = ReadSync_SocketHaveAvailableData(bs);
                return NaiveUtils.GetCachedTaskInt(read);
            }

            if (EnableSmartSyncRead | EnableReadaheadBuffer) {
                if (socketAvailable < bs.Len || socketAvailable < ReadaheadBufferSize) {
                    // Get the Available value from socket when needed.
                    socketAvailable = Socket.Available;
                }
                var bufRead2 = TryFillAndReadInternalBuffer(bs);
                if (bufRead2 > 0) {
                    return NaiveUtils.GetCachedTaskInt(bufRead2);
                }
                if (EnableSmartSyncRead && socketAvailable > 0) {
                    // If the receive buffer of OS is not empty,
                    // use sync operation to reduce async overhead.
                    // This optimization made ReadAsync 12x faster.
                    int read = ReadSync_SocketHaveAvailableData(bs);
                    return NaiveUtils.GetCachedTaskInt(read);
                }
            }
            Interlocked.Increment(ref ctr.Rasync);
            return ReadAsyncImpl(bs);
        }

        protected abstract Task<int> ReadAsyncImpl(BytesSegment bs);

        protected void OnAsyncReadCompleted(int read)
        {
            removeAvailable(read);
        }

        private int TryFillAndReadInternalBuffer(BytesSegment bs)
        {
            var readBufferSize = this.ReadaheadBufferSize;
            if (EnableReadaheadBuffer && (socketAvailable > bs.Len & bs.Len < readBufferSize)) {
                if (readaheadScore > 0)
                    readaheadScore--;
                if (readaheadBuffer.Bytes == null) {
                    if (readaheadScore > 0)
                        return 0;
                    readaheadBuffer.Bytes = new byte[readBufferSize];
                } else if (readaheadBuffer.Bytes.Length < readBufferSize) {
                    readaheadBuffer.Bytes = new byte[readBufferSize];
                }
                Interlocked.Increment(ref ctr.Rsync);
                readaheadBuffer.Offset = 0;
                readaheadBuffer.Len = readBufferSize;
                var read = ReadSocketDirectSync(readaheadBuffer);
                readaheadBuffer.Len = read;
                if (read == 0)
                    throw new Exception("WTF! It should not happen: Socket.Receive() returns 0 when Socket.Available > 0.");
                read = Math.Min(read, bs.Len);
                readaheadBuffer.CopyTo(bs, read);
                readaheadBuffer.SubSelf(read);
                return read;
            } else {
                if (readaheadScore < readaheadScoreMax) {
                    if (++readaheadScore == readaheadScoreMax) {
                        // There are many large reading opearations, so the internal buffer can be released now.
                        readaheadBuffer.Bytes = null;
                    }
                }
                return 0;
            }
        }

        private int ReadSync_SocketHaveAvailableData(BytesSegment bs)
        {
            Interlocked.Increment(ref ctr.Rsync);
            var read = ReadSocketDirectSync(bs);
            if (read == 0)
                throw new Exception("WTF! It should not happen: Socket.Receive() returns 0 when Socket.Available > 0.");
            return read;
        }

        private int TryReadInternalBuffer(BytesSegment bs)
        {
            if (readaheadBuffer.Len > 0) {
                Interlocked.Increment(ref ctr.Rbuffer);
                var read = Math.Min(bs.Len, readaheadBuffer.Len);
                readaheadBuffer.CopyTo(bs, read);
                readaheadBuffer.SubSelf(read);
                // We can check if readBuffer is emptied, then release readBuffer here,
                // but we are not doing that, because there may be more ReadAsync() calls with small buffer later.
                return read;
            }
            return 0;
        }

        public int Read(BytesSegment bs)
        {
            var bufRead = TryReadInternalBuffer(bs);
            if (bufRead > 0)
                return bufRead;
            return ReadSocketDirectSync(bs);
        }

        int ReadSocketDirectSync(BytesSegment bs)
        {
            int r;
            r = SocketReadImpl(bs);
            removeAvailable(r);
            return r;
        }

        private int SocketReadImpl(BytesSegment bs)
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
                throw new Exception($"socket read() returned a unexpected value: {r} (underlyingcalls={EnableUnderlyingCalls}, {(underlyingReadImpl == null ? "no" : "have")} impl, platform={osPlatform})");
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
            if (osPlatform == PlatformID.Unix) {
                underlyingReadImpl = (s, bs) => {
                    bs.CheckAsParameter();
                    unsafe {
                        fixed (byte* ptr = bs.Bytes) {
                            return (int)unix_read((int)s.fd, ptr + bs.Offset, (IntPtr)bs.Len);
                            // TODO: Handle errors
                        }
                    }
                };
            } else if (osPlatform == PlatformID.Win32NT) {
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

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

    public abstract class SocketStream : MyStream, IMyStreamSync, IMyStreamReadFull, IMyStreamReadFullR, IMyStreamReadR, IMyStreamWriteR, IMyStreamDispose
    {
        static SocketStream()
        {
            InitUnderlyingRead();
        }

        protected SocketStream(Socket socket)
        {
            this.Socket = socket;
            this.EPPair = EPPair.FromSocket(socket);
        }

        public Socket Socket { get; }

        private IntPtr _fd = (IntPtr)(-1);
        public IntPtr Fd
        {
            get {
                if (_fd == (IntPtr)(-1)) _fd = Socket.Handle;
                return _fd;
            }
        }

        public EPPair EPPair { get; }

        public override string ToString()
        {
            string v;
            try {
                v = GetAdditionalString();
            } catch (Exception) {
                v = " ERROR_GETTING_ADDITIONAL_STRING";
            }
            return $"{{Socket {EPPair.ToString()} {base.State.ToString()}{v}}}";
        }

        public virtual string GetAdditionalString() => null;

        public override Task Close()
        {
            Logging.debug($"{this}: close");
            this.State = MyStreamState.Disposed;
            _fd = (IntPtr)(-1);
            Socket.Close();
            return NaiveUtils.CompletedTask;
        }

        public Task Dispose()
        {
            return Close();
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

        public static int DefaultReadaheadBufferSize { get; set; } = 256;
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
        public bool EnableSmartSyncRead { get; set; } = false;
        public bool Enable2ndBufferCheck { get; set; } = false;

        ReadFullRStateMachine readStateMachine;


        public Task ReadFullAsync(BytesSegment bs)
        {
            if (LengthCanSyncRead >= bs.Len) {
                var task = ReadAsyncR(bs, true);
                if (!(task.IsCompleted && task.GetResult() == bs.Len)) {
                    Logging.errorAndThrow($"BUG in ReadAsync(bs, true)! (bs.Len={bs.Len})");
                }
                return NaiveUtils.CompletedTask;
            } else {
                return ReadFullAsyncImpl(bs);
            }
        }

        public AwaitableWrapper ReadFullAsyncR(BytesSegment bs)
        {
            if (LengthCanSyncRead >= bs.Len) {
                var task = ReadAsyncR(bs, true);
                if (!(task.IsCompleted && task.GetResult() == bs.Len)) {
                    Logging.errorAndThrow($"BUG in ReadAsync(bs, true)! (bs.Len={bs.Len})");
                }
                return AwaitableWrapper.GetCompleted();
            } else {
                if (readStateMachine == null)
                    readStateMachine = new ReadFullRStateMachine();
                return new AwaitableWrapper(readStateMachine.Start(this, bs));
            }
        }

        public virtual int GetAvailable() => Socket.Available;

        protected virtual Task ReadFullAsyncImpl(BytesSegment bs)
        {
            return MyStreamExt.ReadFullImpl(this, bs);
        }

        public override Task<int> ReadAsync(BytesSegment bs)
        {
            // We use an internal read-ahead buffer to reduce many read() syscalls when bs.Len is very small.
            // It's important since there is a hardware bug named 'Meltdown' in Intel CPUs.
            // TESTED: This optimization made ReadAsync 20x faster when bs.Len == 4, 
            //         on Windows 10 x64 16299.192 with a laptop Haswell CPU.
            var ret = PreReadAsync(ref bs, false);
            if (ret > 0)
                return NaiveUtils.GetCachedTaskInt(ret);
            if ((ret = TryReadSync(bs)) > 0) {
                Interlocked.Increment(ref ctr.Rsync);
                return NaiveUtils.GetCachedTaskInt(ret);
            }
            Interlocked.Increment(ref ctr.Rasync);
            return ReadAsyncImpl(bs);
        }

        public AwaitableWrapper<int> ReadAsyncR(BytesSegment bs)
        {
            return ReadAsyncR(bs, false);
        }

        private AwaitableWrapper<int> ReadAsyncR(BytesSegment bs, bool full)
        {
            var ret = PreReadAsync(ref bs, full);
            if (ret > 0)
                return new AwaitableWrapper<int>(ret);
            if ((ret = TryReadSync(bs)) > 0) {
                Interlocked.Increment(ref ctr.Rsync);
                return new AwaitableWrapper<int>(ret);
            }
            Interlocked.Increment(ref ctr.Rasync);
            return ReadAsyncRImpl(bs);
        }

        protected virtual AwaitableWrapper<int> ReadAsyncRImpl(BytesSegment bs)
        {
            return new AwaitableWrapper<int>(ReadAsyncImpl(bs));
        }

        private int PreReadAsync(ref BytesSegment bs, bool full)
        {
            var bufRead = TryReadInternalBuffer(bs);
            if (bufRead > 0) {
                if ((full || (EnableSmartSyncRead && Enable2ndBufferCheck)) && bs.Len > bufRead && socketAvailable > 0) {
                    int read = ReadSync_SocketHaveAvailableData(bs.Sub(bufRead));
                    return (bufRead + read);
                } else {
                    return (bufRead);
                }
            }

            if (full) {
                // when bufRead == 0 && socketAvailable > 0
                int read = ReadSync_SocketHaveAvailableData(bs);
                return (read);
            }

            if (EnableSmartSyncRead || (EnableReadaheadBuffer && bs.Len < ReadaheadBufferSize)) {
                if (socketAvailable < bs.Len || socketAvailable < ReadaheadBufferSize) {
                    // Get the Available value from socket when needed.
                    socketAvailable = GetAvailable();
                }
                var bufRead2 = TryFillAndReadInternalBuffer(bs);
                if (bufRead2 > 0) {
                    return (bufRead2);
                }
                if (EnableSmartSyncRead && socketAvailable > 0) {
                    // If the receive buffer of OS is not empty,
                    // use sync operation to reduce async overhead.
                    // This optimization made ReadAsync 12x faster.
                    int read = ReadSync_SocketHaveAvailableData(bs);
                    return (read);
                }
            }
            return 0;
        }

        protected virtual int TryReadSync(BytesSegment bs)
        {
            return 0;
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
                if (read <= 0)
                    throw new Exception($"{this} should not happen: Socket.Receive() returns {read} when Socket.Available > 0.");
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
            if (read <= 0)
                throw new Exception($"{this} should not happen: Socket.Receive() returns {read} when Socket.Available > 0.");
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

        protected int ReadSocketDirectSync(BytesSegment bs)
        {
            int r;
            r = SocketReadImpl(bs);
            removeAvailable(r);
            return r;
        }

        protected virtual int SocketReadImpl(BytesSegment bs)
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
                throw new Exception($"{this} read() returned a unexpected value: {r} (underlyingcalls={EnableUnderlyingCalls}, {(underlyingReadImpl == null ? "no" : "have")} impl, platform={osPlatform})");
            return r;
        }

        public override Task WriteAsync(BytesSegment bs)
        {
            var rSync = TryWriteSync(bs);
            if (rSync == bs.Len) {
                Interlocked.Increment(ref ctr.Wsync);
                return NaiveUtils.CompletedTask;
            }
            bs.SubSelf(Math.Max(rSync, 0));
            return WriteAsyncImpl(bs);
        }

        public abstract Task WriteAsyncImpl(BytesSegment bs);

        public AwaitableWrapper WriteAsyncR(BytesSegment bs)
        {
            var rSync = TryWriteSync(bs);
            if (rSync == bs.Len) {
                Interlocked.Increment(ref ctr.Wsync);
                return AwaitableWrapper.GetCompleted();
            }
            bs.SubSelf(Math.Max(rSync, 0));
            return WriteAsyncRImpl(bs);
        }

        public virtual AwaitableWrapper WriteAsyncRImpl(BytesSegment bs)
        {
            return new AwaitableWrapper(WriteAsyncImpl(bs));
        }

        protected virtual int TryWriteSync(BytesSegment bs)
        {
            if (Socket.Poll(0, SelectMode.SelectWrite)) {
                return Socket.Send(bs.Bytes, bs.Offset, bs.Len, SocketFlags.None);
            }
            return 0;
        }

        public static bool EnableUnderlyingCalls { get; set; } = false;

        static void InitUnderlyingRead()
        {
            if (osPlatform == PlatformID.Unix) {
                underlyingReadImpl = (s, bs) => {
                    bs.CheckAsParameter();
                    return LinuxNative.RecvToBsThrows((int)s.Fd, bs, 0);
                };
            } else if (osPlatform == PlatformID.Win32NT) {
                underlyingReadImpl = (s, bs) => {
                    bs.CheckAsParameter();
                    int r;
                    unsafe {
                        fixed (byte* ptr = bs.Bytes) {
                            r = win_recv(s.Fd, ptr + bs.Offset, bs.Len, 0);
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

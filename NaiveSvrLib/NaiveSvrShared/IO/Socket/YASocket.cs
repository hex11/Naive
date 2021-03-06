﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Naive.HttpSvr;
using NaiveSocks.Linux;

namespace NaiveSocks
{
    /// <summary>
    /// Yet async socket wrapper using Linux epoll
    /// </summary>
    public class YASocket : SocketStream, IEpollHandler, IMyStreamNoBuffer, IMyStreamMultiBuffer
    {
        public static bool isX86 = true;
        public static bool Debug = false;

        private const EPOLL_EVENTS PollInEvents = EPOLL_EVENTS.IN | EPOLL_EVENTS.RDHUP | EPOLL_EVENTS.ET;

        public YASocket(Socket socket) : base(socket)
        {
            fd = Fd.ToInt32();
            EnableReadaheadBuffer = false;
            CreateFdR();
        }

        private int fd;

        // use two additional fd for thread-safe
        private int fdR = -1; // -1 when not exists
        private bool fdRAdded;

        private int fdW = -1; // -1 when not exists

        private EPOLL_EVENTS lastReadEvents;

        public override string GetAdditionalString()
        {
            int ava;
            try {
                ava = GetAvailable();
            } catch (Exception) {
                ava = -114514;
            }
            var str = " fd=" + fd + "/" + fdR + (ava != 0 ? " avail=" + ava : null) + " lastEv=" + lastReadEvents;
            if (readState > 0)
                str += " recving(" + readState + ", " + readArg + ")";
            if (raWrite.IsBeingListening)
                str += " sending";
            if (ready)
                str += " ready";
            return str;
        }

        private void CreateFdR()
        {
            fdR = DupFdThrows(fd);
        }

        private int DupFdThrows(int fd)
        {
            var r = Syscall.dup(fd);
            if (r < 0)
                throw Syscall.GetExceptionWithErrno(nameof(Syscall.dup));
            return r;
        }

        private static bool HasFlag(EPOLL_EVENTS t, EPOLL_EVENTS flag) => (t & flag) != 0;

        public void HandleEvent(Epoller s, int eFd, EPOLL_EVENTS e)
        {
            try {
                if (Debug)
                    Logging.debugForce(this + ": event " + e);
                if (s == GlobalEpoller) {
                    lastReadEvents = e;
                    BytesSegment bs;
                    int mode;
                    int arg;
                    lock (raRead) {
                        bs = bufRead;
                        mode = readState;
                        arg = readArg;
                        ready = true;
                    }
                    bool operating = mode != 0;
                    int r = 0;
                    Exception ex = null;
                    IPEndPoint ep = null;
                    bool closingFdR = false;
                    if (operating) {
                        if (fdR == -1) {
                            ex = GetClosedException();
                            goto END_ASYNC;
                        }
                        if (mode == 3 || mode == 4) {
                            bs = BufferPool.GlobalGet(arg);
                        }
                        var firstReading = true;
                        goto READ;
                        READAGAIN:
                        ex = null;
                        firstReading = false;
                        READ:
                        int errno;
                        if (mode == 4) {
                            r = Syscall.RecvToBsFrom(fdR, bs, ref ep, MSG_FLAGS.DONTWAIT, out errno);
                        } else {
                            r = ReadNonblocking(fdR, bs, out errno);
                        }
                        if ((mode == 3 || mode == 4) && r <= 0) {
                            BufferPool.GlobalPut(bs.Bytes);
                            bs.ResetSelf();
                        }
                        if (r < 0) {
                            if (IsUdp && HasFlag(e, EPOLL_EVENTS.HUP)) {
                                if (Debug)
                                    Logging.warning(this + ": udp recv() hup and error " + errno);
                                closingFdR = true;
                                lock (raRead) {
                                    State |= MyStreamState.Closed;
                                }
                            } else {
                                if (errno == 11) {
                                    if (firstReading)
                                        Logging.warning(this + ": EAGAIN after event " + e + ", ignored.");
                                    if (mode == 2)
                                        bufRead = bs;
                                    return;
                                }
                                if (Debug)
                                    Logging.warning(this + ": recv() error " + errno);
                                closingFdR = true;
                                lock (raRead) {
                                    if (HasFlag(e, EPOLL_EVENTS.ERR))
                                        State |= MyStreamState.Closed;
                                    else
                                        State |= MyStreamState.RemoteShutdown;
                                }
                            }
                            ex = Syscall.GetExceptionWithErrno(nameof(Syscall.recv), errno);
                        } else {
                            if (r == 0) {
                                lock (raRead) {
                                    State |= MyStreamState.RemoteShutdown;
                                    closingFdR = true;
                                }
                                if (mode == 2)
                                    ex = GetReadFullException(arg, arg - bs.Len);
                            } else { // r > 0
                                if (mode == 3) {
                                    bs.Len = r;
                                    // and return to async caller
                                } else if (mode == 2) {
                                    bs.SubSelf(r);
                                    if (bs.Len > 0) {
                                        goto READAGAIN;
                                    }
                                }
                            }
                        }
                    }
                    if (HasFlag(e, EPOLL_EVENTS.ERR | EPOLL_EVENTS.HUP)) {
                        closingFdR = true;
                    }
                    if (closingFdR || State.IsClosed) {
                        lock (raRead) {
                            if (closingFdR && fdR != -1) {
                                GlobalEpoller.RemoveFd(fdR);
                                fdRAdded = false;
                                fdR_Close_NoLock();
                            }
                            if (State.IsClosed) {
                                TryCleanUp_NoLock();
                            }
                        }
                    }
                    END_ASYNC:
                    if (operating) {
                        bufRead.ResetSelf();
                        readState = 0;
                        readArg = 0;
                        if (ex != null) {
                            if (Debug)
                                Logging.debugForce(this + ": recv() async throws " + ex.Message);
                            switch (mode) {
                                case 4:
                                    raReadFrom.TrySetException(ex);
                                    break;
                                case 3:
                                    raReadNB.TrySetException(ex);
                                    break;
                                case 2:
                                    raReadFull.TrySetException(ex);
                                    break;
                                case 1:
                                    raRead.TrySetException(ex);
                                    break;
                            }
                        } else {
                            if (Debug)
                                Logging.debugForce(this + ": recv() async " + r);
                            switch (mode) {
                                case 4:
                                    raReadFrom.TrySetResult(new ReceiveFromResult { From = ep, Buffer = bs.Sub(0, r) });
                                    break;
                                case 3:
                                    raReadNB.TrySetResult(bs);
                                    break;
                                case 2:
                                    raReadFull.TrySetResult(0);
                                    break;
                                case 1:
                                    raRead.TrySetResult(r);
                                    break;
                            }
                        }
                    }
                } else if (s == GlobalEpollerW) {
                    GlobalEpollerW.RemoveFd(fdW);
                    BytesSegment bs;
                    BytesView bv;
                    bool wrongState;
                    lock (raRead) {
                        bs = bufWrite;
                        bv = bufWriteM;
                        bufWrite.ResetSelf();
                        bufWriteM = null;
                        wrongState = State.HasShutdown;
                    }
                    if (wrongState) {
                        fdW_Close();
                        raWrite.SetException(GetStateException());
                        return;
                    }
                    Interlocked.Increment(ref ctr.Wasync);
                    if (bv == null) {
                        var r = Syscall.SendFromBs(fdW, bs, MSG_FLAGS.DONTWAIT);
                        if (r < 0) {
                            var errno = Syscall.GetErrno();
                            fdW_Close();
                            lock (raRead) {
                                State = MyStreamState.Closed;
                                TryCleanUp_NoLock();
                            }
                            var ex = Syscall.GetExceptionWithErrno(nameof(Syscall.write), errno);
                            raWrite.SetException(ex);
                        } else if (bs.Len != r) {
                            if (r == 0) {
                                fdW_Close();
                                raWrite.SetException(new Exception("send() returns 0, event: " + e));
                            } else {
                                bufWrite = bs.Sub(r);
                                GlobalEpollerW.AddFd(fdW, EPOLL_EVENTS.OUT | EPOLL_EVENTS.ONESHOT, this, true);
                            }
                        } else {
                            fdW_Close();
                            raWrite.SetResult(0);
                        }
                    } else {
                        if (Syscall.SendFromBv(fdW, ref bv, MSG_FLAGS.DONTWAIT, out var errno)) {
                            fdW_Close();
                            raWrite.SetResult(0);
                        } else if (errno != 0) {
                            fdW_Close();
                            var ex = Syscall.GetExceptionWithErrno(nameof(Syscall.write), errno);
                        } else {
                            bufWriteM = bv;
                            GlobalEpollerW.AddFd(fdW, EPOLL_EVENTS.OUT | EPOLL_EVENTS.ONESHOT, this, true);
                        }
                    }
                } else {
                    throw new Exception("unexpected source Epoller!");
                }
            } catch (Exception ex) {
                var ex2 = new Exception(this + " exception with EPOLL_EVENTS: " + e, ex);
                raRead.TrySetException(ex2);
                raWrite.TrySetException(ex2);
                throw ex2;
            }
        }

        void fdR_Close_NoLock()
        {
            if (fdRAdded) {
                throw new Exception("Should not close fdR when it's added to epoll.");
            }
            if (Syscall.close(fdR) != 0)
                throw Syscall.GetExceptionWithErrno("close");
            fdR = -1;
        }

        public override async Task WriteAsyncImpl(BytesSegment bs)
        {
            await WriteAsyncRImpl(bs);
        }

        protected override int TryWriteNonblocking(BytesSegment bs)
        {
            bs.CheckAsParameter();
            lock (raRead) {
                if (State.HasShutdown)
                    throw GetStateException();
                var r = Syscall.SendFromBs(fd, bs, MSG_FLAGS.DONTWAIT, out var errno);
                if (errno == 0) {
                    return r;
                } else if (errno == 11) {
                    return 0;
                } else {
                    State = MyStreamState.Closed;
                    TryCleanUp_NoLock();
                    throw Syscall.GetExceptionWithErrno("send", errno);
                }
            }
        }

        private ReusableAwaiter<VoidType> raWrite = new ReusableAwaiter<VoidType>();
        private BytesSegment bufWrite;
        private BytesView bufWriteM;

        public override AwaitableWrapper WriteAsyncRImpl(BytesSegment bs)
        {
            bs.CheckAsParameter();
            lock (raRead) {
                if (State.HasShutdown)
                    throw GetStateException();
                if (fdW != -1) {
                    string msg = this + ": another writing task is in progress.";
                    Logging.logWithStackTrace(msg, Logging.Level.Error);
                    Logging.warning("Continuation: " + raWrite.GetContinuationInfo());
                    throw new Exception(msg);
                }
                raWrite.Reset();
                bufWrite = bs;
                // Create fdW and add to epoll. fdW will be removed and closed by HandleEvent().
                fdW = DupFdThrows(fd);
                GlobalEpollerW.AddFd(fdW, EPOLL_EVENTS.OUT | EPOLL_EVENTS.ONESHOT, this);
            }
            return new AwaitableWrapper(raWrite);
        }

        public AwaitableWrapper WriteMultipleAsyncR(BytesView bv)
        {
            //return WriteAsyncR(bv.GetBytes());

            if (bv.nextNode == null) return WriteAsyncR(bv.Segment);
            foreach (var item in bv) {
                bv.Segment.CheckAsParameter();
            }
            lock (raRead) {
                if (State.HasShutdown)
                    throw GetStateException();
                if (Syscall.SendFromBv(fd, ref bv, MSG_FLAGS.DONTWAIT, out var errno)) {
                    Interlocked.Increment(ref ctr.Wsync);
                    return AwaitableWrapper.GetCompleted();
                }
                if (errno != 0 && errno != 11) throw Syscall.GetExceptionWithErrno("sendmsg", errno);
                if (fdW != -1) {
                    string msg = this + ": another writing task is in progress.";
                    Logging.logWithStackTrace(msg, Logging.Level.Error);
                    Logging.warning("Continuation: " + raWrite.GetContinuationInfo());
                    throw new Exception(msg);
                }
                raWrite.Reset();
                bufWriteM = bv;
                // Create fdW and add to epoll. fdW will be removed and closed by HandleEvent().
                fdW = DupFdThrows(fd);
                GlobalEpollerW.AddFd(fdW, EPOLL_EVENTS.OUT | EPOLL_EVENTS.ONESHOT, this);
            }
            return new AwaitableWrapper(raWrite);
        }

        public override AwaitableWrapper WriteToAsyncR(BytesSegment bs, IPEndPoint ep)
        {
            // simply try to send without blocking.
            if (ep.AddressFamily == AddressFamily.InterNetwork) {
                bs.CheckAsParameter();
                Syscall.SendFromBsTo(fd, bs, ep, MSG_FLAGS.DONTWAIT, out var errno);
                if (errno == 0) return AwaitableWrapper.GetCompleted();
            }

            // fallback to base implementation.
            return base.WriteToAsyncR(bs, ep);
        }

        void fdW_Close()
        {
            if (Syscall.close(fdW) == -1) {
                throw Syscall.GetExceptionWithErrno("close");
            }
            fdW = -1;
        }

        protected override async Task<int> ReadAsyncImpl(BytesSegment bs)
        {
            return await ReadAsyncRImpl(bs);
        }

        private ReusableAwaiter<int> raRead = new ReusableAwaiter<int>();
        private ReusableAwaiter<VoidType> raReadFull = new ReusableAwaiter<VoidType>();
        private ReusableAwaiter<BytesSegment> raReadNB = new ReusableAwaiter<BytesSegment>();
        private ReusableAwaiter<ReceiveFromResult> raReadFrom = new ReusableAwaiter<ReceiveFromResult>();
        private BytesSegment bufRead;

        private int readState;
        // 0. None
        // 1. ReadAsyncR
        // 2. ReadFullAsyncR
        // 3. ReadNBAsyncR
        // 4. ReadFromAsyncR

        private int readArg;

        protected override unsafe int TryReadNonblocking(BytesSegment bs)
        {
            if (!ready)
                return 0;
            lock (raRead) {
                if (fdR == -1) {
                    //throw GetClosedException();
                    return base.SocketReadImpl(bs);
                }
                if (ready) { // double checking
                    bs.CheckAsParameter();
                    var r = ReadNonblocking(bs);
                    if (r >= 0)
                        return r;
                }
            }
            return 0;
        }

        bool ready = true;

        private bool addFdToEpoller()
        {
            if (!fdRAdded) {
                fdRAdded = true;
                GlobalEpoller.AddFd(fdR, PollInEvents, this);
                // ensure the socket is not ready to read after AddFd, or events may never raise:
                ready = true;
                return true;
            }
            return false;
        }

        private void ReadPrecheck()
        {
            if (readState != 0)
                throw GetReadingInProgressException();
            if (State.HasRemoteShutdown)
                throw GetStateException();
        }

        protected override AwaitableWrapper<int> ReadAsyncRImpl(BytesSegment bs)
        {
            if (Debug)
                Logging.debugForce(this + ": ReadAsyncRImpl()");
            if (bs.Len <= 0)
                throw new ArgumentOutOfRangeException("bs.Len");
            bs.CheckAsParameter();
            lock (raRead) {
                ReadPrecheck();
                READ_AGAIN:
                if (ready) {
                    var r = ReadNonblocking(bs);
                    if (r != -1) // success or EOF.
                        return new AwaitableWrapper<int>(r);
                }
                if (addFdToEpoller()) goto READ_AGAIN;
                raRead.Reset();
                readState = 1;
                bufRead = bs;
                readArg = bs.Len; // only for ToString() to print the length
                if (Debug)
                    Logging.debugForce(this + ": wait for epoll");
            }
            return new AwaitableWrapper<int>(raRead);
        }

        public AwaitableWrapper<BytesSegment> ReadNBAsyncR(int maxSize)
        {
            if (Debug)
                Logging.debugForce(this + ": ReadAsyncRImpl()");
            if (maxSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxSize));
            lock (raRead) {
                ReadPrecheck();
                READ_AGAIN:
                if (ready) {
                    var bs = new BytesSegment(BufferPool.GlobalGet(maxSize));
                    var r = ReadNonblocking(bs);
                    if (r != -1) { // success or EOF.
                        bs.Len = r;
                        return new AwaitableWrapper<BytesSegment>(bs);
                    } else {
                        BufferPool.GlobalPut(bs.Bytes);
                    }
                }
                if (addFdToEpoller()) goto READ_AGAIN;
                raReadNB.Reset();
                readState = 3;
                readArg = maxSize;
                if (Debug)
                    Logging.debugForce(this + ": wait for epoll");
            }
            return new AwaitableWrapper<BytesSegment>(raReadNB);
        }

        public override AwaitableWrapper ReadFullAsyncR(BytesSegment bs)
        {
            if (Debug)
                Logging.debugForce(this + ": ReadFullAsyncR()");
            if (bs.Len <= 0)
                throw new ArgumentOutOfRangeException("bs.Len");
            bs.CheckAsParameter();
            lock (raRead) {
                ReadPrecheck();
                int r = 0;
                READ_AGAIN:
                if (ready) {
                    var syncR = ReadNonblocking(bs.Sub(r));
                    if (syncR != -1) {
                        if (syncR == 0) {
                            throw GetReadFullException(bs.Len, r);
                            // or it will hang.
                        }
                        r += syncR;
                        if (r == bs.Len) {
                            Interlocked.Increment(ref ctr.Rsync);
                            return AwaitableWrapper.GetCompleted();
                        } else {
                            goto READ_AGAIN;
                            // ensure the socket is not ready
                        }
                    }
                }
                if (addFdToEpoller()) goto READ_AGAIN;
                raReadFull.Reset();
                readState = 2;
                readArg = bs.Len;
                bufRead = bs.Sub(r);
                if (Debug)
                    Logging.debugForce(this + ": wait for epoll");
            }
            Interlocked.Increment(ref ctr.Rasync);
            return new AwaitableWrapper(raReadFull);
        }

        public override AwaitableWrapper<ReceiveFromResult> ReadFromAsyncR(int maxSize, IPEndPoint ep)
        {
            if (Debug)
                Logging.debugForce(this + ": ReadFromAsyncR()");
            if (maxSize <= 0)
                throw new ArgumentOutOfRangeException("maxSize");
            lock (raRead) {
                ReadPrecheck();
                READ_AGAIN:
                if (ready) {
                    var bs = BufferPool.GlobalGetBs(maxSize);
                    var r = ReadNonblockingFrom(bs, ref ep);
                    if (r != -1) // success or EOF.
                        return new AwaitableWrapper<ReceiveFromResult>(new ReceiveFromResult { From = ep, Buffer = bs.Sub(0, r) });
                    else
                        BufferPool.GlobalPut(bs.Bytes);
                }
                if (addFdToEpoller()) goto READ_AGAIN;
                raReadFrom.Reset();
                readState = 4;
                readArg = maxSize;
                if (Debug)
                    Logging.debugForce(this + ": wait for epoll");
            }
            return new AwaitableWrapper<ReceiveFromResult>(raReadFrom);
        }

        private int ReadNonblockingFrom(BytesSegment bs, ref IPEndPoint ep)
        {
            var r = Syscall.RecvToBsFrom(fd, bs, ref ep, MSG_FLAGS.DONTWAIT, out var errno);
            if (errno == 0) {
                if (Debug)
                    Logging.debugForce(this + ": recvfrom() " + r);
                return r;
            } else if (errno == 11) { // EAGAIN
                if (Debug)
                    Logging.debugForce(this + ": EAGAIN");
                ready = false;
                return -1;
            } else {
                if (Debug)
                    Logging.debugForce(this + ": recvfrom() throws " + errno);
                throw Syscall.GetExceptionWithErrno("recvfrom", errno);
            }
        }

        private int ReadNonblocking(BytesSegment bs)
        {
            var r = ReadNonblocking(fdR, bs, out var errno);
            if (errno == 0) {
                if (Debug)
                    Logging.debugForce(this + ": recv() " + r);
                return r;
            } else if (errno == 11) { // EAGAIN
                if (Debug)
                    Logging.debugForce(this + ": EAGAIN");
                ready = false;
                return -1;
            } else {
                if (Debug)
                    Logging.debugForce(this + ": recv() throws " + errno);
                throw Syscall.GetExceptionWithErrno("recv", errno);
            }
        }

        private static int ReadNonblocking(int fd, BytesSegment bs, out int errno)
        {
            int r = Syscall.RecvToBs(fd, bs, MSG_FLAGS.DONTWAIT);
            errno = r < 0 ? Syscall.GetErrno() : 0;
            return r;
        }

        protected override int SocketReadImpl(BytesSegment bs)
        {
            bs.CheckAsParameter();
            int fdRsync;
            lock (raRead) {
                var r = ReadNonblocking(fdR, bs, out var errno);
                if (errno == 0) {
                    return r;
                } else if (errno == 11) {
                    fdRsync = DupFdThrows(fd);
                } else {
                    throw Syscall.GetExceptionWithErrno("recv", errno);
                }
            }
            try {
                return Syscall.RecvToBsThrows(fdRsync, bs, 0);
            } finally {
                if (Syscall.close(fdRsync) == -1)
                    Logging.warning(this + " close fdRsync errno " + Syscall.GetErrno());
            }
        }

        public override Task Shutdown(SocketShutdown direction)
        {
            lock (raWrite) {
                int how;
                MyStreamState flag;
                switch (direction) {
                    case SocketShutdown.Receive:
                        how = 0;
                        flag = MyStreamState.RemoteShutdown;
                        break;
                    case SocketShutdown.Send:
                        how = 1;
                        flag = MyStreamState.LocalShutdown;
                        break;
                    case SocketShutdown.Both:
                        how = 2;
                        flag = MyStreamState.Closed;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(direction));
                }
                if ((State.Value | flag.Value) != State.Value) {
                    State |= flag;
                    if (Syscall.shutdown(fd, how) == -1) {
                        int errno = Syscall.GetErrno();
                        if (errno != 107) // ENOTCONN
                            Logging.warning(this + ": shutdown(how=" + how + ") errno " + errno);
                    }
                    TryCleanUp_NoLock();
                }
            }
            return NaiveUtils.CompletedTask;
        }

        public override Task Close()
        {
            lock (raRead) {
                State = MyStreamState.Closed;
                TryCleanUp_NoLock();
            }
            return base.Close();
        }

        bool cleanedUp = false;

        private void TryCleanUp_NoLock()
        {
            if (cleanedUp)
                return;
            cleanedUp = true;
            if (!fdRAdded && fdR != -1)
                fdR_Close_NoLock();
            var r = Syscall.shutdown(fd, 2);
            if (r == -1) {
                int errno = Syscall.GetErrno();
                if (errno != 107) // ENOTCONN
                    Logging.warning(this + ": shutdown() errno " + errno);
            }
        }

        private static Exception GetClosedException()
        {
            return new Exception("Socket closed");
        }

        private Exception GetStateException()
        {
            return new Exception("Socket state: " + State);
        }

        private static Exception GetReadFullException(int count, int pos)
        {
            return new Exception($"EOF when ReadFull() count={count} pos={pos}");
        }

        private Exception GetReadingInProgressException()
        {
            return new Exception($"A reading task is in progress. (mode={readState})");
        }

        public static Epoller GlobalEpoller => LazyGlobalEpoller.Value;

        public static Lazy<Epoller> LazyGlobalEpoller = new Lazy<Epoller>(() => {
            var e = new Epoller();
            e.Logger.Stamp = "epoll";
            e.InitEpoll();
            new Thread(e.Run) { Name = "GlobalEpoll" }.Start();
            return e;
        }, true);

        public static Epoller GlobalEpollerW => LazyGlobalEpollerW.Value;

        public static Lazy<Epoller> LazyGlobalEpollerW = new Lazy<Epoller>(() => {
            var e = new Epoller();
            e.Logger.Stamp = "epollW";
            e.InitEpoll();
            new Thread(e.Run) { Name = "GlobalEpollW" }.Start();
            return e;
        }, true);
    }

    public interface IEpollHandler
    {
        void HandleEvent(Epoller s, int fd, EPOLL_EVENTS e);
    }

    public class Epoller
    {
        static bool Debug => YASocket.Debug;
        private const int MAX_EVENTS = 16;

        private int ep;

        public IEpollHandler RunningHandler { get; private set; }

        public ContinuationRunner.Context ContRunnerContext { get; private set; }

        private ReaderWriterLockSlim mapLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private Dictionary<int, IEpollHandler> mapFdToHandler = new Dictionary<int, IEpollHandler>();
        private HashSet<int> fdCleanupList = new HashSet<int>();

        public Logger Logger = new Logger() { ParentLogger = Logging.RootLogger };

        public int[] GetFds() => mapFdToHandler.Keys.ToArray();

        public Dictionary<int, IEpollHandler> GetMap() => new Dictionary<int, IEpollHandler>(mapFdToHandler);

        public void InitEpoll()
        {
            ep = EpollCreate();
            Logger.debug("epoll_create succeed. fd=" + ep);
        }

        public void Run()
        {
            ContRunnerContext = ContinuationRunner.CheckCurrentContext();
            PollLoop(ep);
        }

        int cleanupWarningLastTime = -5;
        int cleanupWarningTimes = 0;

        public void AddFd(int fd, EPOLL_EVENTS events, IEpollHandler handler, bool noCleanupWarning = false)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (Debug)
                Logger.debugForce("AddFd " + fd + " " + events);

            var ev = new epoll_event {
                events = events,
                u32a = fd
            };
            mapLock.EnterWriteLock();
            try {
                if (fdCleanupList.Contains(fd) && !noCleanupWarning) {
                    var time = WebSocket.CurrentTimeRough;
                    cleanupWarningTimes++;
                    if (time - cleanupWarningLastTime >= 5) {
                        cleanupWarningLastTime = time;
                        Logger.warning("Adding fd " + fd + " (handler " + handler + "), which is in cleanupList (happend " + cleanupWarningTimes + " times)");
                    }
                }
                mapFdToHandler.Add(fd, handler);
                //mapFdToHandler[fd] = handler;
            } finally {
                mapLock.ExitWriteLock();
            }
            unsafe {
                if (Syscall.epoll_ctl(ep, EPOLL_CTL.ADD, fd, ev) != 0) {
                    Syscall.ThrowWithErrno(nameof(Syscall.epoll_ctl));
                }
            }
        }

        public void ModifyFd(int fd, EPOLL_EVENTS events)
        {
            if (Debug)
                Logger.debugForce("ModifyFd " + fd + " " + events);

            unsafe {
                var ev = new epoll_event {
                    events = events,
                    u32a = fd
                };
                if (Syscall.epoll_ctl(ep, EPOLL_CTL.MOD, fd, ev) != 0) {
                    Syscall.ThrowWithErrno(nameof(Syscall.epoll_ctl));
                }
            }
        }

        public int ModifyFdNotThrows(int fd, EPOLL_EVENTS events)
        {
            if (Debug)
                Logger.debugForce("ModifyFd " + fd + " " + events);

            unsafe {
                var ev = new epoll_event {
                    events = events,
                    u32a = fd
                };
                if (Syscall.epoll_ctl(ep, EPOLL_CTL.MOD, fd, ev) != 0) {
                    return Syscall.GetErrno();
                }
            }
            return 0;
        }

        public void RemoveFd(int fd)
        {
            if (Debug)
                Logger.debugForce("RemoveFd " + fd);

            mapLock.EnterWriteLock();
            try {
                unsafe {
                    var ev = default(epoll_event);
                    if (Syscall.epoll_ctl(ep, EPOLL_CTL.DEL, fd, ev) != 0) {
                        Syscall.ThrowWithErrno(nameof(Syscall.epoll_ctl));
                    }
                }

                mapFdToHandler.Remove(fd);
                fdCleanupList.Add(fd);
            } finally {
                mapLock.ExitWriteLock();
            }
        }

        public int RemoveFdNotThrows(int fd)
        {
            if (Debug)
                Logger.debugForce("RemoveFdNotThrows " + fd);

            mapLock.EnterWriteLock();
            try {
                unsafe {
                    var ev = default(epoll_event);
                    if (Syscall.epoll_ctl(ep, EPOLL_CTL.DEL, fd, ev) != 0) {
                        var errno = Syscall.GetErrno();
                        Logger.warning("RemoveFdNotThrows " + fd + " error " + errno + " " + Syscall.GetErrString(errno));
                        return errno;
                    }
                }

                mapFdToHandler.Remove(fd);
                fdCleanupList.Add(fd);
            } finally {
                mapLock.ExitWriteLock();
            }
            return 0;
        }

        private unsafe void PollLoop(int ep)
        {
            if (ep < 0)
                throw new Exception("epoll fd does not exist.");
            if (Debug)
                Logger.debugForce("PollLoop running");
            if (YASocket.isX86)
                PollLoop_size12(ep);
            else
                PollLoop_size16(ep);
        }

        private unsafe void PollLoop_size12(int ep)
        {
            ContinuationRunner.Context.Begin();
            var events = stackalloc epoll_event[MAX_EVENTS];
            while (true) {
                var eventCount = Syscall.epoll_wait(ep, events, MAX_EVENTS, -1);
                if (eventCount < 0) {
                    var errno = Syscall.GetErrno();
                    if (errno == 4) // Interrupted system call
                        continue;
                    Syscall.ThrowWithErrno(nameof(Syscall.epoll_wait), errno);
                }
                if (Debug)
                    Logger.debugForce("eventcount " + eventCount);
                for (int i = 0; i < eventCount; i++) {
                    var e = events[i];
                    var eventType = e.events;
                    bool cleanedUp;
                    mapLock.EnterReadLock();
                    var fd = e.u32a;
                    bool ok = mapFdToHandler.TryGetValue(fd, out var handler);
                    cleanedUp = fdCleanupList.Contains(fd);
                    mapLock.ExitReadLock();
                    if (cleanedUp & !ok) {
                        Logger.warning($"EpollHandler event after cleaned up: ({i}/{eventCount}) fd={fd} [{eventType}] u64=[{e.u64:X}]");
                    } else if (!ok) {
                        Logger.warning($"EpollHandler not found! event({i}/{eventCount}) fd={fd} [{eventType}] u64=[{e.u64:X}]");
                    } else { // ok
                        if (cleanedUp) {
                            Logger.warning($"EpollHandler cleaned up but handler found: ({i}/{eventCount}) fd={fd} [{eventType}] u64=[{e.u64:X}]");
                        }
                        RunningHandler = handler;
                        handler.HandleEvent(this, fd, eventType);
                        ContinuationRunner.Context.Checkpoint();
                    }
                }
                RunningHandler = null;
                mapLock.EnterWriteLock();
                fdCleanupList.Clear();
                mapLock.ExitWriteLock();
            }
        }

        private unsafe void PollLoop_size16(int ep)
        {
            ContinuationRunner.Context.Begin();
            var events = stackalloc epoll_event_16[MAX_EVENTS];
            while (true) {
                var eventCount = Syscall.epoll_wait(ep, events, MAX_EVENTS, -1);
                if (eventCount < 0) {
                    var errno = Syscall.GetErrno();
                    if (errno == 4) // Interrupted system call
                        continue;
                    Syscall.ThrowWithErrno(nameof(Syscall.epoll_wait), errno);
                }
                if (Debug)
                    Logger.debugForce("eventcount " + eventCount);
                for (int i = 0; i < eventCount; i++) {
                    var e = events[i];
                    var eventType = e.events;
                    bool cleanedUp;
                    mapLock.EnterReadLock();
                    var fd = e.u32a;
                    bool ok = mapFdToHandler.TryGetValue(fd, out var handler);
                    cleanedUp = fdCleanupList.Contains(fd);
                    mapLock.ExitReadLock();
                    if (cleanedUp & !ok) {
                        Logger.warning($"EpollHandler event after cleaned up: ({i}/{eventCount}) fd={fd} [{eventType}] u64=[{e.u64:X}]");
                    } else if (!ok) {
                        Logger.warning($"EpollHandler not found! event({i}/{eventCount}) fd={fd} [{eventType}] u64=[{e.u64:X}]");
                    } else { // ok
                        if (cleanedUp) {
                            Logger.warning($"EpollHandler cleaned up but handler found: ({i}/{eventCount}) fd={fd} [{eventType}] u64=[{e.u64:X}]");
                        }
                        RunningHandler = handler;
                        handler.HandleEvent(this, fd, eventType);
                        ContinuationRunner.Context.Checkpoint();
                    }
                }
                RunningHandler = null;
                mapLock.EnterWriteLock();
                fdCleanupList.Clear();
                mapLock.ExitWriteLock();
            }
        }

        private static int EpollCreate()
        {
            var ep = Syscall.epoll_create(MAX_EVENTS);
            if (ep < 0) {
                Syscall.ThrowWithErrno(nameof(Syscall.epoll_create));
            }

            return ep;
        }
    }

    namespace Linux
    {
        internal static class Syscall
        {
            private const string LIBC = "libc";

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int read([In]int fd, [In]byte* buf, [In]int count);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int write([In]int fd, [In]byte* buf, [In]int count);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int recv(int sockfd, byte* buf, int count, int flags);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int send(int sockfd, byte* buf, int count, int flags);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int sendmsg(int sockfd, msghdr* msg, int flags);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int recvfrom(int sockfd, void* buf, uint len, int flags, void* src_addr, uint* addrlen);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int sendto(int sockfd, void* buf, uint len, int flags, void* dest_addr, uint* addrlen);

            [DllImport(LIBC, SetLastError = true)]
            public static extern int dup([In]int fd);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int pipe2(int* pipefd, int flags);

            [DllImport(LIBC, SetLastError = true)]
            public static extern int close([In]int fd);

            [DllImport(LIBC, SetLastError = true)]
            public static extern int shutdown(int sockfd, int how);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int ioctl([In]int fd, [In]uint request, void* ptr);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int fcntl(int fd, int cmd, void* arg);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int poll(pollfd* fds, uint nfd, int timeout);

            [DllImport(LIBC, SetLastError = true)]
            public static extern int epoll_create([In]int size);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int epoll_wait(int fileDescriptor, void* events, int maxevents, int timeout);

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern int epoll_ctl(int epFd, EPOLL_CTL op, int fd, void* ev);

            public unsafe static int epoll_ctl(int epFd, EPOLL_CTL op, int fd, epoll_event ev)
            {
                if (YASocket.isX86) {
                    return epoll_ctl(epFd, op, fd, &ev);
                } else {
                    epoll_event_16 evnx86 = new epoll_event_16 { events = ev.events, u64 = ev.u64 };
                    return epoll_ctl(epFd, op, fd, &evnx86);
                }
            }

            [DllImport(LIBC, SetLastError = true)]
            public unsafe static extern sbyte* strerror([In]int errnum);

            public static int GetErrno() => Marshal.GetLastWin32Error();

            public static string GetErrString(int errno)
            {
                string errStr = " (failed to get error string)";
                unsafe {
                    var ptr = strerror(errno);
                    if (ptr != (byte*)0) {
                        errStr = new string(ptr);
                    } else {
                        errStr = errno + errStr;
                    }
                }
                return errStr;
            }

            public static void Pipe2(out int fdRead, out int fdWrite, bool blocking = false)
            {
                unsafe {
                    var fds = stackalloc int[2];
                    if (pipe2(fds, blocking ? 0 : 0x0004 /* O_NONBLOCK */) != 0)
                        throw GetExceptionWithErrno("pipe2");
                    fdRead = fds[0];
                    fdWrite = fds[1];
                }
            }

            public static unsafe int RecvToBs(int fd, BytesSegment bs, MSG_FLAGS flags)
            {
                fixed (byte* buf = &bs.Bytes[bs.Offset]) {
                    return recv(fd, buf, bs.Len, (int)flags);
                }
            }

            public static unsafe int RecvToBs(int fd, BytesSegment bs, MSG_FLAGS flags, out int errno)
            {
                errno = 0;
                var r = RecvToBs(fd, bs, flags);
                if (r == -1)
                    errno = GetErrno();
                return r;
            }

            public static unsafe int RecvToBsThrows(int fd, BytesSegment bs, MSG_FLAGS flags)
            {
                RETRY:
                var r = RecvToBs(fd, bs, flags);
                if (r < 0) {
                    var errno = GetErrno();
                    if (errno == 4 /* EINTR */ ) goto RETRY;
                    ThrowWithErrno("recv", errno);
                }
                return r;
            }

            public static unsafe int RecvToBsFrom(int fd, BytesSegment bs, ref IPEndPoint ep, MSG_FLAGS flags, out int errno)
            {
                fixed (byte* buf = &bs.Bytes[bs.Offset]) {
                    sockaddr_in addr;
                    uint addrlen = (uint)sizeof(sockaddr_in);
                    var r = recvfrom(fd, buf, (uint)bs.Len, (int)flags, &addr, &addrlen);
                    errno = r < 0 ? GetErrno() : 0;
                    if (addrlen > 0 && addr.sin_family == 2 /* AF_INET */) {
                        ep = new IPEndPoint(addr.sin_addr, SwapEndian(addr.sin_port));
                    } else { // TODO: IPv6 support
                        ep = new IPEndPoint(IPAddress.None, 0);
                    }
                    return r;
                }
            }

            public static unsafe int SendFromBsTo(int fd, BytesSegment bs, IPEndPoint ep, MSG_FLAGS flags, out int errno)
            {
                fixed (byte* buf = &bs.Bytes[bs.Offset]) {
                    sockaddr_in addr = new sockaddr_in {
                        sin_family = 2,
                        sin_addr = (uint)ep.Address.Address,
                        sin_port = SwapEndian((ushort)ep.Port)
                    };
                    // TODO: IPv6 support
                    uint addrlen = (uint)sizeof(sockaddr_in);
                    var r = sendto(fd, buf, (uint)bs.Len, (int)flags, &addr, &addrlen);
                    errno = r < 0 ? GetErrno() : 0;
                    return r;
                }
            }

            static ushort SwapEndian(ushort val)
            {
                return (ushort)((val & 0xff) << 8
                    | (val & 0xff00) >> 8);
            }

            public static unsafe int SendFromBs(int fd, BytesSegment bs, MSG_FLAGS flags, out int errno)
            {
                errno = 0;
                var r = SendFromBs(fd, bs, flags);
                if (r == -1)
                    errno = GetErrno();
                return r;
            }

            public static unsafe int SendFromBs(int fd, BytesSegment bs, MSG_FLAGS flags)
            {
                fixed (byte* buf = &bs.Bytes[bs.Offset]) {
                    return send(fd, buf, bs.Len, (int)flags);
                }
            }

            // returns true if all data sent.
            public static unsafe bool SendFromBv(int fd, ref BytesView bv, MSG_FLAGS flags, out int errno)
            {
                int bufcount = 0;
                int bytecount = 0;
                foreach (var item in bv) {
                    if (item.len <= 0) continue;
                    bufcount++;
                    bytecount += item.len;
                }
                msghdr msg = new msghdr();
                var arrIov = stackalloc iovec[bufcount];
                msg.msg_iov = arrIov;
                msg.msg_iovlen = bufcount;
                var arrGch = stackalloc GCHandle[bufcount];
                {
                    int i = 0;
                    foreach (var item in bv) {
                        if (item.len <= 0) continue;
                        var h = arrGch[i] = GCHandle.Alloc(item.bytes, GCHandleType.Pinned);
                        arrIov[i] = new iovec {
                            iov_base = (byte*)h.AddrOfPinnedObject() + item.offset,
                            iov_len = (uint)item.len
                        };
                        i++;
                    }
                }
                var w = sendmsg(fd, &msg, (int)flags);
                errno = w == -1 ? Syscall.GetErrno() : 0;
                for (int i = 0; i < bufcount; i++) {
                    arrGch[i].Free();
                }
                if (w != bytecount) {
                    var cur = 0;
                    while (cur < w) {
                        if (cur + bv.len <= w) {
                            cur += bv.len;
                            bv = bv.nextNode;
                        } else {
                            bv = bv.Clone();
                            bv.SubSelf(w - cur);
                            break;
                        }
                    }
                }
                //Logging.debugForce($"sendmsg sent {w} ({bufcount} bufs), remaining {bytecount - w}, total {bytecount}, errno " + errno);
                return w == bytecount;
            }

            public static void ThrowWithErrno(string funcName)
            {
                throw GetExceptionWithErrno(funcName);
            }

            public static void ThrowWithErrno(string funcName, int errno)
            {
                throw GetExceptionWithErrno(funcName, errno);
            }

            public static Exception GetExceptionWithErrno(string funcName)
            {
                return GetExceptionWithErrno(funcName, GetErrno());
            }

            public static Exception GetExceptionWithErrno(string funcName, int errno)
            {
                return new Exception(funcName + " error " + errno + ": " + GetErrString(errno));
            }
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 12)]
        internal struct epoll_event // for x86 and x86-64
        {
            [FieldOffset(0)]
            public EPOLL_EVENTS events;      /* Epoll events */

            [FieldOffset(4)]
            public long u64;      /* User data variable */

            [FieldOffset(4)]
            public int u32a;

            [FieldOffset(8)]
            public int u32b;
        }

        [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 16)]
        internal struct epoll_event_16
        {
            [FieldOffset(0)]
            public EPOLL_EVENTS events;

            [FieldOffset(8)]
            public long u64;

            [FieldOffset(8)]
            public int u32a;

            [FieldOffset(12)]
            public int u32b;
        }

        internal enum EPOLL_CTL
        {
            ADD = 1,
            DEL = 2,
            MOD = 3
        }

        [Flags]
        public enum EPOLL_EVENTS : uint
        {
            IN = 0x001,
            PRI = 0x002,
            OUT = 0x004,
            RDNORM = 0x040,
            RDBAND = 0x080,
            WRNORM = 0x100,
            WRBAND = 0x200,
            MSG = 0x400,
            ERR = 0x008,
            HUP = 0x010,
            RDHUP = 0x2000,
            EXCLUSIVE = 1u << 28,
            WAKEUP = 1u << 29,
            ONESHOT = 1u << 30,
            ET = 1u << 31
        }

        struct pollfd
        {
            public int fd;         /* file descriptor */
            public POLL_EVENTS events;   /* requested events */
            public POLL_EVENTS revents;  /* returned events */
        }

        [Flags]
        public enum POLL_EVENTS : uint
        {
            IN = 0x0001,
            PRI = 0x0002,
            OUT = 0x0004,
            ERR = 0x0008,
            HUP = 0x0010,
            NVAL = 0x0020
        }

        public unsafe struct sockaddr_in
        {
            public short sin_family;
            public ushort sin_port;
            public uint sin_addr;
            public fixed byte sin_zero[8];
        }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct msghdr
        {
            public void* msg_name;
            public uint msg_namelen;

            public iovec* msg_iov;
            public int msg_iovlen;

            public void* msg_control;
            public int msg_controllen;

            public MSG_FLAGS msg_flags;
        }

        public unsafe struct iovec
        {
            public void* iov_base;
            public uint iov_len;
        }

        [Flags]
        public enum MSG_FLAGS : int
        {
            OOB = 1,
            PEEK = 2,
            DONTROUTE = 4,
            TRYHARD = 4,       /* Synonym for MSG_DONTROUTE for DECnet */
            CTRUNC = 8,
            PROBE = 0x10,   /* Do not send. Only probe path f.e. for MTU */
            TRUNC = 0x20,
            DONTWAIT = 0x40,    /* Nonblocking io		 */
            EOR = 0x80, /* End of record */
            WAITALL = 0x100,    /* Wait for a full request */
            FIN = 0x200,
            SYN = 0x400,
            CONFIRM = 0x800,    /* Confirm path validity */
            RST = 0x1000,
            ERRQUEUE = 0x2000,  /* Fetch message from error queue */
            NOSIGNAL = 0x4000,  /* Do not generate SIGPIPE */
            MORE = 0x8000,  /* Sender will send more */
            WAITFORONE = 0x10000,   /* recvmmsg(): block until 1+ packets avail */
            SENDPAGE_NOTLAST = 0x20000, /* sendpage() internal : not the last page */
            BATCH = 0x40000, /* sendmmsg(): more messages coming */
            EOF = FIN,
            NO_SHARED_FRAGS = 0x80000, /* sendpage() internal : page frags are not shared */
            ZEROCOPY = 0x4000000,   /* Use user data in kernel path */
            FASTOPEN = 0x20000000,  /* Send data in TCP SYN */
            CMSG_CLOEXEC = 0x40000000,  /* Set close_on_exec for file
					   descriptor received through
					   SCM_RIGHTS */
        }
    }
}

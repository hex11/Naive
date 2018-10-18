using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class YASocket : SocketStream, IEpollHandler, IMyStreamReadFullR
    {
        public static bool isX86 = true;
        public static bool Debug = false;

        private const EPOLL_EVENTS PollInEvents = EPOLL_EVENTS.IN | EPOLL_EVENTS.RDHUP | EPOLL_EVENTS.ET;

        public YASocket(Socket socket) : base(socket)
        {
            EnableReadaheadBuffer = false;
            CreateFdR();
        }

        private int fd => Fd.ToInt32();

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
            if (raRead.IsBeingListening)
                str += " recving";
            if (raReadFull.IsBeingListening)
                str += " recvingF";
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
            var r = LinuxNative.dup(fd);
            if (r < 0)
                throw LinuxNative.GetExceptionWithErrno(nameof(LinuxNative.dup));
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
                    int readFull;
                    lock (raRead) {
                        bs = bufRead;
                        readFull = readFullCount;
                        bufRead.ResetSelf();
                        ready = true;
                    }
                    bool operating = bs.Bytes != null;
                    int r = 0;
                    Exception ex = null;
                    bool closingFdR = false;
                    if (operating) {
                        if (fdR == -1) {
                            ex = GetClosedException();
                            goto CALLBACK;
                        }
                        var notFirstReading = false;
                        goto READ;
                        READAGAIN:
                        ex = null;
                        notFirstReading = true;
                        READ:
                        r = ReadNonblocking(fdR, bs, out var errno);
                        if (r < 0) {
                            if (errno == 11) {
                                if (!notFirstReading)
                                    Logging.warning(this + ": EAGAIN after event " + e + ", ignored.");
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
                            ex = LinuxNative.GetExceptionWithErrno(nameof(LinuxNative.recv), errno);
                        } else {
                            if (r == 0) {
                                lock (raRead) {
                                    State |= MyStreamState.RemoteShutdown;
                                    closingFdR = true;
                                }
                                if (readFull > 0)
                                    ex = new Exception($"EOF when ReadFull() count={readFull} pos={readFull - bs.Len}");
                            } else { // r > 0
                                if (readFull > 0) {
                                    bs.SubSelf(r);
                                    if (bs.Len > 0) {
                                        goto READAGAIN;
                                    }
                                }
                            }
                        }
                    } else {
                        //throw new Exception("should not happen: bs.Bytes == null");
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
                    CALLBACK:
                    if (operating) {
                        if (ex != null) {
                            if (Debug)
                                Logging.debugForce(this + ": recv() async throws " + ex.Message);
                            if (readFull == 0)
                                raRead.TrySetException(ex);
                            else
                                raReadFull.TrySetException(ex);
                        } else {
                            if (Debug)
                                Logging.debugForce(this + ": recv() async " + r);
                            if (readFull == 0)
                                raRead.TrySetResult(r);
                            else
                                raReadFull.TrySetResult(0);
                        }
                    }
                } else if (s == GlobalEpollerW) {
                    GlobalEpollerW.RemoveFd(fdW);
                    int r;
                    BytesSegment bs;
                    bool wrongState;
                    lock (raRead) {
                        bs = bufWrite;
                        bufWrite.ResetSelf();
                        wrongState = State.HasShutdown;
                    }
                    if (wrongState) {
                        fdW_Close();
                        raWrite.SetException(GetStateException());
                        return;
                    }
                    Interlocked.Increment(ref ctr.Wasync);
                    r = LinuxNative.SendFromBs(fdW, bs, MSG_FLAGS.DONTWAIT);
                    if (r < 0) {
                        var errno = LinuxNative.GetErrno();
                        fdW_Close();
                        lock (raRead) {
                            State = MyStreamState.Closed;
                            TryCleanUp_NoLock();
                        }
                        var ex = LinuxNative.GetExceptionWithErrno(nameof(LinuxNative.write), errno);
                        raWrite.SetException(ex);
                    } else if (bs.Len != r) {
                        if (r == 0) {
                            fdW_Close();
                            raWrite.SetException(new Exception("send() returns 0, event: " + e));
                        } else {
                            bufWrite = bs.Sub(r);
                            GlobalEpollerW.AddFd(fdW, EPOLL_EVENTS.OUT | EPOLL_EVENTS.ONESHOT, this);
                        }
                    } else {
                        fdW_Close();
                        raWrite.SetResult(0);
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
            if (LinuxNative.close(fdR) != 0)
                throw LinuxNative.GetExceptionWithErrno("close");
            fdR = -1;
        }

        public override async Task WriteAsyncImpl(BytesSegment bs)
        {
            await WriteAsyncRImpl(bs);
        }

        protected override int TryWriteSync(BytesSegment bs)
        {
            bs.CheckAsParameter();
            lock (raRead) {
                if (State.HasShutdown)
                    throw GetStateException();
                var r = LinuxNative.SendFromBs(fd, bs, MSG_FLAGS.DONTWAIT, out var errno);
                if (errno == 0) {
                    return r;
                } else if (errno == 11) {
                    return 0;
                } else {
                    State = MyStreamState.Closed;
                    TryCleanUp_NoLock();
                    throw LinuxNative.GetExceptionWithErrno("send", errno);
                }
            }
        }

        private ReusableAwaiter<VoidType> raWrite = new ReusableAwaiter<VoidType>();
        private BytesSegment bufWrite;

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

        void fdW_Close()
        {
            if (LinuxNative.close(fdW) == -1) {
                throw LinuxNative.GetExceptionWithErrno("close");
            }
            fdW = -1;
        }

        protected override async Task<int> ReadAsyncImpl(BytesSegment bs)
        {
            return await ReadAsyncRImpl(bs);
        }

        private ReusableAwaiter<int> raRead = new ReusableAwaiter<int>();
        private ReusableAwaiter<VoidType> raReadFull = new ReusableAwaiter<VoidType>();
        private BytesSegment bufRead;
        private int readFullCount;

        protected override unsafe int TryReadSync(BytesSegment bs)
        {
            if (!ready)
                return 0;
            lock (raRead) {
                if (fdR == -1) {
                    //throw GetClosedException();
                    return base.SocketReadImpl(bs);
                }
                if (ready) { // double checking
                    var r = ReadNonblocking(bs);
                    if (r > 0)
                        return r;
                }
            }
            return 0;
        }

        bool ready = true;

        protected override AwaitableWrapper<int> ReadAsyncRImpl(BytesSegment bs)
        {
            if (Debug)
                Logging.debugForce(this + ": ReadAsyncRImpl()");
            if (bs.Len <= 0)
                throw new ArgumentOutOfRangeException("bs.Len");
            bs.CheckAsParameter();
            lock (raRead) {
                if (State.HasRemoteShutdown)
                    throw GetStateException();
                READ_AGAIN:
                if (ready) {
                    var r = ReadNonblocking(bs);
                    if (r > 0)
                        return new AwaitableWrapper<int>(r);
                }
                if (!fdRAdded) {
                    fdRAdded = true;
                    GlobalEpoller.AddFd(fdR, PollInEvents, this);
                    // ensure the socket is not ready to read after AddFd, or events may never raise:
                    ready = true;
                    goto READ_AGAIN;
                }
                raRead.Reset();
                readFullCount = 0;
                bufRead = bs;
                if (Debug)
                    Logging.debugForce(this + ": wait for epoll");
            }
            return new AwaitableWrapper<int>(raRead);
        }

        public override async Task ReadFullAsyncImpl(BytesSegment bs)
        {
            await ReadFullAsyncR(bs);
            return;
        }

        public AwaitableWrapper ReadFullAsyncR(BytesSegment bs)
        {
            if (Debug)
                Logging.debugForce(this + ": ReadFullAsyncR()");
            if (bs.Len <= 0)
                throw new ArgumentOutOfRangeException("bs.Len");
            bs.CheckAsParameter();
            lock (raRead) {
                if (State.HasRemoteShutdown)
                    throw GetStateException();
                int r = 0;
                READ_AGAIN:
                if (ready) {
                    r += ReadNonblocking(bs);
                    if (r == bs.Len)
                        return AwaitableWrapper.GetCompleted();
                }
                if (!fdRAdded) {
                    fdRAdded = true;
                    GlobalEpoller.AddFd(fdR, PollInEvents, this);
                    // ensure the socket is not ready to read after AddFd, or events may never raise:
                    ready = true;
                    goto READ_AGAIN;
                }
                raReadFull.Reset();
                readFullCount = bs.Len;
                bufRead = bs.Sub(r);
                if (Debug)
                    Logging.debugForce(this + ": wait for epoll");
            }
            return new AwaitableWrapper(raReadFull);
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
                return 0;
            } else {
                if (Debug)
                    Logging.debugForce(this + ": recv() throws " + errno);
                throw LinuxNative.GetExceptionWithErrno("recv", errno);
            }
        }

        private static int ReadNonblocking(int fd, BytesSegment bs, out int errno)
        {
            int r = LinuxNative.RecvToBs(fd, bs, MSG_FLAGS.DONTWAIT);
            errno = r < 0 ? LinuxNative.GetErrno() : 0;
            return r;
        }

        protected override int SocketReadImpl(BytesSegment bs)
        {
            int fdRsync;
            lock (raRead) {
                var r = ReadNonblocking(fdR, bs, out var errno);
                if (errno == 0) {
                    return r;
                } else if (errno == 11) {
                    fdRsync = DupFdThrows(fd);
                } else {
                    throw LinuxNative.GetExceptionWithErrno("recv", errno);
                }
            }
            try {
                return LinuxNative.RecvToBsThrows(fdRsync, bs, 0);
            } finally {
                if (LinuxNative.close(fdRsync) == -1)
                    Logging.warning(this + " close fdRsync errno " + LinuxNative.GetErrno());
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
                    if (LinuxNative.shutdown(fd, how) == -1)
                        Logging.warning(this + ": shutdown(how=" + how + ") errno " + LinuxNative.GetErrno());
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
            var r = LinuxNative.shutdown(fd, 2);
            if (r == -1) {
                int errno = LinuxNative.GetErrno();
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

        public static Epoller GlobalEpoller => LazyGlobalEpoller.Value;

        public static Lazy<Epoller> LazyGlobalEpoller = new Lazy<Epoller>(() => {
            var e = new Epoller();
            e.InitEpoll();
            new Thread(e.Run) { Name = "GlobalEpoll" }.Start();
            return e;
        }, true);

        public static Epoller GlobalEpollerW => LazyGlobalEpollerW.Value;

        public static Lazy<Epoller> LazyGlobalEpollerW = new Lazy<Epoller>(() => {
            var e = new Epoller();
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

        private ReaderWriterLockSlim mapLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private Dictionary<int, IEpollHandler> mapFdToHandler = new Dictionary<int, IEpollHandler>();
        private HashSet<int> fdCleanupList = new HashSet<int>();

        private Logger Logger = new Logger() { ParentLogger = Logging.RootLogger };

        public int[] GetFds() => mapFdToHandler.Keys.ToArray();

        public Dictionary<int, IEpollHandler> GetMap() => new Dictionary<int, IEpollHandler>(mapFdToHandler);

        public void InitEpoll()
        {
            ep = EpollCreate();
            Logger.Stamp = "ep" + ep;
            Logger.debug("epoll_create succeed.");
        }

        public void Run()
        {
            PollLoop(ep);
        }

        public void AddFd(int fd, EPOLL_EVENTS events, IEpollHandler handler)
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
                mapFdToHandler.Add(fd, handler);
                //mapFdToHandler[fd] = handler;
            } finally {
                mapLock.ExitWriteLock();
            }
            unsafe {
                if (LinuxNative.epoll_ctl(ep, EPOLL_CTL.ADD, fd, ev) != 0) {
                    LinuxNative.ThrowWithErrno(nameof(LinuxNative.epoll_ctl));
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
                if (LinuxNative.epoll_ctl(ep, EPOLL_CTL.MOD, fd, ev) != 0) {
                    LinuxNative.ThrowWithErrno(nameof(LinuxNative.epoll_ctl));
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
                if (LinuxNative.epoll_ctl(ep, EPOLL_CTL.MOD, fd, ev) != 0) {
                    return LinuxNative.GetErrno();
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
                    if (LinuxNative.epoll_ctl(ep, EPOLL_CTL.DEL, fd, ev) != 0) {
                        LinuxNative.ThrowWithErrno(nameof(LinuxNative.epoll_ctl));
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
                    if (LinuxNative.epoll_ctl(ep, EPOLL_CTL.DEL, fd, ev) != 0) {
                        var errno = LinuxNative.GetErrno();
                        Logger.warning("RemoveFdNotThrows " + fd + " error " + errno + " " + LinuxNative.GetErrString(errno));
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
            var events = stackalloc epoll_event[MAX_EVENTS];
            while (true) {
                var eventCount = LinuxNative.epoll_wait(ep, events, MAX_EVENTS, -1);
                if (eventCount < 0) {
                    var errno = LinuxNative.GetErrno();
                    if (errno == 4) // Interrupted system call
                        continue;
                    LinuxNative.ThrowWithErrno(nameof(LinuxNative.epoll_wait), errno);
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
            var events = stackalloc epoll_event_16[MAX_EVENTS];
            while (true) {
                var eventCount = LinuxNative.epoll_wait(ep, events, MAX_EVENTS, -1);
                if (eventCount < 0) {
                    var errno = LinuxNative.GetErrno();
                    if (errno == 4) // Interrupted system call
                        continue;
                    LinuxNative.ThrowWithErrno(nameof(LinuxNative.epoll_wait), errno);
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
            var ep = LinuxNative.epoll_create(MAX_EVENTS);
            if (ep < 0) {
                LinuxNative.ThrowWithErrno(nameof(LinuxNative.epoll_create));
            }

            return ep;
        }
    }

    internal static class LinuxNative
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
            var r = RecvToBs(fd, bs, flags);
            if (r < 0) {
                ThrowWithErrno("recv");
            }
            return r;
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

    public enum EPOLL_CTL
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
        CMSG_CLOEXEC = 0x40000000,	/* Set close_on_exec for file
					   descriptor received through
					   SCM_RIGHTS */
    }
}

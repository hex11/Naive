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
    public class YASocket : SocketStream, IEpollHandler
    {
        public static bool isX86 = true;

        private const EPOLL_EVENTS PollInEvents = EPOLL_EVENTS.IN | EPOLL_EVENTS.RDHUP | EPOLL_EVENTS.ONESHOT | EPOLL_EVENTS.ET;

        public YASocket(Socket socket) : base(socket)
        {
        }

        private int fd => Fd.ToInt32();
        private int fdR = -1;

        private static bool HasFlag(EPOLL_EVENTS t, EPOLL_EVENTS flag) => (t & flag) != 0;

        public void HandleEvent(Epoller s, int eFd, EPOLL_EVENTS e)
        {
            try {
                Logging.debug(this + ": fd " + fd + "/" + fdR + " event on fd " + eFd + ": " + e);
                if (s == GlobalEpoller) {
                    int r;
                    int errno = 0;
                    var bs = bufRead;
                    bufRead.ResetSelf();
                    lock (raRead) {
                        if (bs.Bytes != null) {
                            if (fdR == -1) {
                                raRead.TrySetException(GetClosedException());
                                return;
                            }
                            r = LinuxNative.ReadToBs(fdR, bs);
                        } else {
                            throw new Exception("should not happen: bs.Bytes == null");
                        }
                        if (r < 0) {
                            errno = LinuxNative.GetErrno();
                            fdR_TryCloseAndRemove();
                            if (HasFlag(e, EPOLL_EVENTS.ERR))
                                State |= MyStreamState.Closed;
                            else
                                State |= MyStreamState.RemoteShutdown;
                        } else {
                            if (r == 0) {
                                State |= MyStreamState.RemoteShutdown;
                                fdR_TryCloseAndRemove();
                            }
                        }
                    }
                    if (r < 0) {
                        var ex = LinuxNative.GetExceptionWithErrno(nameof(LinuxNative.read), errno);
                        raRead.TrySetException(ex);
                    } else {
                        raRead.TrySetResult(r);
                    }
                } else if (s == GlobalEpollerW) {
                    GlobalEpollerW.RemoveFd(fd);
                    int r;
                    var bs = bufWrite;
                    bufWrite.ResetSelf();
                    lock (raWrite) {
                        if (State.HasShutdown) {
                            raWrite.SetException(GetStateException());
                            return;
                        }
                        r = LinuxNative.WriteFromBs(fd, bs);
                    }
                    if (r < 0) {
                        var ex = LinuxNative.GetExceptionWithErrno(nameof(LinuxNative.write));
                        raWrite.SetException(ex);
                    } else {
                        raWrite.SetResult(0);
                    }
                } else {
                    throw new Exception("unexpected source Epoller!");
                }
            } catch (Exception ex) {
                throw new Exception(this + " fd " + fd + " exception with EPOLL_EVENTS: " + e, ex);
            }
        }

        void fdR_TryCloseAndRemove()
        {
            lock (raRead) {
                fdR_TryCloseAndRemove_NoLock();
            }
        }

        void fdR_TryCloseAndRemove_NoLock()
        {
            if (fdR == -1)
                return;
            GlobalEpoller.RemoveFdNotThrows(fdR);
            if (LinuxNative.close(fdR) != 0)
                throw LinuxNative.GetExceptionWithErrno("close");
            fdR = -1;
        }

        public override async Task WriteAsyncImpl(BytesSegment bs)
        {
            await WriteAsyncRImpl(bs);
        }

        private ReusableAwaiter<VoidType> raWrite = new ReusableAwaiter<VoidType>();
        private BytesSegment bufWrite;

        public override AwaitableWrapper WriteAsyncRImpl(BytesSegment bs)
        {
            bs.CheckAsParameter();
            lock (raWrite) {
                if (State.HasShutdown)
                    throw GetStateException();
                raWrite.Reset();
                bufWrite = bs;
                GlobalEpollerW.AddFd(fd, EPOLL_EVENTS.OUT, this);
            }
            return new AwaitableWrapper(raWrite);
        }

        protected override async Task<int> ReadAsyncImpl(BytesSegment bs)
        {
            return await ReadAsyncRImpl(bs);
        }

        private ReusableAwaiter<int> raRead = new ReusableAwaiter<int>();
        private BytesSegment bufRead;

        protected override unsafe int TryReadSync(BytesSegment bs)
        {
            pollfd pfd = new pollfd {
                fd = fd,
                events = POLL_EVENTS.IN | POLL_EVENTS.ERR | POLL_EVENTS.HUP
            };
            lock (raRead) {
                if (State.HasRemoteShutdown)
                    throw GetStateException();
                if (LinuxNative.poll(&pfd, 1, 0) == 1) {
                    bs.CheckAsParameter();
                    return ReadFdSafeThrows_NoLock(bs);
                }
            }
            return 0;
        }

        protected override AwaitableWrapper<int> ReadAsyncRImpl(BytesSegment bs)
        {
            bs.CheckAsParameter();
            lock (raRead) {
                if (State.HasRemoteShutdown)
                    throw GetStateException();
                if (raRead.Exception != null)
                    throw raRead.Exception;
                raRead.Reset();
                bufRead = bs;
                if (fdR == -1) {
                    fdR = LinuxNative.dup(fd);
                    GlobalEpoller.AddFd(fdR, PollInEvents, this);
                } else {
                    GlobalEpoller.ModifyFd(fdR, PollInEvents);
                }
            }
            return new AwaitableWrapper<int>(raRead);
        }

        private const int FIONBIO = 0x5421;

        public override int GetAvailable()
        {
            unsafe {
                var a = 0;
                if (LinuxNative.ioctl(fd, FIONBIO, &a) != 0)
                    return 0;
                return a;
            }
        }

        protected override int SocketReadImpl(BytesSegment bs)
        {
            lock (raRead)
                return ReadFdSafeThrows_NoLock(bs);
        }

        private int ReadFdSafeThrows_NoLock(BytesSegment bs)
        {
            if (State.HasRemoteShutdown)
                throw GetStateException();
            return LinuxNative.ReadToBsThrows(fd, bs);
        }

        public override Task Shutdown(SocketShutdown direction)
        {
            lock (raWrite) {
                State |= MyStreamState.LocalShutdown;
            }
            return base.Shutdown(direction);
        }

        public override Task Close()
        {
            lock (raWrite)
                lock (raRead) {
                    State = MyStreamState.Closed;
                    fdR_TryCloseAndRemove_NoLock();
                    raRead.TrySetException(GetClosedException());
                }
            return base.Close();
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
        private const int MAX_EVENTS = 16;

        private int ep;
        private Dictionary<int, IEpollHandler> mapFdToHandler = new Dictionary<int, IEpollHandler>();
        private System.Threading.ReaderWriterLockSlim mapLock = new System.Threading.ReaderWriterLockSlim();
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
            Logger.debug("AddFd " + fd + " " + events);

            var ev = new epoll_event {
                events = events,
                u32a = fd
            };
            mapLock.EnterWriteLock();
            try {
                unsafe {
                    if (LinuxNative.epoll_ctl(ep, EPOLL_CTL.ADD, fd, ev) != 0) {
                        LinuxNative.ThrowWithErrno(nameof(LinuxNative.epoll_ctl));
                    }
                }
                mapFdToHandler.Add(fd, handler);
                //mapFdToHandler[fd] = handler;
            } finally {
                mapLock.ExitWriteLock();
            }
        }

        public void ModifyFd(int fd, EPOLL_EVENTS events)
        {
            Logger.debug("ModifyFd " + fd + " " + events);

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
            Logger.debug("ModifyFd " + fd + " " + events);

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
            Logger.debug("RemoveFd " + fd);

            mapLock.EnterWriteLock();
            try {
                unsafe {
                    var ev = default(epoll_event);
                    if (LinuxNative.epoll_ctl(ep, EPOLL_CTL.DEL, fd, ev) != 0) {
                        LinuxNative.ThrowWithErrno(nameof(LinuxNative.epoll_ctl));
                    }
                }

                mapFdToHandler.Remove(fd);
            } finally {
                mapLock.ExitWriteLock();
            }
        }

        public int RemoveFdNotThrows(int fd)
        {
            Logger.debug("RemoveFdNotThrows " + fd);

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
            } finally {
                mapLock.ExitWriteLock();
            }
            return 0;
        }

        private unsafe void PollLoop(int ep)
        {
            if (ep < 0)
                throw new Exception("epoll fd does not exist.");
            Logger.debug("PollLoop running");
            if (YASocket.isX86)
                PollLoop_x86(ep);
            else
                PollLoop_nonx86(ep);
        }

        private unsafe void PollLoop_x86(int ep)
        {
            fixed (epoll_event* events = new epoll_event[MAX_EVENTS])
                while (true) {
                    var eventCount = LinuxNative.epoll_wait(ep, events, MAX_EVENTS, -1);
                    if (eventCount < 0) {
                        var errno = LinuxNative.GetErrno();
                        if (errno == 4) // Interrupted system call
                            continue;
                        LinuxNative.ThrowWithErrno(nameof(LinuxNative.epoll_wait), errno);
                    }
                    Logger.debug("eventcount " + eventCount);
                    for (int i = 0; i < eventCount; i++) {
                        var e = events[i];
                        var eventType = e.events;
                        mapLock.EnterReadLock();
                        var fd = e.u32a;
                        bool ok = mapFdToHandler.TryGetValue(fd, out var handler);
                        mapLock.ExitReadLock();
                        if (!ok) {
                            Logger.warning($"EpollHandler not found! event({i}/{eventCount})=[{eventType}] u64=[{e.u64:X}]");
                            continue;
                        }
                        handler.HandleEvent(this, fd, eventType);
                    }
                }
        }

        private unsafe void PollLoop_nonx86(int ep)
        {
            fixed (epoll_event_nonx86* events = new epoll_event_nonx86[MAX_EVENTS])
                while (true) {
                    var eventCount = LinuxNative.epoll_wait(ep, events, MAX_EVENTS, -1);
                    if (eventCount < 0) {
                        var errno = LinuxNative.GetErrno();
                        if (errno == 4) // Interrupted system call
                            continue;
                        LinuxNative.ThrowWithErrno(nameof(LinuxNative.epoll_wait), errno);
                    }
                    Logger.debug("eventcount " + eventCount);
                    for (int i = 0; i < eventCount; i++) {
                        var e = events[i];
                        var eventType = e.events;
                        mapLock.EnterReadLock();
                        var fd = e.u32a;
                        bool ok = mapFdToHandler.TryGetValue(fd, out var handler);
                        mapLock.ExitReadLock();
                        if (!ok) {
                            Logger.warning($"EpollHandler not found! event({i}/{eventCount})=[{eventType}] u64=[{e.u64:X}]");
                            continue;
                        }
                        handler.HandleEvent(this, fd, eventType);
                    }
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
        public static extern int dup([In]int fd);

        [DllImport(LIBC, SetLastError = true)]
        public unsafe static extern int pipe2(int* pipefd, int flags);

        [DllImport(LIBC, SetLastError = true)]
        public static extern int close([In]int fd);

        [DllImport(LIBC, SetLastError = true)]
        public unsafe static extern int ioctl([In]int fd, [In]uint request, void* ptr);

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
                epoll_event_nonx86 evnx86 = new epoll_event_nonx86 { events = ev.events, u64 = ev.u64 };
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

        public static unsafe int ReadToBs(int fd, BytesSegment bs)
        {
            fixed (byte* buf = &bs.Bytes[bs.Offset]) {
                return read(fd, buf, bs.Len);
            }
        }

        public static unsafe int ReadToBsThrows(int fd, BytesSegment bs)
        {
            var r = ReadToBs(fd, bs);
            if (r < 0) {
                ThrowWithErrno("read");
            }
            return r;
        }

        public static unsafe int WriteFromBs(int fd, BytesSegment bs)
        {
            fixed (byte* buf = &bs.Bytes[bs.Offset]) {
                return write(fd, buf, bs.Len);
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
    internal struct epoll_event_nonx86
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
}

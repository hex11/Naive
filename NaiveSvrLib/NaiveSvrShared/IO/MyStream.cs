using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Net;

namespace NaiveSocks
{
    public interface IMyStream
    {
        MyStreamState State { get; }

        Task Close();
        Task Shutdown(SocketShutdown direction);
        Task<int> ReadAsync(BytesSegment bs);
        Task WriteAsync(BytesSegment bs);
        Task FlushAsync();
    }

    public interface IHaveBaseStream
    {
        object BaseStream { get; }
    }

    public interface IMyStreamMultiBuffer : IMyStream
    {
        Task WriteMultipleAsync(BytesView bv);
    }

    public interface IMyStreamMultiBufferR : IMyStreamMultiBuffer
    {
        AwaitableWrapper WriteMultipleAsyncR(BytesView bv);
    }

    public interface IMyStreamSync : IMyStream
    {
        void Write(BytesSegment bs);
        int Read(BytesSegment bs);
    }

    public interface IMyStreamReadFull : IMyStream
    {
        Task ReadFullAsync(BytesSegment bs);
    }

    public interface IMyStreamReadFullR : IMyStream
    {
        AwaitableWrapper ReadFullAsyncR(BytesSegment bs);
    }

    public interface IMyStreamNoBuffer
    {
        AwaitableWrapper<BytesSegment> ReadNBAsyncR(int maxSize);
    }

    public interface IMyStreamBeginEnd : IMyStream
    {
        IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state);
        void EndWrite(IAsyncResult asyncResult);
        IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state);
        int EndRead(IAsyncResult asyncResult);
    }

    public interface IMyStreamGetStream : IMyStream
    {
        Stream GetStream();
    }

    public interface IMyStreamReadR : IMyStream
    {
        AwaitableWrapper<int> ReadAsyncR(BytesSegment bs);
    }

    public interface IMyStreamWriteR : IMyStream
    {
        AwaitableWrapper WriteAsyncR(BytesSegment bs);
    }

    public interface IUdpSocket : IMyStream
    {
        AwaitableWrapper WriteToAsyncR(BytesSegment bs, IPEndPoint ep);
        AwaitableWrapper<ReceiveFromResult> ReadFromAsyncR(BytesSegment bs, IPEndPoint ep);
    }

    public struct ReceiveFromResult
    {
        public IPEndPoint From;
        public int Read;
    }

    public interface IMyStreamDispose : IMyStream
    {
        Task Dispose();
    }

    public struct MyStreamState
    {
        private const int
            OPEN = 0,
            LOCAL_SHUTDOWN = 1 << 0,
            REMOTE_SHUTDOWN = 1 << 1,
            CLOSED = LOCAL_SHUTDOWN | REMOTE_SHUTDOWN,
            DISPOSED = 1 << 2 | CLOSED;

        private MyStreamState(int state)
        {
            this.state = state;
        }

        public MyStreamState(bool localShutdown, bool remoteShutdown)
        {
            state = ((localShutdown) ? LOCAL_SHUTDOWN : OPEN)
                  | ((remoteShutdown) ? REMOTE_SHUTDOWN : OPEN);
        }

        public override string ToString()
        {
            switch (state) {
                case OPEN:
                    return "OPEN";
                case LOCAL_SHUTDOWN:
                    return "LOCAL_SHUTDOWN";
                case REMOTE_SHUTDOWN:
                    return "REMOTE_SHUTDOWN";
                case CLOSED:
                    return "CLOSED";
                case DISPOSED:
                    return "DISPOSED";
                default:
                    return $"!!UNKNOWN({state})!!";
            }
        }

        private readonly int state;

        public int Value => state;

        public static readonly MyStreamState Open = new MyStreamState(OPEN);
        public static readonly MyStreamState LocalShutdown = new MyStreamState(LOCAL_SHUTDOWN);
        public static readonly MyStreamState RemoteShutdown = new MyStreamState(REMOTE_SHUTDOWN);
        public static readonly MyStreamState Closed = new MyStreamState(CLOSED);
        public static readonly MyStreamState Disposed = new MyStreamState(DISPOSED);

        public bool IsOpen => state == OPEN;
        public bool IsClosed => Has(CLOSED);
        public bool HasShutdown => Has(LOCAL_SHUTDOWN);
        public bool HasRemoteShutdown => Has(REMOTE_SHUTDOWN);
        public bool IsDisposed => state == DISPOSED;

        private bool Has(int flag) => (state & flag) == flag;

        public static bool operator ==(MyStreamState s1, MyStreamState s2) => s1.state == s2.state;
        public static bool operator !=(MyStreamState s1, MyStreamState s2) => s1.state != s2.state;
        public static MyStreamState operator &(MyStreamState s1, MyStreamState s2) => new MyStreamState(s1.state & s2.state);
        public static MyStreamState operator |(MyStreamState s1, MyStreamState s2) => new MyStreamState(s1.state | s2.state);

        public override int GetHashCode() => state;

        public override bool Equals(object obj)
        {
            if (!(obj is MyStreamState other)) {
                return false;
            }
            return state == other.state;
        }

        public static explicit operator MsgStreamStatus(MyStreamState v)
        {
            return (MsgStreamStatus)v.state;
        }

        public static explicit operator MyStreamState(MsgStreamStatus v)
        {
            return new MyStreamState((int)v);
        }
    }

    public struct BytesCounterValue
    {
        public long Packets, Bytes;

        public void Add(long bytes)
        {
            Interlocked.Increment(ref Packets);
            Interlocked.Add(ref Bytes, bytes);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            ToString(sb, false);
            return sb.ToString();
        }

        public void ToString(StringBuilder sb, bool largeUnit)
        {
            sb.AppendFormat(Packets.ToString("N0")).Append("(");
            if (largeUnit) {
                if (Bytes == 0)
                    sb.Append("0 KB");
                else if (Bytes < 1024)
                    sb.Append("<1 KB");
                else
                    sb.Append((Bytes / 1024).ToString("N0")).Append(" KB");
            } else {
                sb.AppendFormat(Bytes.ToString("N0"));
            }
            sb.Append(")");
        }

        public static BytesCounterValue operator +(BytesCounterValue v1, BytesCounterValue v2)
        {
            return new BytesCounterValue {
                Packets = v1.Packets + v2.Packets,
                Bytes = v1.Bytes + v2.Bytes
            };
        }
    }

    public struct BytesCountersRW
    {
        public BytesCounter R, W;

        public BytesCounterValue TotalValue => R.Value + W.Value;

        public BytesCountersRW(BytesCountersRW nextRW)
        {
            R = new BytesCounter(nextRW.R);
            W = new BytesCounter(nextRW.W);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            ToString(sb, false);
            return sb.ToString();
        }

        public void ToString(StringBuilder sb, bool simple)
        {
            sb.Append("R=");
            R.Value.ToString(sb, simple);
            sb.Append(" W=");
            W.Value.ToString(sb, simple);
        }
    }

    public class BytesCounter
    {
        public BytesCounterValue Value;

        public BytesCounter Next;

        public BytesCounter()
        {
        }

        public BytesCounter(BytesCounter next)
        {
            this.Next = next;
        }

        public void Add(long bytes)
        {
            Value.Add(bytes);
            if (Next?.AddWithTTL(bytes, 64) == false) {
                Logging.logWithStackTrace("BytesCounter.Add(): Recursion limit exceeded!", Logging.Level.Error);
            }
        }

        private bool AddWithTTL(long bytes, int ttl)
        {
            Value.Add(bytes);
            var node = Next;
            while (node != null) {
                if (--ttl <= 0)
                    return false;
                node.Value.Add(bytes);
                node = node.Next;
            }
            return true;
        }

        public override string ToString()
        {
            return Value.ToString();
        }
    }

    public abstract class MyStream : /*Stream,*/ IMyStream
    {
        public virtual MyStreamState State { get; protected set; }
        public virtual bool Connected
        {
            get {
                return State != MyStreamState.Closed;
            }
            protected set {
                State = value ? MyStreamState.Open : MyStreamState.Closed;
            }
        }

        private static Random rd => NaiveUtils.Random;

        public virtual Task Shutdown(SocketShutdown direction)
        {
            return Close();
        }

        public virtual Task Close()
        {
            return NaiveUtils.CompletedTask;
        }

        public abstract Task<int> ReadAsync(BytesSegment bs);

        public abstract Task WriteAsync(BytesSegment bs);

        public virtual Task FlushAsync() => NaiveUtils.CompletedTask;

        public enum SocketImpl
        {
            SocketStream1,
            SocketStream2,
            SocketStreamFa,
            YASocket
        }

        public static SocketImpl CurrentSocketImpl = SocketImpl.SocketStream1;

        public static void SetSocketImpl(string str)
        {
            CurrentSocketImpl = GetSocketImplFromString(str);
            Logging.info("Current SocketStream implementation: " + CurrentSocketImpl);
        }

        public static SocketImpl GetSocketImplFromString(string str)
        {
            SocketImpl impl;
            if (str == "1") {
                impl = SocketImpl.SocketStream1;
            } else if (str == "2") {
                impl = SocketImpl.SocketStream2;
            } else if (str == "fa") {
                impl = SocketImpl.SocketStreamFa;
            } else if (str == "ya") {
                if (Environment.OSVersion.Platform == PlatformID.Unix) {
                    impl = SocketImpl.YASocket;
                } else {
                    Logging.warning($"SocketStream implementation 'ya' (YASocket) is only available on Linux.");
                    impl = SocketImpl.SocketStream1;
                }
            } else {
                impl = SocketImpl.SocketStream1;
                Logging.warning($"GetSocketImplFromString with wrong argument '{str}'");
            }
            return impl;
        }

        public static SocketStream FromSocket(Socket socket)
        {
            return FromSocket(socket, CurrentSocketImpl);
        }

        public static SocketStream FromSocket(Socket socket, string impl)
            => FromSocket(socket, (impl.IsNullOrEmpty() ? CurrentSocketImpl : GetSocketImplFromString(impl)));

        public static SocketStream FromSocket(Socket socket, SocketImpl impl)
        {
            if (impl == SocketImpl.SocketStream2)
                return new SocketStream2(socket);
            if (impl == SocketImpl.SocketStreamFa)
                return new SocketStreamFa(socket);
            if (impl == SocketImpl.YASocket)
                return new YASocket(socket);
            return new SocketStream1(socket);
        }

        public static IMyStream FromStream(Stream stream)
        {
            if (stream is IMyStream mystream)
                return mystream;
            if (stream is StreamFromMyStream sfms) {
                return sfms.BaseStream;
            }
            return new StreamWrapper(stream);
        }

        public static Stream ToStream(IMyStream myStream)
        {
            if (myStream is Stream stream)
                return stream;
            if (myStream is IMyStreamGetStream gs)
                return gs.GetStream();
            return new StreamFromMyStream(myStream);
        }

        private static readonly BytesCounter globalWriteCouter = new BytesCounter();

        public static BytesCounter GlobalWriteCounter => globalWriteCouter;

        public static BytesCounterValue TotalCopied => globalWriteCouter.Value;
        public static long TotalCopiedPackets => TotalCopied.Packets;
        public static long TotalCopiedBytes => TotalCopied.Bytes;

        public static Task Relay(IMyStream left, IMyStream right, Task whenCanReadFromLeft = null)
        {
            return new TwoWayCopier(left, right) { WhenCanReadFromLeft = whenCanReadFromLeft }
                .SetCounters(globalWriteCouter)
                .Run();
        }

        public static Task CloseWithTimeout(IMyStream stream, int timeout = -2)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (timeout < -2)
                throw new ArgumentOutOfRangeException(nameof(timeout), "should be -2 (default), -1 (infinity), or >= 0.");
            if (timeout == -2)
                timeout = 10 * 1000;
            if (stream is IMyStreamDispose) {
                if (stream.State.IsDisposed)
                    return NaiveUtils.CompletedTask;
            } else if (stream.State.IsClosed) {
                return NaiveUtils.CompletedTask;
            }
            return Task.Run(async () => {
                try {
                    var closeTask = stream is IMyStreamDispose dis ? dis.Dispose() : stream.Close();
                    if (await closeTask.WithTimeout(timeout)) {
                        Logging.warning($"stream closing timed out ({timeout} ms). ({stream})");
                    }
                    await closeTask.CAF();
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Warning, $"stream closing ({stream.SafeToStr()})");
                }
            });
        }

        public Task WriteTo(IMyStream stream) => StreamCopy(this, stream);

        public static Task StreamCopy(IMyStream streamfrom, IMyStream streamto) => StreamCopy(streamfrom, streamto, -1);
        public static Task StreamCopy(IMyStream streamfrom, IMyStream streamto, int bs) => StreamCopy(streamfrom, streamto, bs, false);
        public static Task StreamCopy(IMyStream streamfrom, IMyStream streamto, int bs, bool dontShutdown)
        {
            return new Copier(streamfrom, streamto).Copy(bs, !dontShutdown);
        }

        private static void debug(string str)
        {
            Logging.debug(str);
        }

        public class StreamWrapper : StreamWrapperBase, IMyStreamGetStream
        {
            public StreamWrapper(Stream stream) : base(stream)
            {
            }

            public Stream GetStream() => BaseStream;
        }

        public class StreamWrapperBase : MyStream, IMyStreamSync
        {
            protected StreamWrapperBase()
            {
                Connected = true;
            }

            public StreamWrapperBase(Stream stream) : this()
            {
                BaseStream = stream;
            }

            public virtual Stream BaseStream { get; }

            public override Task Close()
            {
                if (Connected) {
                    Connected = false;
                    BaseStream.Close();
                }
                return AsyncHelper.CompletedTask;
            }

            public override Task<int> ReadAsync(BytesSegment bs)
            {
                return BaseStream.ReadAsync(bs);
            }

            public override Task WriteAsync(BytesSegment bs)
            {
                return BaseStream.WriteAsync(bs);
            }

            public override Task FlushAsync()
            {
                return BaseStream.FlushAsync();
            }

            public override string ToString() => $"{{{BaseStream}}}";

            public virtual void Write(BytesSegment bs) => BaseStream.Write(bs);

            public virtual int Read(BytesSegment bs) => BaseStream.Read(bs);
        }

        public class TwoWayCopier
        {
            public static Logger DefaultLogger = Logging.RootLogger;
            public static Logger DefaultVerboseLogger = null;

            private static bool _defaultUseLoggerAsVerboseLogger;

            public static bool DefaultUseLoggerAsVerboseLogger
            {
                get { return _defaultUseLoggerAsVerboseLogger; }
                set {
                    _defaultUseLoggerAsVerboseLogger = value;
                    Copier.DefaultUseLoggerAsVerboseLogger = value;
                }
            }

            private Logger _verboseLogger;
            private Logger _logger;

            public TwoWayCopier(IMyStream left, IMyStream right)
            {
                Left = left;
                Right = right;
                CopierFromLeft = new Copier(left, right);
                CopierFromRight = new Copier(right, left);
                Logger = DefaultLogger;
            }

            public IMyStream Left { get; }
            public IMyStream Right { get; }

            public Task WhenCanReadFromLeft
            {
                get { return CopierFromLeft.WhenCanRead; }
                set { CopierFromLeft.WhenCanRead = value; }
            }

            public Copier CopierFromLeft { get; }
            public Copier CopierFromRight { get; }

            public Logger Logger
            {
                get { return _logger; }
                set {
                    _logger = value;
                    CopierFromRight.Logger = CopierFromLeft.Logger = value;
                }
            }

            public Logger VerboseLogger
            {
                get { return _verboseLogger ?? (DefaultUseLoggerAsVerboseLogger ? Logger : null); }
                set {
                    _verboseLogger = value;
                    CopierFromRight.VerboseLogger = CopierFromLeft.VerboseLogger = value;
                }
            }

            public TwoWayCopier SetCounters(BytesCounter counter)
            {
                CopierFromLeft.CounterW = counter;
                CopierFromRight.CounterW = counter;
                return this;
            }

            public TwoWayCopier SetCounters(BytesCountersRW left, BytesCountersRW right)
            {
                CopierFromLeft.CounterR = left.R;
                CopierFromRight.CounterW = left.W;
                CopierFromRight.CounterR = right.R;
                CopierFromLeft.CounterW = right.W;
                return this;
            }

            public async Task Run()
            {
                var left = Left;
                var right = Right;
                int halfCloseTimeout = (10 * 1000) + NaiveUtils.Random.Next(-1000, 1000);
                const int forceCloseTimeout = 10 * 1000;
                try {
                    var readFromRight = CopierFromRight.CopyAndShutdown();
                    var readFromLeft = CopierFromLeft.CopyAndShutdown();
                    var tasks = new Task[] { readFromRight, readFromLeft };
                    string stringFromTask(Task t) => t == readFromRight ? $"{right} -> {left}" : $"{left} -> {right}";

                    // waiting for half closing.
                    var compeletedTask = await Task.WhenAny(tasks).CAF();
                    if (compeletedTask.IsFaulted) {
                        var exception = compeletedTask.Exception.InnerException;
                        Logger?.exception(exception, Logging.Level.Warning, $"stream copying exception, force closing. ({stringFromTask(compeletedTask)})");
                        return;
                    }

                    var anotherTask = compeletedTask == readFromRight ? readFromLeft : readFromRight;
                    // waiting for full closing with timeout.
                    if (await anotherTask.WithTimeout(halfCloseTimeout)) {
                        Logger?.warning($"keeping half closed for {halfCloseTimeout} ms, force closing. ({stringFromTask(anotherTask)})");
                    } else {
                        if (anotherTask.IsFaulted) {
                            Logger?.exception(anotherTask.Exception.InnerException, Logging.Level.Warning, $"half closed waiting exception. {stringFromTask(anotherTask)}");
                        }
                    }
                } catch (Exception e) {
                    Logger?.exception(e, Logging.Level.Error, $"Relay task ({left.SafeToStr()} <-> {right.SafeToStr()})");
                } finally {
                    var t1 = MyStream.CloseWithTimeout(left, forceCloseTimeout);
                    var t2 = MyStream.CloseWithTimeout(right, forceCloseTimeout);
                    //await t1; await t2;
                }
            }
        }

        public class Copier
        {
            public static bool DefaultUseLoggerAsVerboseLogger = false;

            public static bool TryReadSync = false;
            public static bool TryWriteSync = false;

            private const int defaultBufferSize = 32 * 1024;

            private Logger _verboseLogger;

            public Copier(IMyStream from, IMyStream to)
            {
                From = from;
                To = to;
            }

            public IMyStream From { get; }
            public IMyStream To { get; }

            public BytesCounter CounterR { get; set; }
            public BytesCounter CounterW { get; set; }

            public Task WhenCanRead;

            public int Progress { get; private set; }

            public event Action<Copier, BytesSegment> OnRead;

            public Logger Logger { get; set; }
            public Logger VerboseLogger
            {
                get { return _verboseLogger ?? (DefaultUseLoggerAsVerboseLogger ? Logger : null); }
                set { _verboseLogger = value; }
            }

            public Task Copy() => Copy(-1, false);
            public Task CopyAndShutdown() => Copy(-1, true);

            public async Task Copy(int bs, bool shutdown)
            {
                if (WhenCanRead != null) await WhenCanRead;

                IMsgStream msgStream = (From as MsgStreamToMyStream)?.MsgStream;
                if (bs == -1) bs = defaultBufferSize;
                Naive.HttpSvr.BytesSegment buf;
                Msg lastMsg = new Msg();

                if (msgStream != null || (From is IMyStreamNoBuffer && !TryReadSync)) {
                    buf = new BytesSegment();
                } else {
                    buf = BufferPool.GlobalGet(bs);
                }
                try {
                    int syncCounter = 0;
                    while (true) {
                        BytesSegment tempBuf = new BytesSegment();
                        if (syncCounter > 64) {
                            syncCounter = 0;
                            await Task.Yield();
                        }

                        // read:
                        int read;
                        if (msgStream != null) {
                            // no buffer preallocated for IMsgStream
                            var msg = await msgStream.RecvMsgR(null).SyncCounter(ref syncCounter);
                            lastMsg = msg;
                            if (msg.IsEOF) {
                                read = 0;
                            } else {
                                read = msg.Data.tlen;
                                if (msg.Data.nextNode == null) {
                                    buf = new BytesSegment(msg.Data);
                                } else {
                                    buf = msg.Data.GetBytes();
                                }
                            }
                        } else if (TryReadSync && From is IMyStreamSync fromSync) {
                            read = fromSync.Read(buf);
                        } else if (From is IMyStreamNoBuffer nb) {
                            tempBuf = await nb.ReadNBAsyncR(bs).SyncCounter(ref syncCounter);
                            buf = tempBuf;
                            read = tempBuf.Len;
                        } else {
                            read = await From.ReadAsyncR(buf).SyncCounter(ref syncCounter);
                        }

                        // handle shutdown:
                        if (read == 0) {
                            VerboseLogger?.debugForce($"SHUTDOWN: {From} -> {To}");
                            if (shutdown && !To.State.HasShutdown)
                                await To.Shutdown(SocketShutdown.Send).CAF();
                            break;
                        }

                        ProcessPayload(buf.Sub(0, read));

                        Progress += read;
                        CounterR?.Add(read);

                        // write:
                        if (TryWriteSync && To is IMyStreamSync toSync)
                            toSync.Write(new BytesSegment(buf.Bytes, buf.Offset, read));
                        else
                            await To.WriteAsyncR(new BytesSegment(buf.Bytes, buf.Offset, read)).SyncCounter(ref syncCounter);
                        CounterW?.Add(read);

                        // recycle buffer if possible:
                        if (lastMsg.Data != null) {
                            lastMsg.TryRecycle();
                            buf.ResetSelf();
                        }
                        if (tempBuf.Bytes != null) {
                            BufferPool.GlobalPut(tempBuf.Bytes);
                            tempBuf.ResetSelf();
                            buf.ResetSelf();
                        }
                    }
                } finally {
                    VerboseLogger?.debugForce($"STOPPED: {From} -> {To}");
                    //if (buf.Bytes != null) {
                    //    BufferPool.GlobalPut(buf.Bytes);
                    //}
                }
            }

            private void ProcessPayload(BytesSegment bs)
            {
                OnRead?.Invoke(this, bs);
            }

            public override string ToString() => $"{{Copier {From} -> {To}}}";
        }
    }

    public static class MyStreamExt
    {
        public static Task WriteMultipleAsync(this IMyStream myStream, BytesView bv)
        {
            if (myStream is IMyStreamMultiBuffer bvs) {
                return bvs.WriteMultipleAsync(bv);
            } else {
                return NaiveUtils.RunAsyncTask(async () => {
                    foreach (var item in bv) {
                        if (item.len > 0)
                            await myStream.WriteAsync(new BytesSegment(item));
                    }
                });
            }
        }

        public static Task WriteAsync(this IMyStream myStream, byte[] buf, int offset, int count)
        {
            return myStream.WriteAsync(new BytesSegment(buf, offset, count));
        }

        public static Task<int> ReadAsync(this IMyStream myStream, byte[] buf, int offset, int count)
        {
            return myStream.ReadAsync(new BytesSegment(buf, offset, count));
        }

        public static void Write(this IMyStream myStream, byte[] buf, int offset, int count)
        {
            if (myStream is IMyStreamSync sync)
                sync.Write(new BytesSegment(buf, offset, count));
            else
                myStream.WriteAsync(buf, offset, count).RunSync();
        }

        public static int Read(this IMyStream myStream, byte[] buf, int offset, int count)
        {
            if (myStream is IMyStreamSync sync)
                return sync.Read(new BytesSegment(buf, offset, count));
            else
                return myStream.ReadAsync(buf, offset, count).RunSync();
        }

        public static Task ReadFullAsync(this IMyStream myStream, BytesSegment bs)
        {
            if (myStream is IMyStreamReadFull imsrf)
                return imsrf.ReadFullAsync(bs);
            else
                return ReadFullImpl(myStream, bs);
        }

        public static AwaitableWrapper ReadFullAsyncR(this IMyStream myStream, BytesSegment bs)
        {
            if (myStream is IMyStreamReadFullR fullR)
                return fullR.ReadFullAsyncR(bs);
            else if (myStream is IMyStreamReadFull full)
                return new AwaitableWrapper(full.ReadFullAsync(bs));
            else
                return new AwaitableWrapper(ReadFullImpl(myStream, bs));
        }

        public static async Task ReadFullImpl(IMyStream myStream, BytesSegment bs)
        {
            int pos = 0;
            if (myStream is IMyStreamReadR r) {
                while (pos < bs.Len) {
                    var read = await r.ReadAsyncR(bs.Sub(pos));
                    if (read == 0)
                        throw new DisconnectedException($"unexpected EOF while ReadFull() (count={bs.Len}, pos={pos})");
                    pos += read;
                }
                return;
            }
            while (pos < bs.Len) {
                var read = await myStream.ReadAsync(bs.Sub(pos)).CAF();
                if (read == 0)
                    throw new DisconnectedException($"unexpected EOF while ReadFull() (count={bs.Len}, pos={pos})");
                pos += read;
            }
        }

        public static AwaitableWrapper<int> ReadAsyncR(this IMyStream myStream, BytesSegment bs)
        {
            if (myStream is IMyStreamReadR myStreamReuse) {
                return myStreamReuse.ReadAsyncR(bs);
            }
            return new AwaitableWrapper<int>(myStream.ReadAsync(bs));
        }

        public static AwaitableWrapper WriteAsyncR(this IMyStream myStream, BytesSegment bs)
        {
            if (myStream is IMyStreamWriteR myStreamReuse) {
                return myStreamReuse.WriteAsyncR(bs);
            }
            return new AwaitableWrapper(myStream.WriteAsync(bs));
        }

        public static Task RelayWith(this IMyStream stream1, IMyStream stream2)
        {
            return MyStream.Relay(stream1, stream2);
        }

        public static Stream ToStream(this IMyStream myStream)
        {
            return MyStream.ToStream(myStream);
        }

        public static IMyStream ToMyStream(this Stream stream)
        {
            return MyStream.FromStream(stream);
        }
    }

    public class ReadFullRStateMachine : ReusableAwaiter<VoidType>
    {
        Action _moveNext;

        IMyStream myStream = null;
        BytesSegment bs;
        int step = -1;
        int pos = 0;
        AwaitableWrapper<int> awaitable;

        private void MoveNext()
        {
            if (step == 0) {

            } else if (step == 1) {
                goto STEP1;
            } else {
                throw new Exception();
            }

            //while (pos < bs.Len) {
            WHILE:
            try {
                if (!(pos < bs.Len))
                    goto EWHILE;
                awaitable = myStream.ReadAsyncR(bs.Sub(pos));
                step = 1;
                if (awaitable.IsCompleted)
                    goto STEP1;
                awaitable.UnsafeOnCompleted(_moveNext);
            } catch (Exception e) {
                ResetState();
                SetException(e);
                return;
            }
            return;
            STEP1:
            try {
                var read = awaitable.GetResult();
                if (read == 0)
                    throw new DisconnectedException($"unexpected EOF while ReadFull() (count={bs.Len}, pos={pos})");
                pos += read;
            } catch (Exception e) {
                ResetState();
                SetException(e);
                return;
            }
            goto WHILE;
            //}
            EWHILE:
            ResetState();
            SetResult(VoidType.Void);
            return;
        }

        private void ResetState()
        {
            step = -1;
            myStream = null;
            bs.ResetSelf();
            pos = 0;
        }

        public ReusableAwaiter<VoidType> Start(IMyStream stream, BytesSegment bytes)
        {
            if (step != -1)
                throw new Exception("state machine is already running");
            if (_moveNext == null) {
                _moveNext = MoveNext;
            }
            Reset();
            step = 0;
            myStream = stream;
            bs = bytes;
            pos = 0;
            MoveNext();
            return this;
        }
    }

    public class StreamFromMyStream : Stream, IHaveBaseStream
    {
        public StreamFromMyStream(IMyStream baseStream)
        {
            BaseStream = baseStream;
        }

        public override string ToString() => $"{{Stream {BaseStream}}}";

        public IMyStream BaseStream { get; }

        object IHaveBaseStream.BaseStream => BaseStream;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            BaseStream.FlushAsync().RunSync();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return BaseStream.FlushAsync();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (BaseStream is IMyStreamSync sync)
                return sync.Read(new BytesSegment(buffer, offset, count));
            return BaseStream.ReadAsync(new BytesSegment(buffer, offset, count)).RunSync();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return BaseStream.ReadAsync(new BytesSegment(buffer, offset, count));
        }

        public override void Close()
        {
            BaseStream.Close();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (BaseStream is IMyStreamSync sync)
                sync.Write(new BytesSegment(buffer, offset, count));
            else
                BaseStream.WriteAsync(new BytesSegment(buffer, offset, count)).RunSync();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return BaseStream.WriteAsync(new BytesSegment(buffer, offset, count));
        }

        class WriteAsyncResult : IAsyncResult
        {
            public AwaitableWrapper task;

            public bool IsCompleted => task.IsCompleted;

            public WaitHandle AsyncWaitHandle => throw new NotImplementedException();

            public object AsyncState { get; set; }

            public bool CompletedSynchronously { get; set; }
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            if (BaseStream is IMyStreamBeginEnd be) {
                return be.BeginWrite(buffer, offset, count, callback, state);
            }
            var task = BaseStream.WriteAsyncR(new BytesSegment(buffer, offset, count));
            var ar = new WriteAsyncResult() { task = task, AsyncState = state, CompletedSynchronously = task.IsCompleted };
            if (ar.CompletedSynchronously) {
                callback?.Invoke(ar);
            } else {
                if (callback != null)
                    task.OnCompleted(() => callback(ar));
            }
            return ar;
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            if (BaseStream is IMyStreamBeginEnd be) {
                be.EndWrite(asyncResult);
            } else {
                var ar = (WriteAsyncResult)asyncResult;
                if (ar.task.IsCompleted == false)
                    throw new Exception("task is not completed.");
                ar.task.GetResult();
            }
        }

        class ReadAsyncResult : IAsyncResult
        {
            public AwaitableWrapper<int> task;

            public bool IsCompleted => task.IsCompleted;

            public WaitHandle AsyncWaitHandle => throw new NotImplementedException();

            public object AsyncState { get; set; }

            public bool CompletedSynchronously { get; set; }
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            if (BaseStream is IMyStreamBeginEnd be) {
                return be.BeginRead(buffer, offset, count, callback, state);
            }
            var task = BaseStream.ReadAsyncR(new BytesSegment(buffer, offset, count));
            var ar = new ReadAsyncResult() { task = task, AsyncState = state, CompletedSynchronously = task.IsCompleted };
            if (ar.CompletedSynchronously) {
                callback?.Invoke(ar);
            } else {
                if (callback != null)
                    task.OnCompleted(() => callback(ar));
            }
            return ar;
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            if (BaseStream is IMyStreamBeginEnd be) {
                return be.EndRead(asyncResult);
            } else {
                var ar = (ReadAsyncResult)asyncResult;
                if (ar.task.IsCompleted == false)
                    throw new Exception("task is not completed.");
                return ar.task.GetResult();
            }
        }
    }

    public class MsgStreamToMyStream : IMyStream, IMyStreamMultiBuffer, IHaveBaseStream
    {
        public MsgStreamToMyStream(IMsgStream msgStream)
        {
            MsgStream = msgStream;
        }

        public IMsgStream MsgStream { get; }

        object IHaveBaseStream.BaseStream => MsgStream;

        public virtual MyStreamState State
        {
            get {
                return (MyStreamState)MsgStream.State;
            }
        }

        public async Task Close()
        {
            await MsgStream.Close(new CloseOpt(CloseType.Close)).CAF();
        }

        public async Task Shutdown(SocketShutdown direction)
        {
            await MsgStream.Close(new CloseOpt(CloseType.Shutdown, direction)).CAF();
        }

        private BytesView latestMsg = null;

        public async Task<int> ReadAsync(BytesSegment bs)
        {
            var pos = 0;
            var curnode = latestMsg;
            if (curnode == null || curnode.tlen == 0) {
                curnode = (await MsgStream.RecvMsgR(null)).Data;
                if (curnode == null)
                    return 0;
            }
            do {
                if (curnode.len > 0) {
                    var size = Math.Min(bs.Len, curnode.len);
                    Buffer.BlockCopy(curnode.bytes, curnode.offset, bs.Bytes, bs.Offset + pos, size);
                    curnode.SubSelf(size);
                    pos += size;
                }
            } while (pos < bs.Len && (curnode = curnode.nextNode) != null);
            if (curnode == null || curnode.tlen == 0) {
                latestMsg = null;
            } else {
                latestMsg = curnode;
            }
            return pos;
        }

        public Task WriteAsync(BytesSegment bs)
        {
            return MsgStream.SendMsg(new Msg(new BytesView(bs.Bytes, bs.Offset, bs.Len)));
        }

        public Task WriteMultipleAsync(BytesView bv)
        {
            return MsgStream.SendMsg(new Msg(bv));
        }

        public Task FlushAsync()
        {
            return NaiveUtils.CompletedTask;
        }

        public override string ToString()
        {
            return $"{{{MsgStream}}}";
        }
    }

    public class LoopbackStream : IMyStream
    {
        public MyStreamState State => new MyStreamState(Another.recvEOF, recvEOF);

        public string Description;

        public override string ToString()
        {
            return Description ?? Another.Description;
        }

        private object _syncRoot = new object();

        public LoopbackStream Another { get; }

        public LoopbackStream()
        {
            Another = new LoopbackStream(this);
        }

        private LoopbackStream(LoopbackStream another)
        {
            Another = another;
        }

        private readonly ReusableAwaiter<VoidType> tcsNewBuffer = new ReusableAwaiter<VoidType>();

        private readonly ReusableAwaiter<VoidType> tcsBufferEmptied = new ReusableAwaiter<VoidType>();

        private BytesSegment buffer;

        private bool recvEOF = false;

        public Task Close()
        {
            this.Shutdown(SocketShutdown.Both);
            Another.Shutdown(SocketShutdown.Both);
            return NaiveUtils.CompletedTask;
        }

        public Task Shutdown(SocketShutdown direction)
        {
            lock (Another._syncRoot) {
                if (Another.recvEOF == false) {
                    Another.recvEOF = true;
                    Another.tcsNewBuffer.SetResult(0);
                }
            }
            return NaiveUtils.CompletedTask;
        }

        public Task FlushAsync()
        {
            return NaiveUtils.CompletedTask;
        }

        public async Task<int> ReadAsync(BytesSegment bs)
        {
            lock (_syncRoot) {
                if (tcsNewBuffer?.IsCompleted == false) {
                    throw new Exception("another recv task is running.");
                }
                if (buffer.Len == 0 & !recvEOF) {
                    tcsNewBuffer.Reset();
                }
            }
            if (tcsNewBuffer != null)
                await tcsNewBuffer;
            lock (_syncRoot) {
                if (recvEOF && buffer.Len == 0) {
                    return 0;
                }
                int read = Math.Min(bs.Len, buffer.Len);
                buffer.CopyTo(bs, 0, read);
                buffer.SubSelf(read);
                if (buffer.Len == 0) {
                    buffer = new BytesSegment();
                    tcsBufferEmptied.SetResult(0);
                }
                return read;
            }
        }

        private Task WriteFromAnother(BytesSegment bv)
        {
            lock (_syncRoot) {
                if (recvEOF)
                    throw new Exception($"{this}: stream closed/shutdown.");
                buffer = bv;
                tcsBufferEmptied.Reset();
                tcsNewBuffer.SetResult(0);
                if (tcsBufferEmptied.IsCompleted)
                    return NaiveUtils.CompletedTask;
                return tcsBufferEmptied.CreateTask();
            }
        }

        public Task WriteAsync(BytesSegment bs)
        {
            return Another.WriteFromAnother(bs);
        }
    }
}

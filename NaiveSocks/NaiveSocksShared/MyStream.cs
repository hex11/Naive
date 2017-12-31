using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Diagnostics;
using System.Threading;

namespace NaiveSocks
{
    public interface IMyStream
    {
        MyStreamState State { get; }

        Task Close();
        Task Shutdown(SocketShutdown direction);
        Task<int> ReadAsync(BytesSegment bv);
        Task WriteAsync(BytesSegment bv);
        Task FlushAsync();
    }

    public interface IMyStreamWithByteViewSupport : IMyStream
    {
        Task WriteAsync(BytesView bv);
    }

    public struct MyStreamState
    {
        private const int
            OPEN = 0,
            LOCAL_SHUTDOWN = 1 << 0,
            REMOTE_SHUTDOWN = 1 << 1,
            CLOSED = LOCAL_SHUTDOWN | REMOTE_SHUTDOWN;

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
            default:
                return $"!!UNKNOWN({state})!!";
            }
        }

        private readonly int state;

        public static readonly MyStreamState Open = new MyStreamState(OPEN);
        public static readonly MyStreamState LocalShutdown = new MyStreamState(LOCAL_SHUTDOWN);
        public static readonly MyStreamState RemoteShutdown = new MyStreamState(REMOTE_SHUTDOWN);
        public static readonly MyStreamState Closed = new MyStreamState(CLOSED);

        public bool IsOpen => state == OPEN;
        public bool IsClosed => state == CLOSED;
        public bool HasShutdown => Has(LOCAL_SHUTDOWN);
        public bool HasRemoteShutdown => Has(REMOTE_SHUTDOWN);

        private bool Has(int flag) => (state & flag) != 0;

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

        static Random rd = new Random();

        public virtual Task Shutdown(SocketShutdown direction)
        {
            return Close();
        }

        public virtual Task Close()
        {
            return NaiveUtils.CompletedTask;
        }

        public abstract Task<int> ReadAsync(BytesSegment bv);

        public abstract Task WriteAsync(BytesSegment bv);

        public virtual Task FlushAsync() => NaiveUtils.CompletedTask;

        public static MyStream FromSocket(Socket socket) => new SocketWrapper(socket);
        public static MyStream FromStream(Stream stream) => new StreamWrapper(stream);

        public static Stream ToStream(IMyStream myStream)
        {
            if (myStream is StreamWrapper sw)
                return sw.BaseStream;
            return new StreamFromMyStream(myStream);
        }

        public Task RelayWith(IMyStream stream)
        {
            return Relay(this, stream);
        }

        public static async Task Relay(IMyStream left, IMyStream right, Task whenCanReadFromLeft = null)
        {
            int halfCloseTimeout = 10 * 1000 + rd.Next(-1000, 1000);
            const int forceCloseTimeout = 10 * 1000;
            try {
                var readFromRight = StreamCopy(right, left);
                if (whenCanReadFromLeft != null) {
                    try {
                        await whenCanReadFromLeft.CAF();
                    } catch (Exception e) {
                        throw new Exception("waiting for reading from left", e);
                    }
                }
                var readFromLeft = StreamCopy(left, right);
                var tasks = new Task[] { readFromRight, readFromLeft };
                string stringFromTask(Task t) => t == readFromRight ? $"{right} -> {left}" : $"{left} -> {right}";

                // waiting for half closing.
                var compeletedTask = await Task.WhenAny(tasks).CAF();
                if (compeletedTask.IsFaulted) {
                    var exception = compeletedTask.Exception.InnerException;
                    Logging.exception(exception, Logging.Level.Warning, $"stream copying exception, force closing. ({stringFromTask(compeletedTask)})");
                } else {
                    var anotherTask = compeletedTask == readFromRight ? readFromLeft : readFromRight;

                    // waiting for full closing with timeout.
                    var timedout = await Task.WhenAny(anotherTask, Task.Delay(halfCloseTimeout)).CAF() != anotherTask;
                    if (timedout) {
                        Logging.warning($"keeping half closed for {halfCloseTimeout} ms, force closing. ({stringFromTask(anotherTask)})");
                    } else {
                        // re-await for possible exception
                        if (anotherTask.IsFaulted) {
                            Logging.exception(anotherTask.Exception.InnerException, Logging.Level.Warning, $"half closed waiting exception. {stringFromTask(anotherTask)}");
                        }
                        //Logging.info($"half closing time: {sw.ElapsedMilliseconds} ms. ({stringFromTask(anotherTask)})");
                    }
                }
            } catch (Exception e) {
                Logging.exception(e, Logging.Level.Error, $"Relay task ({left.SafeToStr()} <-> {right.SafeToStr()})");
            }
            close:
            CloseWithTimeout(left, forceCloseTimeout);
            CloseWithTimeout(right, forceCloseTimeout);
        }

        public static void CloseWithTimeout(IMyStream stream, int timeout = -2)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (stream.State.IsClosed)
                return;
            if (timeout == -2)
                timeout = 10 * 1000;
            NaiveUtils.RunAsyncTask(async () => {
                try {
                    Stopwatch sw = Stopwatch.StartNew();
                    var taskTimeout = Task.Delay(timeout);
                    var closeTask = stream.Close();
                    if (await Task.WhenAny(taskTimeout, closeTask).CAF() == taskTimeout) {
                        Logging.warning($"stream closing timed out ({timeout} ms). ({stream})");
                    } else {
                        //Logging.info($"stream closing took {sw.ElapsedMilliseconds} ms. ({stream})");
                    }
                    await closeTask.CAF();
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Warning, $"stream closing ({stream.SafeToStr()})");
                }
            }).Forget();
        }

        public Task WriteTo(IMyStream stream) => StreamCopy(this, stream);

        private const int defaultBufferSize = 32 * 1024;
        private static WeakObjectPool<byte[]> bufPool = new WeakObjectPool<byte[]>(() => new byte[defaultBufferSize]) { MaxCount = 48 };

        public static int BufferPoolMaxCount
        {
            get {
                return bufPool.MaxCount;
            }
            set {
                bufPool.MaxCount = value;
            }
        }

        public static Task StreamCopy(IMyStream streamfrom, IMyStream streamto) => StreamCopy(streamfrom, streamto, -1);
        public static Task StreamCopy(IMyStream streamfrom, IMyStream streamto, int bs) => StreamCopy(streamfrom, streamto, bs, false);
        public static async Task StreamCopy(IMyStream streamfrom, IMyStream streamto, int bs, bool dontShutdown)
        {
            IMsgStream msgStream = (streamfrom as MsgStreamToMyStream)?.MsgStream;
            if (bs == -1) bs = defaultBufferSize;
            WeakObjectPool<byte[]>.Handle bufhandle = null;
            Naive.HttpSvr.BytesSegment buf;

            if (msgStream == null) {
                if (bs == defaultBufferSize) {
                    bufhandle = bufPool.Get();
                    buf = new BytesSegment(bufhandle.Value);
                } else {
                    buf = new BytesSegment(new byte[bs]);
                }
            } else {
                buf = new BytesSegment();
            }
            try {
                while (true) {
                    int read;
                    if (msgStream == null) {
                        read = await streamfrom.ReadAsync(buf).CAF();
                    } else {
                        // no buffer preallocated for IMsgStream
                        var msg = await msgStream.RecvMsg(null).CAF();
                        if (msg.IsEOF) {
                            read = 0;
                        } else {
                            read = msg.Data.tlen;
                            buf = new BytesSegment(msg.Data);
                        }
                    }
                    //debug($"READ {read}: {streamfrom} -> {streamto}");
                    if (read == 0) {
                        debug($"SHUTDOWN: {streamfrom} -> {streamto}");
                        if (!dontShutdown && !streamto.State.HasShutdown)
                            await streamto.Shutdown(SocketShutdown.Send).CAF();
                        break;
                    }
                    await streamto.WriteAsync(new BytesSegment(buf.Bytes, buf.Offset, read)).CAF();
                    //await streamto.FlushAsync();
                }
            } finally {
                debug($"CLOSE: {streamfrom} -> {streamto}");
                bufhandle?.Dispose();
            }
        }

        static void debug(string str)
        {
            Logging.debug(str);
        }


        public class StreamWrapper : MyStream
        {
            public StreamWrapper(Stream stream)
            {
                BaseStream = stream;
                Connected = true;
            }

            public Stream BaseStream { get; }

            public override async Task Close()
            {
                if (Connected) {
                    Connected = false;
                    BaseStream.Close();
                }
            }

            public override Task<int> ReadAsync(BytesSegment bv)
            {
                return BaseStream.ReadAsync(bv);
            }

            public override Task WriteAsync(BytesSegment bv)
            {
                return BaseStream.WriteAsync(bv);
            }

            public override Task FlushAsync()
            {
                return BaseStream.FlushAsync();
            }

            public override string ToString() => $"{{Stream {BaseStream}}}";
        }

        public class SocketWrapper : MyStream
        {
            public SocketWrapper(Socket socket)
            {
                this.Socket = socket;
                this.EPPair = EPPair.FromSocket(socket);
            }

            public EPPair EPPair { get; }
            public Socket Socket { get; }

            public override string ToString() => $"{{Socket {State} {EPPair.ToString()}}}";

            static ObjectPool<SocketAsyncEventArgs> recvArgPool = new ObjectPool<SocketAsyncEventArgs>(
                createFunc: () => {
                    var arg = new SocketAsyncEventArgs();
                    arg.Completed += RecvCompletedCallback;
                    arg.UserToken = new RecvUserToken();
                    return arg;
                }) { MaxCount = 48, DisposeFunc = x => x.Dispose() };

            class RecvUserToken
            {
                public TaskCompletionSource<int> tcs;
                public SocketWrapper sw;
                public void Reset()
                {
                    tcs = null;
                    sw = null;
                }
            }

            public override Task<int> ReadAsync(BytesSegment bv)
            {
                //return base.ReadAsync(bv);
                var e = recvArgPool.GetValue();
                var userToken = ((RecvUserToken)e.UserToken);
                var tcs = new TaskCompletionSource<int>();
                userToken.tcs = tcs;
                var sw = userToken.sw = this;
                e.SetBuffer(bv.Bytes, bv.Offset, bv.Len);
                if (!Socket.ReceiveAsync(e)) { // if opearation completed synchronously
                    RecvCompleted(e, userToken, this, tcs, true);
                }
                return tcs.Task;
            }

            private static void RecvCompletedCallback(object sender, SocketAsyncEventArgs e)
            {
                var userToken = (RecvUserToken)e.UserToken;
                var sw = userToken.sw;
                var tcs = userToken.tcs;
                RecvCompleted(e, userToken, sw, tcs, false);
            }

            private static void RecvCompleted(SocketAsyncEventArgs e, RecvUserToken userToken, SocketWrapper sw, TaskCompletionSource<int> tcs, bool syncCompleted)
            {
                userToken.Reset();
                e.SetBuffer(null, 0, 0);
                SocketError socketError = e.SocketError;
                int bytesTransferred = e.BytesTransferred;
                recvArgPool.PutValue(e);
                if (socketError == SocketError.Success) {
                    if (bytesTransferred == 0) {
                        Logging.debug($"{sw}: remote shutdown");
                        sw.State |= MyStreamState.RemoteShutdown;
                    }
                    tcs.SetResult(bytesTransferred);
                } else {
                    var exception = new SocketException((int)socketError);
                    if (syncCompleted)
                        throw exception;
                    tcs.SetException(exception);
                }
            }

            static ObjectPool<SocketAsyncEventArgs> sendArgPool = new ObjectPool<SocketAsyncEventArgs>(
                createFunc: () => {
                    var arg = new SocketAsyncEventArgs();
                    arg.Completed += SendCompletedCallback;
                    return arg;
                }) { MaxCount = 16, DisposeFunc = x => x.Dispose() };

            public override Task WriteAsync(BytesSegment bv)
            {
                //return base.ReadAsync(bv);
                var e = sendArgPool.GetValue();
                var tcs = new TaskCompletionSource<int>();
                e.UserToken = tcs;
                e.SetBuffer(bv.Bytes, bv.Offset, bv.Len);
                if (!Socket.SendAsync(e)) { // if opearation completed synchronously
                    SendCompleted(e, tcs, true);
                }
                return tcs.Task;
            }

            private static void SendCompletedCallback(object sender, SocketAsyncEventArgs e)
            {
                var tcs = e.UserToken as TaskCompletionSource<int>;
                SendCompleted(e, tcs, false);
            }

            private static void SendCompleted(SocketAsyncEventArgs e, TaskCompletionSource<int> tcs, bool syncCompleted)
            {
                e.UserToken = null;
                e.SetBuffer(null, 0, 0);
                SocketError socketError = e.SocketError;
                sendArgPool.PutValue(e);
                if (socketError == SocketError.Success) {
                    tcs.SetResult(0);
                } else {
                    var exception = new SocketException((int)socketError);
                    if (syncCompleted)
                        throw exception;
                    tcs.SetException(exception);
                }
            }

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
        }
    }

    class StreamFromMyStream : Stream
    {
        public StreamFromMyStream(IMyStream baseStream)
        {
            BaseStream = baseStream;
        }

        public IMyStream BaseStream { get; }

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
            return BaseStream.ReadAsync(new BytesSegment(buffer, offset, count)).RunSync();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return BaseStream.ReadAsync(new BytesSegment(buffer, offset, count));
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
            BaseStream.WriteAsync(new BytesSegment(buffer, offset, count)).RunSync();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return BaseStream.WriteAsync(new BytesSegment(buffer, offset, count));
        }
    }

    public class MsgStreamToMyStream : IMyStream
    {
        public MsgStreamToMyStream(IMsgStream msgStream)
        {
            MsgStream = msgStream;
        }

        public IMsgStream MsgStream { get; }

        public MyStreamState State
        {
            get {
                switch (MsgStream.State) {
                case MsgStreamStatus.Open:
                    return MyStreamState.Open;
                case MsgStreamStatus.Shutdown:
                    return MyStreamState.LocalShutdown;
                case MsgStreamStatus.RemoteShutdown:
                    return MyStreamState.RemoteShutdown;
                default:
                    return MyStreamState.Closed;
                }
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

        BytesView latestMsg = null;

        public async Task<int> ReadAsync(BytesSegment bv)
        {
            var pos = 0;
            var curnode = latestMsg;
            if (curnode == null || curnode.tlen == 0) {
                curnode = (await MsgStream.RecvMsg(null).CAF()).Data;
                if (curnode == null)
                    return 0;
            }
            do {
                if (curnode.len > 0) {
                    var size = Math.Min(bv.Len, curnode.len);
                    Buffer.BlockCopy(curnode.bytes, curnode.offset, bv.Bytes, bv.Offset + pos, size);
                    curnode.Sub(size);
                    pos += size;
                }
            } while (pos < bv.Len && (curnode = curnode.nextNode) != null);
            if (curnode.tlen > 0) {
                latestMsg = curnode;
            }
            return pos;
        }

        public Task WriteAsync(BytesSegment bv)
        {
            return MsgStream.SendMsg(new Msg(new BytesView(bv.Bytes, bv.Offset, bv.Len)));
        }

        public Task FlushAsync()
        {
            return NaiveUtils.CompletedTask;
        }

        public override string ToString()
        {
            return $"{{{MsgStream} as MyStream}}";
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

        object _syncRoot = new object();

        public LoopbackStream Another { get; }

        public LoopbackStream()
        {
            Another = new LoopbackStream(this);
        }

        private LoopbackStream(LoopbackStream another)
        {
            Another = another;
        }

        TaskCompletionSource<object> tcsNewBuffer;
        void notifyNewBuffer()
        {
            var tmp = tcsNewBuffer;
            tcsNewBuffer = null;
            tmp?.SetResult(null);
        }

        TaskCompletionSource<object> tcsBufferEmptied;
        void notifyBufferEmptied()
        {
            var tmp = tcsBufferEmptied;
            tcsBufferEmptied = null;
            tmp?.SetResult(null);
        }

        MemoryStream memoryStream = new MemoryStream();

        bool recvEOF = false;

        public Task Close()
        {
            this.Shutdown(SocketShutdown.Both);
            Another.Shutdown(SocketShutdown.Both);
            return NaiveUtils.CompletedTask;
        }

        public Task Shutdown(SocketShutdown direction)
        {
            lock (Another._syncRoot) {
                Another.recvEOF = true;
                Another.notifyNewBuffer();
            }
            return NaiveUtils.CompletedTask;
        }

        public Task FlushAsync()
        {
            return NaiveUtils.CompletedTask;
        }

        public async Task<int> ReadAsync(BytesSegment bv)
        {
            Task task = null;
            lock (_syncRoot) {
                if (tcsNewBuffer?.Task.IsCompleted == false) {
                    throw new Exception("another recv task is running.");
                }
                if (memoryStream.Length == 0 & !recvEOF) {
                    tcsNewBuffer = new TaskCompletionSource<object>();
                    task = tcsNewBuffer.Task;
                }
            }
            if (task != null)
                await task;
            lock (_syncRoot) {
                var read = (recvEOF && memoryStream.Length == 0) ? 0 : memoryStream.Read(bv.Bytes, bv.Offset, bv.Len);
                if (memoryStream.Position == memoryStream.Length) {
                    memoryStream.SetLength(0);
                    notifyBufferEmptied();
                }
                return read;
            }
        }

        private Task WriteFromAnother(BytesSegment bv)
        {
            lock (_syncRoot) {
                if (recvEOF)
                    throw new Exception($"{this}: stream closed/shutdown.");
                memoryStream.Write(bv.Bytes, bv.Offset, bv.Len);
                memoryStream.Position = 0;
                tcsBufferEmptied = new TaskCompletionSource<object>();
                notifyNewBuffer();
                return tcsBufferEmptied?.Task ?? NaiveUtils.CompletedTask;
            }
        }

        public Task WriteAsync(BytesSegment bv)
        {
            return Another.WriteFromAnother(bv);
        }
    }
}

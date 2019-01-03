using NaiveSocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    public partial class WebSocket : FilterBase, IDisposable, IMsgStream, IMsgStreamStringSupport
    {
        public WebSocket(IMyStream BaseStream, bool isClient)
        {
            this.BaseStream = BaseStream;
            IsClient = isClient;
        }

        public WebSocket(IMyStream BaseStream, bool isClient, bool isOpen) : this(BaseStream, isClient)
        {
            if (isOpen) {
                ConnectionState = States.Open;
            }
        }

        public override string ToString()
        {
            return $"{{Ws({(IsClient ? "client" : "server")}) on {BaseStream}}}";
        }

        public MsgStreamStatus State { get; private set; }

        public Task SendMsg(Msg msg)
        {
            return SendBytesAsync(msg.Data);
        }

        public async Task<Msg> RecvMsg(BytesView buf)
        {
            var frame = await _readAsyncR(new BytesSegment(buf?.bytes, buf?.offset ?? 0, buf?.len ?? 0));
            return new Msg(frame.bv);
        }

        ReusableAwaiter<Msg> _RecvMsgR_ra = new ReusableAwaiter<Msg>();
        ReusableAwaiter<FrameValue> _RecvMsgR_awaiter;
        Action _RecvMsgR_cont;

        public AwaitableWrapper<Msg> RecvMsgR(BytesView buf)
        {
            if (_RecvMsgR_cont == null) {
                _RecvMsgR_cont = () => {
                    if (!_RecvMsgR_awaiter.TryGetResult(out var r, out var ex)) {
                        _RecvMsgR_ra.SetException(ex);
                    } else {
                        _RecvMsgR_ra.SetResult(new Msg(r.bv));
                    }
                };
            }
            _RecvMsgR_ra.Reset();
            var awaiter = _RecvMsgR_awaiter = _readAsyncR(new BytesSegment(buf?.bytes, buf?.offset ?? 0, buf?.len ?? 0));
            if (awaiter.IsCompleted) {
                _RecvMsgR_cont();
            } else {
                awaiter.OnCompleted(_RecvMsgR_cont);
            }
            return new AwaitableWrapper<Msg>(_RecvMsgR_ra);
        }

        public async Task Close(CloseOpt opt)
        {
            switch (opt.CloseType) {
                case CloseType.Close:
                    State = MsgStreamStatus.Close;
                    Close();
                    break;
                case CloseType.Shutdown:
                    State = MsgStreamStatus.Shutdown;
                    await SendBytesAsync(null, 0, 0).CAF();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        Task IMsgStreamStringSupport.SendString(string str)
        {
            return SendStringAsync(str);
        }

        public async Task<string> RecvString()
        {
            return (await RecvMsg(null)).GetString();
        }

        public AsyncEvent<WebSocketMsg> ReceivedAsync = new AsyncEvent<WebSocketMsg>();
        public event Action<WebSocketMsg> Received;
        public event Action<WebSocket> Connected;
        public event Action<WebSocket> PingReceived;
        public event Action<WebSocket> PongReceived;
        public event Action<WebSocket> Closed;
        public event Action<WebSocket> Activated;
        public States ConnectionState = States.Opening;

        private void OnActivated()
        {
            this.LatestActiveTime = CurrentTimeRough;
            Activated?.Invoke(this);
        }

        public int CreateTime = CurrentTime;
        public int LatestActiveTime = CurrentTime;

        ManageState _manageState = ManageState.Normal;

        enum ManageState
        {
            Normal,
            PingSent,
            TimedoutClosed
        }

        public int ManagedPingTimeout = 15;
        public int ManagedCloseTimeout = 60;

        public bool IsTimeout => _manageState == ManageState.TimedoutClosed;
        void throwIfTimeout()
        {
            if (IsTimeout)
                throw getTimeoutException();
        }

        private DisconnectedException getTimeoutException()
            => new DisconnectedException($"timeout ({ManagedCloseTimeout} seconds)");

        private bool isManaged = false;
        public void AddToManaged()
        {
            if (isManaged)
                return;
            isManaged = true;
            lock (ManagedWebSockets)
                ManagedWebSockets.Add(this);
            Manager.CheckManageTask();
        }

        public void AddToManaged(int pingInterval, int timeoutToClose)
        {
            ManagedPingTimeout = pingInterval;
            ManagedCloseTimeout = timeoutToClose;
            AddToManaged();
        }

        public enum States
        {
            Opening,
            Open,
            Closing,
            Closed
        }

        public IMyStream BaseStream { get; protected set; }
        public bool IsClient { get; }

        public int MaxMessageLength = 1 * 1024 * 1024; // 1 MiB

        private static UTF8Encoding UTF8Encoding => NaiveUtils.UTF8Encoding;

        public Task RecvLoopAsync() => recvLoopAsync();

        protected async Task recvLoopAsync()
        {
            try {
                Connected?.Invoke(this);
                while (ConnectionState == States.Open) {
                    await processFrameAsync(await _readAsyncR()).CAF();
                }
            } catch (DisconnectedException) {

            } finally {
                Close();
            }
        }

        public string ReadStringMsg()
        {
            var frame = _read();
            if (frame.opcode == 1) {
                return UTF8Encoding.GetString(frame.payload, 0, frame.len);
            } else {
                throw new Exception($"excepted opcode 1(string) but opcode {frame.opcode} received");
            }
        }

        public byte[] ReadBinaryMsg()
        {
            Read(out var op, out var buf);
            if (op == 2) {
                return buf;
            } else {
                throw new Exception($"excepted opcode 2(binary) but opcode {op} received");
            }
        }

        public void ReadUntilFin(Action<int, byte[], int> action)
        {
            bool fin;
            do {
                Read(out int opcode, out byte[] payload, out fin, out var len);
                action(opcode, payload, len);
            } while (!fin);
        }

        public void Read(out int opcode, out byte[] payload)
        {
            opcode = 0;
            payload = null;
            bool fin = false;
            do {
                Read(out var opcode2, out var payload2, out fin);
                if (payload == null) {
                    opcode = opcode2;
                    payload = payload2;
                } else {
                    payload = concatBytes(payload, payload2);
                }
            } while (!fin);
        }

        public Task<FrameValue> ReadAsync()
        {
            return ReadAsync(null, 0);
        }

        public async Task<FrameValue> ReadAsync(byte[] buf, int offset)
        {
            if (ConnectionState == States.Closed) {
                throw new DisconnectedException("read on closed connection");
            }
            try {
                return await _readAsyncR(new BytesSegment() { Bytes = buf }.Sub(offset)); // buf can be null
            } catch (Exception) {
                throwIfTimeout();
                Close();
                throw;
            }
        }

        public void Read(out int opcode, out byte[] payload, out bool fin)
        {
            Read(out opcode, out payload, out fin, out var len);
        }

        public void Read(out int opcode, out byte[] payload, out bool fin, out int len)
        {
            var result = _read();
            opcode = result.opcode;
            payload = result.payload;
            fin = result.fin;
            len = result.len;
        }

        public FrameValue _read()
        {
            if (ConnectionState == States.Closed) {
                throw new DisconnectedException("read on closed connection");
            }
            try {
                return _readAsync().RunSync();
            } catch (Exception) {
                throwIfTimeout();
                Close();
                throw;
            }
        }

        public struct FrameValue
        {
            public int opcode;
            public byte[] payload;
            public int offset;
            public int len;
            public bool fin;

            private BytesView _bv;
            public BytesView bv
            {
                get => _bv ?? (_bv = new BytesView(payload, offset, len));
                set => _bv = value;
            }
        }

        public void StartPrereadForControlFrame()
        {
            if (_loopR_prereadStarted)
                throw new InvalidOperationException("Preread task is already started.");
            _readAsyncR();
            _loopR_prereadStarted = true;
        }

        private async Task<FrameValue> _readAsync()
        {
            return await _readAsyncR();
        }

        ReusableAwaiter<FrameValue> _readAsyncR(BytesSegment optionalBuffer = default(BytesSegment))
        {
            if (_loopR_task == null) {
                _loopR_task = _loopR();
            } else if (_loopR_task.IsCompleted) {
                throw new Exception("loopR task is completed!");
            }
            if (_loopR_prereadStarted) {
                _loopR_prereadStarted = false;
                return _loopR_result;
            }
            _loopR_result.Reset();
            _loopR_request.SetResult(optionalBuffer);
            return _loopR_result;
        }

        Task _loopR_task;

        bool _loopR_prereadStarted = false;

        ReusableAwaiter<BytesSegment> _loopR_request = new ReusableAwaiter<BytesSegment>();
        ReusableAwaiter<FrameValue> _loopR_result = new ReusableAwaiter<FrameValue>();

        private readonly byte[] _read_buf = new byte[8];

        private async Task _loopR()
        {
        START:
            var optionalBuffer = await _loopR_request;

            try {
                optionalBuffer.CheckAsParameter_AllowNull();
                BytesView bv = new BytesView();
                var wf = new FrameValue();

            REREAD:

                var stream = BaseStream;
                var buf = _read_buf;

                await _readBaseAsync(2).CAF();
                wf.fin = (buf[0] & 0x80) > 0;
                wf.opcode = (buf[0] & 0x0F);
                var mask = (buf[1] & 0x80) > 0;
                var payloadlen = (int)(buf[1] & 0x7F);
                if (payloadlen == 126) {
                    await _readBaseAsync(2).CAF();
                    payloadlen = (int)buf[0] << 8 | buf[1];
                } else if (payloadlen == 127) {
                    await _readBaseAsync(8).CAF();
                    ulong longlen = 0;
                    int cur = 0;
                    for (int i = 8 - 1; i >= 0; i--)
                        longlen |= (ulong)buf[cur++] << (i * 8);
                    if (longlen > int.MaxValue)
                        throw new NotImplementedException($"payload is large than Int32.MaxValue ({longlen} > {int.MaxValue})");
                    payloadlen = (int)longlen;
                }
                if (payloadlen > MaxMessageLength)
                    throw new Exception($"message length limit exceeded ({payloadlen} > {MaxMessageLength})");
                if (optionalBuffer.Bytes != null && payloadlen > optionalBuffer.Len)
                    throw new Exception($"payload is larger than buffer ({payloadlen} > {optionalBuffer.Len})");
                var maskkey = buf;
                if (mask) {
                    await _readBaseAsync(4).CAF();
                }
                byte[] payload;
                int payloadOffset;
                if (optionalBuffer.Bytes != null) {
                    payload = optionalBuffer.Bytes;
                    payloadOffset = optionalBuffer.Offset;
                } else {
                    payload = BufferPool.GlobalGet(payloadlen);
                    payloadOffset = 0;
                }
                bv.Set(payload, payloadOffset, payloadlen);
                if (payloadlen > 0) {
                    await _readBaseAsync(stream, payload, payloadOffset, payloadlen).CAF();
                    if (mask) {
                        for (int i = 0; i < payloadlen; i++) {
                            payload[payloadOffset + i] ^= maskkey[i % 4];
                        }
                    }
                    if (HaveReadFilter) {
                        var oldlen = bv.len;
                        OnRead(bv);
                        if (optionalBuffer.Bytes != null && bv.bytes != optionalBuffer.Bytes) {
                            if (bv.nextNode != null) {
                                throw new NotImplementedException("bv.nextNode != null");
                            }
                            bv.bytes.CopyTo(optionalBuffer.Bytes, payloadOffset);
                        }
                    }
                }

                wf.payload = bv.bytes;
                wf.offset = bv.offset;
                wf.len = bv.tlen;
                wf.bv = bv;

                if (wf.opcode != 0x8)
                    OnActivated();
                switch (wf.opcode) {
                    case 0x8: // close
                        try {
                            ConnectionState = States.Closing;
                            SendMsg(0x8, null, 0, 0);
                        } catch (Exception) { }
                        break;
                    case 0x9: // ping
                        OnPingReceived();
                        var b = bv.GetBytes();
                        await SendMsgAsync(0xA, b, 0, b.Length).CAF();
                        break;
                    case 0xA: // pong
                        OnPongReceived();
                        break;
                    default:
                        _loopR_request.Reset();
                        _loopR_result.SetResult(wf);
                        goto START;
                }

                goto REREAD;

            } catch (Exception e) {
                _loopR_request.Reset();
                _loopR_result.SetException(e);
                return;
            }
        }

        private WebSocketMsg _unfin;
        private int _unfin_Len;

        private Task processFrameAsync(FrameValue wf) => processFrameAsync(wf, wf.fin, wf.opcode, wf.payload, wf.len);
        private Task processFrameAsync(FrameValue wf, bool fin, int opcode, byte[] buf, int len)
        {
            WebSocketMsg msg;
            if (_unfin == null) {
                msg = new WebSocketMsg();
                msg.server = this;
                msg.opcode = opcode;
            } else {
                msg = _unfin;
                opcode = msg.opcode;
                if (_unfin_Len + len > MaxMessageLength)
                    throw new Exception($"message length limit exceeded ({_unfin_Len + len} > {MaxMessageLength})");
            }
            switch (opcode) {
                case 0x1: // string msg
                    msg.data = msg.data as string + Encoding.UTF8.GetString(buf, 0, len);
                    break;
                case 0x2: // bytes msg
                    if (msg.data == null) {
                        msg.data = wf.bv;
                    } else {
                        (msg.data as BytesView).lastNode.nextNode = wf.bv;
                    }
                    if (fin)
                        msg.data = (msg.data as BytesView).GetBytes();
                    break;
                default:
                    return NaiveUtils.CompletedTask;
            }
            if (fin) {
                _unfin = null;
                _unfin_Len = 0;
                Received?.Invoke(msg);
                return ReceivedAsync.InvokeAsync(msg);
            } else {
                _unfin = msg;
                _unfin_Len += len;
            }
            return NaiveUtils.CompletedTask;
        }

        ReadFullRStateMachine readFullR;


        AwaitableWrapper _readBaseAsync(int count)
        {
            return _readBaseAsync(BaseStream, _read_buf, 0, count);
        }

        private AwaitableWrapper _readBaseAsync(IMyStream stream, byte[] buf, int count)
        {
            return _readBaseAsync(stream, buf, 0, count);
        }

        private AwaitableWrapper _readBaseAsync(IMyStream stream, byte[] buf, int offset, int count)
        {
            var bs = new BytesSegment(buf, offset, count);
            if (BaseStream is IMyStreamReadFullR imsrfr) {
                return imsrfr.ReadFullAsyncR(bs);
            } else {
                if (readFullR == null)
                    readFullR = new ReadFullRStateMachine();
                return new AwaitableWrapper(readFullR.Start(BaseStream, bs));
            }
        }

        private byte[] concatBytes(byte[] a, byte[] b)
        {
            byte[] n = new byte[a.Length + b.Length];
            a.CopyTo(n, 0);
            b.CopyTo(n, a.Length);
            return n;
        }

        public int LastPingDuration { get; private set; } = -1;

        public int LastPingSendTime { get; private set; } = -1;
        public int LastPingReceivedTime { get; private set; } = -1;

        private DateTime _pingstart;

        private void OnPongReceived()
        {
            Logging.debug($"pong received on {this}");
            Interlocked.Increment(ref TotalPongsReceived);
            LastPingDuration = (int)(DateTime.UtcNow - _pingstart).TotalMilliseconds;
            _manageState = ManageState.Normal;
            PongReceived?.Invoke(this);
        }

        private void OnPingReceived()
        {
            Logging.debug($"ping received on {this}");
            Interlocked.Increment(ref TotalPingsReceived);
            LastPingReceivedTime = CurrentTime;
            PingReceived?.Invoke(this);
        }

        public void SendPing()
        {
            LastPingSendTime = CurrentTime;
            _pingstart = DateTime.UtcNow;
            SendMsg(0x9, null, 0, 0);
            Interlocked.Increment(ref TotalPingsSent);
        }

        public void BeginSendPing()
        {
            SendPingAsync();
        }

        public Task LastPingTask { get; private set; }

        public Task SendPingAsync()
        {
            if (LastPingTask?.IsCompleted == false)
                return LastPingTask;
            LastPingSendTime = CurrentTime;
            _pingstart = DateTime.UtcNow;
            var task = SendMsgAsync(0x9, null, 0, 0);
            Interlocked.Increment(ref TotalPingsSent);
            return LastPingTask = task;
        }

        public void SendString(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            byte[] bytes = UTF8Encoding.GetBytes(str);
            SendMsg(0x1, bytes, 0, bytes.Length);
        }

        public async Task SendStringAsync(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            var buf = NaiveUtils.GetUTF8Bytes_AllocFromPool(str);
            await SendMsgAsync(0x1, buf.Bytes, 0, buf.Len);
            BufferPool.GlobalPut(buf.Bytes);
        }

        public void BeginSendString(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            byte[] bytes = UTF8Encoding.GetBytes(str);
            BeginSendMsg(0x1, bytes, 0, bytes.Length);
        }

        public void SendBytes(byte[] bytes) => SendBytes(bytes, 0, bytes.Length);
        public Task SendBytesAsync(byte[] bytes) => SendBytesAsync(bytes, 0, bytes.Length);
        public void SendBytes(byte[] bytes, int begin, int count)
        {
            SendMsg(0x2, bytes, begin, count);
        }

        public Task SendBytesAsync(byte[] bytes, int begin, int count)
        {
            return SendMsgAsync(0x2, bytes, begin, count);
        }

        public Task SendBytesAsync(BytesView bv)
        {
            return SendMsgAsync(0x2, bv);
        }

        private readonly byte[] maskbytes = new byte[] { 12, 34, 45, 78 };
        private static Random rd => NaiveUtils.Random;
        private void genMaskBytes()
        {
            rd.NextBytes(maskbytes);
        }

        public void SendMsg(byte opcode, byte[] buf, int begin, int len)
            => SendMsg(opcode, buf, begin, len, true);

        public void SendMsg(byte opcode, byte[] buf, int begin, int len, bool fin)
        {
            _sendMsg(opcode, buf, begin, len, fin);
        }

        private readonly object _lockLatestSendTask = new object();
        private Task _latestSendTask;

        public Task SendMsgAsync(byte opcode, BytesView bv) => SendMsgAsync(opcode, bv, true);
        public Task SendMsgAsync(byte opcode, BytesView bv, bool fin)
        {
            lock (_lockLatestSendTask) {
                if (_latestSendTask == null || _latestSendTask.IsCompleted) {
                    return _latestSendTask = _sendMsgAsync(opcode, bv, fin);
                } else {
                    return _latestSendTask = _sendMsgAsyncQueued(_latestSendTask, opcode, bv, fin);
                }
            }
        }

        private async Task _sendMsgAsyncQueued(Task taskToWait, byte opcode, BytesView bv, bool fin)
        {
            try {
                await taskToWait;
            } catch (Exception) { }
            await _sendMsgAsync(opcode, bv, fin);
        }

        public Task SendMsgAsync(byte opcode, byte[] buf, int begin, int len) => SendMsgAsync(opcode, new BytesView(buf, begin, len));

        public void BeginSendMsg(byte opcode, byte[] buf, int begin, int len) => SendMsgAsync(opcode, buf, begin, len);

        private const int SendBufSizeMax = 32 * 1024;

        BytesView _sendMsgBvCache;

        private async Task _sendMsgAsync(byte opcode, BytesView buf, bool fin = true)
        {
            if (buf == null)
                throw new ArgumentNullException(nameof(buf));
            if (buf.bytes == null && buf.len > 0)
                throw new ArgumentNullException("buf.bytes");

            BytesView bv = buf;
            if (bv.tlen > 0) {
                OnWrite(bv);
            }
            var tlen = bv.tlen;


            int bufcur = 0;
            byte[] sendMsgBuf;
            var isMasked = IsClient;
            var stream = BaseStream;
            if (!isMasked && stream is IMyStreamMultiBuffer msmb) {
                // if base stream supports writing multiple buffers,
                // we can reduce copying overhead.
                sendMsgBuf = BufferPool.GlobalGet(calcFrameHeaderSize(tlen, isMasked));
                buildFrameHeader(opcode, tlen, fin, sendMsgBuf, ref bufcur, isMasked);
                var sendMsgBv = _sendMsgBvCache ?? new BytesView();
                _sendMsgBvCache = null;
                sendMsgBv.Set(sendMsgBuf, 0, bufcur);
                sendMsgBv.nextNode = bv;
                if (stream is IMyStreamMultiBufferR msmbr)
                    await msmbr.WriteMultipleAsyncR(sendMsgBv);
                else
                    await msmb.WriteMultipleAsync(sendMsgBv);
                sendMsgBv.Reset();
                _sendMsgBvCache = sendMsgBv;
                BufferPool.GlobalPut(sendMsgBuf);
            } else {
                sendMsgBuf = BufferPool.GlobalGet(Math.Min(calcFrameHeaderSize(tlen, isMasked) + tlen, SendBufSizeMax));
                buildFrameHeader(opcode, tlen, fin, sendMsgBuf, ref bufcur, isMasked);
                if (buf != null) {
                    var curSendBufSize = sendMsgBuf.Length;
                    var curbv = bv;
                    int maskIndex = 0;
                    do {
                        int cur = curbv.offset;
                        int end = cur + curbv.len;
                        var bytes = curbv.bytes;
                        if (isMasked) {
                            for (; cur < end; cur++) {
                                sendMsgBuf[bufcur++] = (byte)(bytes[cur] ^ maskbytes[maskIndex++ % 4]);
                                if (bufcur >= curSendBufSize) {
                                    await stream.WriteAsyncR(new BytesSegment(sendMsgBuf, 0, bufcur)).CAF();
                                    bufcur = 0;
                                }
                            }
                        } else {
                            while (cur < end) {
                                var toCopy = Math.Min(curSendBufSize - bufcur, end - cur);
                                Buffer.BlockCopy(bytes, cur, sendMsgBuf, bufcur, toCopy);
                                cur += toCopy;
                                bufcur += toCopy;
                                if (bufcur == curSendBufSize) {
                                    await stream.WriteAsyncR(new BytesSegment(sendMsgBuf, 0, bufcur)).CAF();
                                    bufcur = 0;
                                }
                            }
                        }
                    } while ((curbv = curbv.nextNode) != null);
                }
                if (bufcur > 0)
                    await stream.WriteAsyncR(new BytesSegment(sendMsgBuf, 0, bufcur)).CAF();
                BufferPool.GlobalPut(sendMsgBuf);
            }
        }

        private void _sendMsg(byte opcode, byte[] buf, int begin, int len, bool fin = true)
        {
            SendMsgAsync(opcode, new BytesView(buf, begin, len), fin).RunSync();
        }

        private int calcFrameHeaderSize(int len, bool ismasked)
        {
            int size;
            if (len <= 125) {
                size = 2;
            } else if (len < 65536) {
                size = 4;
            } else {
                size = 10;
            }
            if (ismasked)
                size += 4;
            return size;
        }

        private void buildFrameHeader(byte opcode, int len, bool fin, byte[] buf, ref int bufIndex, bool ismask)
        {
            buf[bufIndex++] = (byte)((fin ? 0x80 : 0x00) ^ (opcode & 0x0f));
            byte mask = (byte)(ismask ? 0x80 : 0x00);
            if (len <= 125) {
                buf[bufIndex++] = (byte)(mask ^ len);
            } else if (len < 65536) {
                buf[bufIndex++] = (byte)(mask ^ 126);
                buf[bufIndex++] = (byte)(len >> 8);
                buf[bufIndex++] = (byte)(len);
            } else {
                buf[bufIndex++] = (byte)(mask ^ 127);
                var llen = (long)len;
                for (int i = 8 - 1; i >= 0; i--) {
                    buf[bufIndex++] = (byte)(llen >> (i * 8));
                }
            }
            if (ismask) {
                genMaskBytes();
                for (var i = 0; i < maskbytes.Length; i++)
                    buf[bufIndex++] = maskbytes[i];
            }
        }

        public static string GenerateSecWebSocketKey() => Guid.NewGuid().ToString("D");

        public static string GetWebsocketAcceptKey(string wskey)
        {
            const string magicString = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            var str = wskey + magicString;
            using (var sha1 = SHA1.Create()) {
                var hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(str));
                return Convert.ToBase64String(hash);
            }
        }

        public void Close()
        {
            if (ConnectionState == States.Closed)
                return;
            ConnectionState = States.Closed;
            if (isManaged)
                lock (ManagedWebSockets)
                    ManagedWebSockets.Remove(this);
            Closed?.Invoke(this);
            try {
                BaseStream?.Close();
            } catch (Exception) { }
        }

        public virtual void Dispose()
        {
            BaseStream?.Close();
        }

        public async Task StartVerify(bool recvFirst)
        {
            var rd = RandomNumberGenerator.Create();
            var sha256 = SHA256.Create();
            if (recvFirst) {
                // 1
                var rep = await ReadAsync().CAF(); // 1 <-
                if (rep.len != 32)
                    throw new Exception("handshake failed: wrong packet1 length");
                var sendbuf = new byte[64];
                var clihash = sha256.ComputeHash(rep.bv.GetBytes());
                clihash.CopyTo(sendbuf, 0);
                byte[] serverrandom = new byte[32];
                rd.GetBytes(serverrandom);
                serverrandom.CopyTo(sendbuf, 32);
                await SendBytesAsync(sendbuf).CAF(); // 2 ->
                rep = await ReadAsync().CAF(); // 3 <-
                if (rep.len != 32)
                    throw new Exception("handshake failed: wrong packet3 length");
                if (sha256.ComputeHash(serverrandom).SequenceEqual(rep.payload.Take(32).ToArray()) == false)
                    throw new Exception("handshake failed: wrong packet3");
            } else {
                byte[] clientrandom = new byte[32];
                rd.GetBytes(clientrandom);
                await SendBytesAsync(clientrandom.Clone() as byte[]).CAF(); // 1 -> : client random (32 bytes)
                var rep = await ReadAsync().CAF(); // 2 <- : sha256 of client random (32 bytes) | server random (32 bytes)
                if (rep.len != 64)
                    throw new Exception("handshake failed: wrong packet2 length");
                var excepted = sha256.ComputeHash(clientrandom);
                if (excepted.SequenceEqual(rep.bv.GetBytes(0, 32)) == false)
                    throw new Exception("handshake failed: wrong packet2");
                var buf2 = rep.bv.GetBytes(32, 32);
                await SendBytesAsync(sha256.ComputeHash(buf2)).CAF(); // 3 -> : sha256 of server random bytes (32 bytes)
            }
        }
    }
}

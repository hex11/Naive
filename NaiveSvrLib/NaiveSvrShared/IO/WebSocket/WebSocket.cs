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
    public class WebSocket : FilterBase, IDisposable, IMsgStream, IMsgStreamStringSupport
    {
        public WebSocket(IMyStream BaseStream, bool isClient)
        {
            this.BaseStream = BaseStream;
            IsClient = isClient;
            PongReceived += WebSocket_PongReceived;
            Activated += _activated;
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

        public Task<Msg> RecvMsg(BytesView buf)
        {
            return CreateReadTask(new BytesSegment(buf?.bytes, buf?.offset ?? 0, buf?.len ?? 0), VoidType.Void, (s, x) => new Msg(x.bv), null);
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

        private static readonly Action<WebSocket> _activated = socket => {
            socket.LatestActiveTime = CurrentTimeRough;
        };

        public AsyncEvent<WebSocketMsg> ReceivedAsync = new AsyncEvent<WebSocketMsg>();
        public event Action<WebSocketMsg> Received;
        public event Action<WebSocket> Connected;
        public event Action<WebSocket> PingReceived;
        public event Action<WebSocket> PongReceived;
        public event Action<WebSocket> Closed;
        public event Action<WebSocket> Activated;
        public States ConnectionState = States.Opening;

        private static int _timeAcc = 1;
        public static int TimeAcc
        {
            get { return _timeAcc; }
            set {
                ConfigManageTask(value, _manageInterval);
            }
        }

        private static int _manageInterval = 3000;
        public static int ManageInterval
        {
            get { return _manageInterval; }
            set {
                ConfigManageTask(_timeAcc, value);
            }
        }

        public static void ConfigManageTask(int timeAcc, int manageInterval)
        {
            if (timeAcc <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeAcc));
            if (manageInterval <= 0)
                throw new ArgumentOutOfRangeException(nameof(manageInterval));
            _timeAcc = timeAcc;
            _manageInterval = manageInterval;

            CheckManageTask();
        }

        private static int _RoughTime = 0;

        private static bool _dontCalcTime = false;

        public static int CurrentTime
        {
            get {
                if (_dontCalcTime) {
                    return _RoughTime;
                } else {
                    var calcTime = CalcCurrentTime();
                    _RoughTime = calcTime;
                    return calcTime;
                }
            }
        }

        public static int CurrentTimeRough => _RoughTime;

        static long _totalMsUntilLastTicks = 0;

        static int _lastTicks = Environment.TickCount;

        static SpinLock _lastTicksLock = new SpinLock(false);

        private static int CalcCurrentTime()
        {
            var curTicks = Environment.TickCount;
            var laTicks = _lastTicks;

            if (curTicks >= laTicks) {
                var delta = curTicks - laTicks;
                return (int)((_totalMsUntilLastTicks + delta) / 1000);
            } else {
                bool lt = false;
                _lastTicksLock.Enter(ref lt);
                var delta = (int.MaxValue - laTicks) + (curTicks - int.MinValue) + 1;
                _lastTicks = curTicks;
                _totalMsUntilLastTicks += delta;
                var ret = (int)(_totalMsUntilLastTicks / 1000);
                _lastTicksLock.Exit(false);
                return ret;
            }
        }

        public int CreateTime = CurrentTime;
        public int LatestActiveTime = CurrentTime;

        static readonly object _manageLock = new object();

        static Thread _manageThread;

        static readonly AutoResetEvent _manageIntervalShrinked = new AutoResetEvent(false);

        static void ManageThread(object obj)
        {
            while (true) {
                var interval = _manageInterval;
                if (_manageIntervalShrinked.WaitOne(interval)) {
                    Thread.Sleep(_manageInterval);
                }

                _RoughTime = CalcCurrentTime();
                _dontCalcTime = true;

                try {
                    CheckManagedWebsocket();
                    RunAdditionalTasks();
                } finally {
                    _dontCalcTime = false;
                }
            }
        }

        private static void CheckManageTask()
        {
            var interval = _manageInterval;
            if (_manageThread == null)
                lock (_manageLock)
                    if (_manageThread == null) {
                        Logging.debug("websocket management thread started.");
                        _manageThread = new Thread(ManageThread);
                        _manageThread.Start();
                    }
        }

        private static void RunAdditionalTasks()
        {
            for (int i = AdditionalManagementTasks.Count - 1; i >= 0; i--) {
                Func<bool> item;
                try {
                    item = AdditionalManagementTasks[i];
                } catch (Exception) {
                    continue; // ignore
                }
                bool remove = true;
                try {
                    remove = item();
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "management additional task " + item);
                    remove = true;
                }
                if (remove)
                    lock (AdditionalManagementTasks)
                        AdditionalManagementTasks.RemoveAt(i);
            }
        }

        private static void CheckManagedWebsocket()
        {
            for (int i = ManagedWebSockets.Count - 1; i >= 0; i--) {
                WebSocket item;
                try {
                    item = ManagedWebSockets[i];
                } catch (Exception) {
                    continue; // ignore
                }
                try {
                    var delta = CurrentTime - item.LatestActiveTime;
                    var closeTimeout = item.ManagedCloseTimeout;
                    var pingTimeout = item.ManagedPingTimeout;
                    if (closeTimeout <= 0)
                        continue;
                    if (pingTimeout <= 0)
                        pingTimeout = closeTimeout;
                    if (delta > closeTimeout
                        && (item._manageState == ManageState.PingSent || item.ConnectionState != States.Open)) {
                        Logging.warning($"{item} timed out, closing.");
                        item._manageState = ManageState.TimedoutClosed;
                        item.Close();
                    } else if (pingTimeout > 0 && delta > pingTimeout && item.ConnectionState == States.Open) {
                        if (item._manageState == ManageState.Normal) {
                            Logging.debug($"{item} pinging.");
                            item._manageState = ManageState.PingSent;
                            item.BeginSendPing();
                        } else {
                            Logging.debug($"{item} still pinging.");
                        }
                    } else {
                        //item._manageState = ManageState.Normal;
                    }
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "WebSocket manage task exception, ignored.");
                }
            }
        }

        ManageState _manageState = ManageState.Normal;

        enum ManageState
        {
            Normal,
            PingSent,
            TimedoutClosed
        }

        public static List<WebSocket> ManagedWebSockets = new List<WebSocket>();

        static List<Func<bool>> AdditionalManagementTasks = new List<Func<bool>>();

        public static void AddManagementTask(Func<bool> func)
        {
            lock (AdditionalManagementTasks)
                AdditionalManagementTasks.Add(func);
            CheckManageTask();
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
            CheckManageTask();
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

        public void RecvLoop() => recvLoop();
        public Task RecvLoopAsync() => recvLoopAsync();

        //private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        protected void recvLoop()
        {
            try {
                _recvLoop();
            } catch (DisconnectedException) {

            } finally {
                Close();
            }
        }

        protected async Task recvLoopAsync()
        {
            try {
                Connected?.Invoke(this);
                while (ConnectionState == States.Open) {
                    await processFrameAsync(await ReadAsync().CAF()).CAF();
                }
            } catch (DisconnectedException) {

            } finally {
                Close();
            }
        }

        private void _recvLoop()
        {
            Connected?.Invoke(this);
            while (ConnectionState == States.Open) {
                var frame = _read();
                processFrame(frame);
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

        public Task<FrameValue> ReadAsync(byte[] buf, int offset)
        {
            if (ConnectionState == States.Closed) {
                throw new DisconnectedException("read on closed connection");
            }
            return CreateReadTask(new BytesSegment() { Bytes = buf }.Sub(offset), VoidType.Void, returnAsIs, (s, e) => {
                throwIfTimeout();
                Close();
                throw e;
            });
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
                return _readAsync().GetAwaiter().GetResult();
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

        private readonly byte[] _read_buf = new byte[8];

        private Task<FrameValue> _readAsync()
        {
            return _readAsync(null, 0);
        }

        Task<FrameValue> _prereadTask;

        public void StartPrereadForControlFrame()
        {
            if (_prereadTask != null)
                throw new InvalidOperationException("_prereadTask != null");
            _prereadTask = __readAsync(new BytesSegment(), VoidType.Void, returnAsIs, null, true);
        }

        private Task<FrameValue> _readAsync(byte[] optionalBuffer, int offset)
        {
            var tmp = _prereadTask;
            if (tmp != null) {
                _prereadTask = null;
                return tmp;
            }
            return CreateReadTask(new BytesSegment() { Bytes = optionalBuffer }.Sub(offset), VoidType.Void, returnAsIs, null);
        }

        private Task<TRet> CreateReadTask<TState, TRet>(BytesSegment optionalBuffer, TState state, Func<TState, FrameValue, TRet> retWrapper, Func<TState, Exception, TRet> errWrapper)
        {
            return __readAsync(optionalBuffer, state, retWrapper, errWrapper, false);
        }

        static readonly Func<VoidType, FrameValue, FrameValue> returnAsIs = (s, x) => x;

        private async Task<TRet> __readAsync<TState, TRet>(BytesSegment optionalBuffer, TState state,
                                        Func<TState, FrameValue, TRet> retWrapper, Func<TState, Exception, TRet> errWrapper, bool isPrereadTask)
        {
            try {
                optionalBuffer.CheckAsParameter_AllowNull();
                var wf = new FrameValue();
                BytesView bv = new BytesView();

                if (!isPrereadTask && _prereadTask != null) {
                    var tmp = _prereadTask;
                    _prereadTask = null;
                    wf = await tmp;
                    goto HANDLE_MSG;
                }

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
                    await WebSocket._readBaseAsync(stream, payload, payloadOffset, payloadlen).CAF();
                    if (mask) {
                        for (int i = 0; i < payloadlen; i++) {
                            payload[payloadOffset + i] ^= maskkey[i % 4];
                        }
                    }
                    if (HaveReadFilter != null) {
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

                HANDLE_MSG:

                if (wf.opcode != 0x8)
                    Activated?.Invoke(this);
                switch (wf.opcode) {
                case 0x8: // close
                    try {
                        ConnectionState = States.Closing;
                        SendMsg(0x8, null, 0, 0);
                    } catch (Exception) { }
                    break;
                case 0x9: // ping
                    Logging.debug($"ping received on {this}");
                    PingReceived?.Invoke(this);
                    var b = bv.GetBytes();
                    await SendMsgAsync(0xA, b, 0, b.Length).CAF();
                    break;
                case 0xA: // pong
                    Logging.debug($"pong received on {this}");
                    PongReceived?.Invoke(this);
                    break;
                default:
                    return retWrapper(state, wf);
                }

                goto REREAD;

            } catch (Exception e) when (errWrapper != null) {
                return errWrapper(state, e);
            }
        }

        private WebSocketMsg _unfin;
        private void processFrame(FrameValue wf) => processFrame(wf, wf.fin, wf.opcode, wf.payload, wf.len);
        private void processFrame(FrameValue wf, bool fin, int opcode, byte[] buf, int len)
        {
            WebSocketMsg msg;
            if (_unfin == null) {
                msg = new WebSocketMsg();
                msg.server = this;
                msg.opcode = opcode;
            } else {
                msg = _unfin;
                opcode = msg.opcode;
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
                return;
            }
            if (fin) {
                _unfin = null;
                Received?.Invoke(msg);
                ReceivedAsync.InvokeAsync(msg).RunSync();
            } else {
                _unfin = msg;
            }
        }

        private Task processFrameAsync(FrameValue wf) => processFrameAsync(wf, wf.fin, wf.opcode, wf.payload, wf.len);
        private async Task processFrameAsync(FrameValue wf, bool fin, int opcode, byte[] buf, int len)
        {
            WebSocketMsg msg;
            if (_unfin == null) {
                msg = new WebSocketMsg();
                msg.server = this;
                msg.opcode = opcode;
            } else {
                msg = _unfin;
                opcode = msg.opcode;
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
                return;
            }
            if (fin) {
                _unfin = null;
                Received?.Invoke(msg);
                await ReceivedAsync.InvokeAsync(msg).CAF();
            } else {
                _unfin = msg;
            }
        }

        Task _readBaseAsync(int count) => _readBaseAsync(BaseStream, _read_buf, count);

        private static Task _readBaseAsync(IMyStream stream, byte[] buf, int count)
        {
            return stream.ReadFullAsync(new BytesSegment(buf, 0, count));
        }

        private static Task _readBaseAsync(IMyStream stream, byte[] buf, int offset, int count)
        {
            return stream.ReadFullAsync(new BytesSegment(buf, offset, count));
        }

        private byte[] concatBytes(byte[] a, byte[] b)
        {
            byte[] n = new byte[a.Length + b.Length];
            a.CopyTo(n, 0);
            b.CopyTo(n, a.Length);
            return n;
        }

        public int LastPingTime { get; private set; } = -1;

        private DateTime _pingstart;

        private void WebSocket_PongReceived(WebSocket ws)
        {
            LastPingTime = (int)(DateTime.UtcNow - _pingstart).TotalMilliseconds;
            _manageState = ManageState.Normal;
        }

        public void SendPing()
        {
            _pingstart = DateTime.UtcNow;
            SendMsg(0x9, null, 0, 0);
        }

        public void BeginSendPing()
        {
            SendPingAsync();
        }

        public Task LastPingTask { get; private set; }

        public int LastPingStartWsTime { get; private set; }

        public Task SendPingAsync()
        {
            if (LastPingTask?.IsCompleted == false)
                return LastPingTask;
            _pingstart = DateTime.UtcNow;
            LastPingStartWsTime = CurrentTime;
            return LastPingTask = SendMsgAsync(0x9, null, 0, 0);
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

        public Task SendMsgAsync(byte opcode, BytesView bv)
        {
            lock (_lockLatestSendTask) {
                if (_latestSendTask == null || _latestSendTask.IsCompleted) {
                    return _latestSendTask = _sendMsgAsync(opcode, bv);
                } else {
                    return _latestSendTask = _sendMsgAsyncQueued(_latestSendTask, opcode, bv);
                }
            }
        }

        private async Task _sendMsgAsyncQueued(Task taskToWait, byte opcode, BytesView bv)
        {
            try {
                await taskToWait;
            } catch (Exception) { }
            await _sendMsgAsync(opcode, bv);
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
            if (!isMasked && BaseStream is IMyStreamMultiBuffer msmb) {
                // if base stream supports writing multiple buffers,
                // we can reduce copying overhead.
                sendMsgBuf = BufferPool.GlobalGet(calcFrameHeaderSize(tlen, isMasked));
                buildFrameHeader(opcode, tlen, fin, sendMsgBuf, ref bufcur, isMasked);
                var sendMsgBv = _sendMsgBvCache ?? new BytesView();
                _sendMsgBvCache = null;
                sendMsgBv.Set(sendMsgBuf, 0, bufcur);
                sendMsgBv.nextNode = bv;
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
                                    await stream.WriteAsync(sendMsgBuf, 0, bufcur).CAF();
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
                                    await stream.WriteAsync(sendMsgBuf, 0, bufcur).CAF();
                                    bufcur = 0;
                                }
                            }
                        }
                    } while ((curbv = curbv.nextNode) != null);
                }
                if (bufcur > 0)
                    await stream.WriteAsync(sendMsgBuf, 0, bufcur).CAF();
                BufferPool.GlobalPut(sendMsgBuf);
            }
        }

        private void _sendMsg(byte opcode, byte[] buf, int begin, int len, bool fin = true)
        {
            if (buf == null && len > 0)
                throw new ArgumentNullException(nameof(buf));

            BytesView bv = new BytesView(buf, begin, len);
            if (HaveWriteFilter && len > 0) {
                OnWrite(bv);
                buf = bv.bytes;
                begin = bv.offset;
                len = bv.tlen;
            }
            {
                int bufcur = 0;
                var isMasked = IsClient;
                var sendMsgBuf = BufferPool.GlobalGet(Math.Min(calcFrameHeaderSize(len, isMasked) + len, SendBufSizeMax));
                var curSendBufSize = sendMsgBuf.Length;
                buildFrameHeader(opcode, bv.tlen, fin, sendMsgBuf, ref bufcur, isMasked);
                var stream = BaseStream;
                if (buf != null) {
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
                                    stream.Write(sendMsgBuf, 0, bufcur);
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
                                    stream.Write(sendMsgBuf, 0, bufcur);
                                    bufcur = 0;
                                }
                            }
                        }
                    } while ((curbv = curbv.nextNode) != null);
                }
                if (bufcur > 0)
                    stream.Write(sendMsgBuf, 0, bufcur);
                BufferPool.GlobalPut(sendMsgBuf);
            }
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    public class WebSocket : Filterable, IDisposable, IMsgStream, IMsgStreamStringSupport
    {
        static WebSocket()
        {
            StartTimeTask();
        }

        public WebSocket(Stream BaseStream, bool isClient)
        {
            this.BaseStream = BaseStream;
            IsClient = isClient;
            PongReceived += WebSocket_PongReceived;
            Activated += _activated;
        }

        public override string ToString()
        {
            return $"{{WebSocket({(IsClient ? "client" : "server")}) on {BaseStream}}}";
        }

        public MsgStreamStatus State { get; private set; }

        public Task SendMsg(Msg msg)
        {
            return SendBytesAsync(msg.Data);
        }

        public async Task<Msg> RecvMsg(BytesView buf)
        {
            if (buf != null) {
                var r = await ReadAsync(buf.bytes, buf.offset).CAF();
                return new Msg(r.bv);
            } else {
                var r = await ReadAsync().CAF();
                return new Msg(r.bv);
            }
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
            socket.LatestActiveTime = CurrentTime;
        };

        public AsyncEvent<WebSocketMsg> ReceivedAsync = new AsyncEvent<WebSocketMsg>();
        public event Action<WebSocketMsg> Received;
        public event Action<WebSocket> Connected;
        public event Action<WebSocket> PingReceived;
        public event Action<WebSocket> PongReceived;
        public event Action<WebSocket> Closed;
        public event Action<WebSocket> Activated;
        public States ConnectionState = States.Opening;

        static int _timeTask_id = 0;
        private static void StartTimeTask()
        {
            var id = Interlocked.Increment(ref _timeTask_id);
            NaiveUtils.RunAsyncTask(async () => {
                while (true) {
                    await Task.Delay(_timeAcc * 1000).CAF();
                    if (id != _timeTask_id)
                        return;
                    CurrentTime += _timeAcc;
                }
            });
        }

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
                throw new ArgumentOutOfRangeException("TimeAcc");
            if (manageInterval <= 0)
                throw new ArgumentOutOfRangeException("ManageInterval");
            _timeAcc = timeAcc;
            _manageInterval = manageInterval;

            if (timeAcc * 1000 == manageInterval) {
                _timeTask_id++;
                incrTimeByManageTask = true;
            } else {
                if (incrTimeByManageTask) {
                    incrTimeByManageTask = false;
                    StartTimeTask();
                }
            }
        }

        static bool incrTimeByManageTask = false;

        static int _manageTaskRunning = 0;

        public static int CurrentTime { get; private set; } = 0;
        public int CreateTime = CurrentTime;
        public int LatestActiveTime = CurrentTime;

        private static void CheckManageTask()
        {
            if (Interlocked.CompareExchange(ref _manageTaskRunning, 1, 0) != 0)
                return;
            NaiveUtils.RunAsyncTask(async () => {
                //List<WebSocket> tmpList = new List<WebSocket>();
                Logging.debug("websocket management task started.");
                do {
                    await Task.Delay(_manageInterval).CAF();
                    if (incrTimeByManageTask)
                        CurrentTime += _timeAcc;
                    //tmpList.Clear();
                    if (ManagedWebSockets.Count == 0)
                        continue;
                    //lock (ManagedWebSockets) {
                    //    tmpList.AddRange(ManagedWebSockets);
                    //}
                    //foreach (var item in tmpList) 
                    for (int i = ManagedWebSockets.Count - 1; i >= 0; i--) {
                        WebSocket item;
                        try {
                            item = ManagedWebSockets[i];
                        } catch (Exception) {
                            continue; // ignore
                        }
                        try {
                            var delta = CurrentTime - item.LatestActiveTime;
                            if (item.ManagedCloseTimeout > 0 && delta > item.ManagedCloseTimeout) {
                                item.IsTimeout = true;
                                item.Close();
                            } else if (item.ManagedPingTimeout > 0 && delta > item.ManagedPingTimeout && item.ConnectionState == States.Open) {
                                item.BeginSendPing();
                            }
                        } catch (Exception e) {
                            Logging.exception(e, Logging.Level.Error, "WebSocket manage task exception, ignored.");
                        }
                    }
                }
                while (!(ManagedWebSockets.Count == 0 && Interlocked.CompareExchange(ref _manageTaskRunning, 0, 1) == 1));
                Logging.debug("websocket management task stopped.");
            });
        }

        public static List<WebSocket> ManagedWebSockets = new List<WebSocket>();

        public int ManagedPingTimeout = 15;
        public int ManagedCloseTimeout = 60;

        public bool IsTimeout { get; private set; }
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

        public enum States
        {
            Opening,
            Open,
            Closing,
            Closed
        }

        public Stream BaseStream { get; protected set; }
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

        public async Task<WebsocketFrame> ReadAsync()
        {
            if (ConnectionState == States.Closed) {
                throw new DisconnectedException("read on closed connection");
            }
            try {
                return await _readAsync().CAF();
            } catch (Exception) {
                throwIfTimeout();
                Close();
                throw;
            }
        }

        public async Task<WebsocketFrame> ReadAsync(byte[] buf, int offset)
        {
            if (ConnectionState == States.Closed) {
                throw new DisconnectedException("read on closed connection");
            }
            try {
                return await _readAsync(buf, offset).CAF();
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

        public WebsocketFrame _read()
        {
            if (ConnectionState == States.Closed) {
                throw new DisconnectedException("read on closed connection");
            }
            try {
                return _readAsync().GetAwaiter().GetResult();
            } catch (IOException) when (IsTimeout) {
                throw getTimeoutException();
            } catch (Exception) {
                Close();
                throw;
            }
        }

        public class WebsocketFrame
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

        private Task<WebsocketFrame> _readAsync()
        {
            return _readAsync(null, 0);
        }

        private async Task<WebsocketFrame> _readAsync(byte[] optionalBuffer, int offset)
        {
            WebsocketFrame wf = new WebsocketFrame();
            BytesView bv = new BytesView();
            while (true) {
                var stream = BaseStream;
                var buf = _read_buf;

                Task readAsync(int count) => _readAsync(stream, buf, count);
                await readAsync(2).CAF();
                wf.fin = (buf[0] & 0x80) > 0;
                wf.opcode = (buf[0] & 0x0F);
                var mask = (buf[1] & 0x80) > 0;
                var payloadlen = (int)(buf[1] & 0x7F);
                if (payloadlen == 126) {
                    await readAsync(2).CAF();
                    payloadlen = (int)buf[0] << 8 | buf[1];
                } else if (payloadlen == 127) {
                    await readAsync(8).CAF();
                    ulong longlen = 0;
                    for (int i = 8 - 1; i >= 0; i--)
                        longlen |= (ulong)buf[i] << (i * 8);
                    if (longlen > int.MaxValue)
                        throw new NotImplementedException($"payload is larget than Int32.MaxValue ({longlen} > {int.MaxValue})");
                    payloadlen = (int)longlen;
                }
                if (payloadlen > MaxMessageLength)
                    throw new Exception($"message length limit exceeded ({payloadlen} > {MaxMessageLength})");
                if (optionalBuffer != null && payloadlen > optionalBuffer.Length - offset)
                    throw new Exception($"payload is larger than buffer ({payloadlen} > {optionalBuffer.Length - offset})");
                var maskkey = buf;
                if (mask) {
                    await readAsync(4).CAF();
                }
                var payload = optionalBuffer ?? new byte[payloadlen];
                bv.Set(payload, offset, payloadlen);
                if (payloadlen > 0) {
                    await _readAsync(stream, payload, offset, payloadlen).CAF();
                    if (mask) {
                        for (int i = 0; i < payloadlen; i++) {
                            payload[offset + i] ^= maskkey[i % 4];
                        }
                    }
                    if (ReadFilter != null) {
                        var oldlen = bv.len;
                        ReadFilter(bv);
                        if (optionalBuffer != null && bv.bytes != optionalBuffer) {
                            bv.bytes.CopyTo(optionalBuffer, offset);
                        }
                    }
                }
                wf.payload = bv.bytes;
                wf.offset = bv.offset;
                wf.len = bv.tlen;
                wf.bv = bv;
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
                    PingReceived?.Invoke(this);
                    var b = bv.GetBytes();
                    await SendMsgAsync(0xA, b, 0, b.Length).CAF();
                    break;
                case 0xA: // pong
                    PongReceived?.Invoke(this);
                    break;
                default:
                    return wf;
                }
            }
        }

        private WebSocketMsg _unfin;
        private void processFrame(WebsocketFrame wf) => processFrame(wf, wf.fin, wf.opcode, wf.payload, wf.len);
        private void processFrame(WebsocketFrame wf, bool fin, int opcode, byte[] buf, int len)
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

        private Task processFrameAsync(WebsocketFrame wf) => processFrameAsync(wf, wf.fin, wf.opcode, wf.payload, wf.len);
        private async Task processFrameAsync(WebsocketFrame wf, bool fin, int opcode, byte[] buf, int len)
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

        private static async Task _readAsync(Stream stream, byte[] buf, int count)
        {
            int offset = 0;
            while (count > 0) {
                int read;
                try {
                    count -= read = await stream.ReadAsync(buf, offset, count);
                    offset += read;
                } catch (IOException e) when (e.InnerException is SocketException se) {
                    throw new DisconnectedException($"SocketErrorCode: {se.SocketErrorCode}");
                }
                if (read == 0)
                    throw new DisconnectedException("unexpected EOF");
            }
        }

        private static async Task _readAsync(Stream stream, byte[] buf, int offset, int count)
        {
            while (count > 0) {
                int read;
                try {
                    count -= read = await stream.ReadAsync(buf, offset, count);
                    offset += read;
                } catch (IOException e) when (e.InnerException is SocketException se) {
                    throw new DisconnectedException($"SocketErrorCode: {se.SocketErrorCode}");
                }
                if (read == 0)
                    throw new DisconnectedException("unexpected EOF");
            }
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

        public Task SendStringAsync(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            byte[] bytes = UTF8Encoding.GetBytes(str);
            return SendMsgAsync(0x1, bytes, 0, bytes.Length);
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
        private static readonly Random rd = new Random();
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

        private readonly object _syncRootLastSendTask = new object();
        private Task _latestSendTask;

        public Task SendMsgAsync(byte opcode, BytesView bv)
        {
            lock (_syncRootLastSendTask) {
                if (_latestSendTask == null || _latestSendTask.IsCompleted) {
                    return _latestSendTask = _sendMsgAsync(opcode, bv);
                } else {
                    return _latestSendTask = _sendMsgAsync_await(_latestSendTask, opcode, bv);
                }
            }
        }

        private async Task _sendMsgAsync_await(Task taskToWait, byte opcode, BytesView bv)
        {
            try {
                await taskToWait;
            } catch (Exception) { }
            await _sendMsgAsync(opcode, bv);
        }

        public Task SendMsgAsync(byte opcode, byte[] buf, int begin, int len) => SendMsgAsync(opcode, new BytesView(buf, begin, len));

        public void BeginSendMsg(byte opcode, byte[] buf, int begin, int len) => SendMsgAsync(opcode, buf, begin, len);

        private static readonly WeakObjectPool<byte[]> _sendMsgBufferPool = new WeakObjectPool<byte[]>(() => new byte[SendBufSizeMax]);
        private const int SendBufSizeMin = 990;
        private const int SendBufSizeMax = 1440;


        private void _sendMsg(byte opcode, byte[] buf, int begin, int len, bool fin = true)
        {
            if (buf == null && len > 0)
                throw new ArgumentNullException(nameof(buf));

            BytesView bv = new BytesView(buf, begin, len);
            if (WriteFilter != null && len > 0) {
                WriteFilter(bv);
                buf = bv.bytes;
                begin = bv.offset;
                len = bv.tlen;
            }
            var curSendBufSize = NaiveUtils.Random.Next(SendBufSizeMin, SendBufSizeMax);
            using (var handle = _sendMsgBufferPool.Get()) {
                int bufcur = 0;
                var sendMsgBuf = handle.Value;
                var isMasked = IsClient;
                buildHeader(opcode, bv.tlen, fin, sendMsgBuf, ref bufcur, isMasked);
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
                                    curSendBufSize = NaiveUtils.Random.Next(SendBufSizeMin, SendBufSizeMax);
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
                                    curSendBufSize = NaiveUtils.Random.Next(SendBufSizeMin, SendBufSizeMax);
                                }
                            }
                        }
                    } while ((curbv = curbv.nextNode) != null);
                }
                if (bufcur > 0)
                    stream.Write(sendMsgBuf, 0, bufcur);
            }

        }

        private void buildHeader(byte opcode, int len, bool fin, byte[] buf, ref int bufIndex, bool ismask)
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

        private async Task _sendMsgAsync(byte opcode, BytesView buf, bool fin = true)
        {
            if (buf == null)
                throw new ArgumentNullException(nameof(buf));
            if (buf.bytes == null && buf.len > 0)
                throw new ArgumentNullException("buf.bytes");

            BytesView bv = buf;
            if (WriteFilter != null && bv.len > 0) {
                WriteFilter(bv);
            }
            var curSendBufSize = NaiveUtils.Random.Next(SendBufSizeMin, SendBufSizeMax);
            var len = bv.tlen;
            using (var handle = _sendMsgBufferPool.Get()) {
                int bufcur = 0;
                var sendMsgBuf = handle.Value;
                var isMasked = IsClient;
                buildHeader(opcode, bv.tlen, fin, sendMsgBuf, ref bufcur, isMasked);
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
                                    await stream.WriteAsync(sendMsgBuf, 0, bufcur).CAF();
                                    bufcur = 0;
                                    curSendBufSize = NaiveUtils.Random.Next(SendBufSizeMin, SendBufSizeMax);
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
                                    curSendBufSize = NaiveUtils.Random.Next(SendBufSizeMin, SendBufSizeMax);
                                }
                            }
                        }
                    } while ((curbv = curbv.nextNode) != null);
                }
                if (bufcur > 0)
                    await stream.WriteAsync(sendMsgBuf, 0, bufcur).CAF();
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
            BaseStream?.Dispose();
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

    public class BytesView
    {
        public byte[] bytes;
        public int offset;
        public int len;
        public BytesView nextNode;

        public BytesView()
        {
        }

        public BytesView(byte[] bytes)
        {
            Set(bytes);
        }

        public BytesView(byte[] bytes, int offset, int len)
        {
            Set(bytes, offset, len);
        }

        public void Set(byte[] bytes)
        {
            this.bytes = bytes;
            this.offset = 0;
            this.len = bytes.Length;
        }

        public void Set(byte[] bytes, int offset, int len)
        {
            this.bytes = bytes;
            this.offset = offset;
            this.len = len;
        }

        public void Set(BytesView bv)
        {
            this.bytes = bv.bytes;
            this.offset = bv.offset;
            this.len = bv.len;
        }

        public BytesView Clone()
        {
            return new BytesView(bytes, offset, len) { nextNode = nextNode };
        }

        public void Sub(int startIndex)
        {
            // TODO
            if (len < startIndex)
                throw new NotImplementedException();
            offset += startIndex;
            len -= startIndex;
        }

        public byte[] GetBytes() => GetBytes(0, tlen);
        public byte[] GetBytes(bool forceNew) => GetBytes(0, tlen, forceNew);
        public byte[] GetBytes(int offset, int len) => GetBytes(offset, len, false);
        public byte[] GetBytes(int offset, int len, bool forceNew)
        {
            if (!forceNew && offset == 0 & this.offset == 0 & len == bytes.Length) {
                return bytes;
            }
            var buf = new Byte[len];
            for (int i = 0; i < len; i++) {
                buf[i] = this[offset + i];
            }
            return buf;
        }

        public BytesView lastNode
        {
            get {
                var curnode = this;
                while (curnode.nextNode != null) {
                    curnode = curnode.nextNode;
                }
                return curnode;
            }
        }

        public int tlen
        {
            get {
                var len = 0;
                var curnode = this;
                do {
                    len += curnode.len;
                } while ((curnode = curnode.nextNode) != null);
                return len;
            }
        }

        public byte this[int index]
        {
            get {
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                var pos = 0;
                var curnode = this;
                do {
                    if (index < pos + curnode.len) {
                        return curnode.bytes[curnode.offset + index - pos];
                    }
                    pos += curnode.len;
                } while ((curnode = curnode.nextNode) != null);
                throw new IndexOutOfRangeException();
            }
            set {
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                var pos = 0;
                var curnode = this;
                do {
                    if (index < pos + curnode.len) {
                        curnode.bytes[curnode.offset + index - pos] = value;
                        return;
                    }
                    pos += curnode.len;
                } while ((curnode = curnode.nextNode) != null);
            }
        }

        public override string ToString()
        {
            int n = 1;
            var node = this;
            while ((node = node.nextNode) != null) {
                n++;
            }
            StringBuilder sb = new StringBuilder($"{{BytesView n={n} tlen={tlen}| ");
            var tooLong = tlen > 12;
            var shownSize = Math.Min(12, tlen);
            for (int i = 0; i < shownSize; i++) {
                sb.Append(this[i]);
                sb.Append(',');
            }
            sb.Remove(sb.Length - 1, 1);
            if (tooLong)
                sb.Append("...");
            sb.Append('}');
            return sb.ToString();
        }

        public static implicit operator BytesView(byte[] bytes)
        {
            return new BytesView(bytes);
        }

        public BufferEnumerator GetEnumerator()
        {
            return new BufferEnumerator(this);
        }

        public struct BufferEnumerator : IEnumerator<BytesView>
        {
            public BufferEnumerator(BytesView firstNode)
            {
                FirstNode = firstNode;
                Current = null;
            }

            BytesView FirstNode { get; }
            public BytesView Current { get; private set; }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (Current == null) {
                    Current = FirstNode;
                    return true;
                }
                Current = Current.nextNode;
                return Current != null;
            }

            public void Reset()
            {
                Current = null;
            }
        }
    }

    public struct BytesSegment
    {
        public byte[] Bytes;
        public int Offset;
        public int Len;

        public BytesSegment(byte[] bytes)
        {
            this.Bytes = bytes;
            this.Offset = 0;
            this.Len = bytes.Length;
        }

        public BytesSegment(BytesView bv)
        {
            this.Bytes = bv.bytes;
            this.Offset = bv.offset;
            this.Len = bv.len;
        }

        public BytesSegment(byte[] bytes, int offset, int len)
        {
            this.Bytes = bytes;
            this.Offset = offset;
            this.Len = len;
        }

        public void Set(byte[] bytes)
        {
            this.Bytes = bytes;
            this.Offset = 0;
            this.Len = bytes.Length;
        }

        public void Set(byte[] bytes, int offset, int len)
        {
            this.Bytes = bytes;
            this.Offset = offset;
            this.Len = len;
        }

        public byte[] GetBytes()
        {
            return GetBytes(false);
        }

        public byte[] GetBytes(bool forceCreateNew)
        {
            if (!forceCreateNew && (Offset == 0 & Len == Bytes.Length)) {
                return Bytes;
            }
            var buf = new Byte[Len];
            Buffer.BlockCopy(Bytes, Offset, buf, 0, Len);
            return buf;
        }

        public byte this[int index]
        {
            get => Bytes[Offset + index];
            set => Bytes[Offset + index] = value;
        }

        public BytesSegment Sub(int begin) => Sub(begin, Len - begin);
        public BytesSegment Sub(int begin, int count)
        {
            return new BytesSegment(Bytes, Offset + begin, count);
        }

        public void CopyTo(BytesSegment dst, int srcBegin, int count)
        {
            Buffer.BlockCopy(Bytes, Offset + srcBegin, dst.Bytes, dst.Offset, count);
        }

        public void CopyTo(BytesSegment dst, int srcBegin, int count, int dstBegin)
        {
            Buffer.BlockCopy(Bytes, Offset + srcBegin, dst.Bytes, dst.Offset + dstBegin, count);
        }

        public static implicit operator BytesSegment(byte[] bytes)
        {
            return new BytesSegment(bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CheckAsParameter()
        {
            if (Bytes == null)
                throw new ArgumentNullException("Bytes");
            if (Offset < 0 | Len < 0)
                throw new ArgumentOutOfRangeException("Offset and Len cannot be less than zero");
            if (Bytes.Length < Offset + Len)
                throw new ArgumentException("Bytes.Length < Offset + Len");
        }
    }

    public class Filterable
    {

        public Action<BytesView> ReadFilter;
        public Action<BytesView> WriteFilter;

        public void ClearFilter()
        {
            WriteFilter = null;
            ReadFilter = null;
        }

        public void AddWriteFilter(Action<BytesView> f)
        {
            WriteFilter = CombineFilter(WriteFilter, f);
        }

        public void AddReadFilter(Action<BytesView> f)
        {
            ReadFilter = CombineFilter(f, ReadFilter);
        }

        public static Action<BytesView> CombineFilter(Action<BytesView> f1, Action<BytesView> f2)
        {
            if (f1 == null)
                return f2;
            if (f2 == null)
                return f1;
            return (bv) => {
                f1(bv);
                f2(bv);
            };
        }

        [Obsolete]
        public void ApplyXORFilter(byte[] key)
        {
            var filter = GetXORFilter(key);
            AddWriteFilter(filter);
            AddReadFilter(filter);
        }

        [Obsolete]
        public void ApplyXORFilter2(byte[] key)
        {
            AddWriteFilter(GetXORFilter2(key));
            AddReadFilter(GetXORFilter2(key));
        }

        [Obsolete]
        public void ApplyXORFilter3(byte[] key)
        {
            AddWriteFilter(GetXORFilter3(key));
            AddReadFilter(GetXORFilter3(key));
        }

        [Obsolete]
        public void ApplyAesFilter(byte[] iv, byte[] key)
        {
            AddWriteFilter(GetAesFilter(true, iv, key));
            AddReadFilter(GetAesFilter(false, iv, key));
        }

        public void ApplyAesFilter2(byte[] key)
        {
            AddWriteFilter(GetAesFilter2(true, key));
            AddReadFilter(GetAesFilter2(false, key));
        }

        public void ApplyAesStreamFilter(byte[] key)
        {
            AddWriteFilter(GetAesStreamFilter(true, key));
            AddReadFilter(GetAesStreamFilter(false, key));
        }

        public void ApplyDeflateFilter()
        {
            AddWriteFilter(GetDeflateFilter(true));
            AddReadFilter(GetDeflateFilter(false));
        }

        [Obsolete]
        public static Action<BytesView> GetXORFilter(byte[] key)
        {
            return (bv) => {
                var tlen = bv.tlen;
                for (int i = 0; i < tlen; i++) {
                    bv[i] ^= key[i % key.Length];
                }
            };
        }

        [Obsolete]
        public static Action<BytesView> GetXORFilter2(byte[] key)
        {
            int lastpostion = 0;
            return (bv) => {
                int i = lastpostion;
                int tlen = bv.tlen;
                for (; i < tlen; i++) {
                    bv[i] ^= key[i % key.Length];
                }
                lastpostion = i % key.Length;
            };
        }

        [Obsolete]
        public static Action<BytesView> GetXORFilter3(byte[] key)
        {
            int lastpostion = 0;
            byte pass = 0;
            return (bv) => {
                int ki = lastpostion;
                int tlen = bv.tlen;
                for (int i = 0; i < tlen; i++) {
                    bv[i] ^= (byte)(key[ki++] + pass);
                    if (ki == key.Length) {
                        ki = 0;
                        pass++;
                    }
                }
                lastpostion = ki;
            };
        }

        [Obsolete]
        public static Action<BytesView> GetAesFilter(bool isEncrypt, byte[] iv, byte[] key)
        {
            int keySize = key.Length * 8, blockSize = iv.Length * 8;
            int blockBytesSize = blockSize / 8;
            byte[] buf = new byte[blockSize / 8];
            BytesView bvBuf = new BytesView(buf);
            var aesalg = Aes.Create();
            aesalg.Padding = PaddingMode.PKCS7;
            aesalg.KeySize = keySize;
            aesalg.BlockSize = blockSize;
            aesalg.IV = iv;
            aesalg.Key = key;
            return (bv) => {
                if (bv.len == 0)
                    return;
                var pos = 0;
                if (isEncrypt) {
                    using (var ms = new MemoryStream(bv.bytes, bv.offset, bv.len))
                    using (var cryStream = new CryptoStream(ms, aesalg.CreateEncryptor(), CryptoStreamMode.Read)) {
                        int read;
                        int oldbytesSize = bv.len - bv.len % blockBytesSize;
                        bv.len = oldbytesSize;
                        bv.nextNode = bvBuf;
                        while ((read = cryStream.Read(bv.bytes, bv.offset + pos, oldbytesSize - pos)) > 0) {
                            pos += read;
                        }
                        while ((read = cryStream.Read(buf, pos - oldbytesSize, buf.Length - (pos - oldbytesSize))) > 0) {
                            pos += read;
                        }
                    }
                } else {
                    using (var cryStream = new CryptoStream(new MemoryStream(bv.bytes, bv.offset, bv.len), aesalg.CreateDecryptor(), CryptoStreamMode.Read)) {
                        int read;
                        while ((read = cryStream.Read(buf, 0, blockBytesSize)) > 0) {
                            for (int i = 0; i < read; i++) {
                                bv.bytes[bv.offset + pos++] = buf[i];
                            }
                        }
                        bv.len = pos;
                    }
                }
            };
        }

        public static Action<BytesView> GetAesFilter2(bool isEncrypting, byte[] key)
        {
            int keySize = key.Length * 8, blockSize = 128;
            int blockBytesSize = blockSize / 8;
            byte[] buf = new byte[blockSize / 8];
            BytesView bvBuf = new BytesView(buf);
            var aesalg = new AesCryptoServiceProvider();
            aesalg.Padding = PaddingMode.PKCS7;
            //aesalg.Mode = CipherMode.CBC;
            aesalg.KeySize = keySize;
            aesalg.BlockSize = blockSize;
            aesalg.Key = key;
            bool firstPacket = true;
            if (isEncrypting) {
                return (bv) => {
                    if (firstPacket) {
                        firstPacket = false;
                        aesalg.GenerateIV();
                        var tmp = bv.Clone();
                        bv.Set(aesalg.IV);
                        bv.nextNode = tmp;
                        bv = tmp;
                    }
                    if (bv.len == 0)
                        return;
                    var pos = 0;
                    using (var ms = new MemoryStream(bv.bytes, bv.offset, bv.len))
                    using (var cryStream = new CryptoStream(ms, aesalg.CreateEncryptor(), CryptoStreamMode.Read)) {
                        int read;
                        int oldbytesSize = bv.len - bv.len % blockBytesSize;
                        bv.len = oldbytesSize;
                        bv.nextNode = bvBuf;
                        while ((read = cryStream.Read(bv.bytes, bv.offset + pos, oldbytesSize - pos)) > 0) {
                            pos += read;
                        }
                        while ((read = cryStream.Read(buf, pos - oldbytesSize, buf.Length - (pos - oldbytesSize))) >
                               0) {
                            pos += read;
                        }
                        aesalg.IV = buf;
                    }
                };
            } else {
                return (bv) => {
                    if (firstPacket) {
                        firstPacket = false;
                        aesalg.IV = bv.GetBytes(0, blockBytesSize);
                        //bv.offset += blockBytesSize;
                        for (int i = 0; i < bv.len - blockBytesSize; i++) {
                            bv[i] = bv[i + blockBytesSize];
                        }
                        bv.len -= blockBytesSize;
                    }
                    if (bv.len == 0)
                        return;
                    var pos = 0;
                    using (var cryStream = new CryptoStream(new MemoryStream(bv.bytes, bv.offset, bv.len),
                        aesalg.CreateDecryptor(), CryptoStreamMode.Read)) {
                        int read;
                        int lastBlockPos = bv.len - blockBytesSize;
                        for (int i = 0; i < blockBytesSize; i++) {
                            buf[i] = bv[lastBlockPos + i];
                        }
                        aesalg.IV = buf;
                        while ((read = cryStream.Read(bv.bytes, bv.offset + pos, bv.len - pos)) > 0) {
                            pos += read;
                        }
                        bv.len = pos;
                    }
                };
            }
        }

        const int blocksPerPass = 16;

        private static readonly byte[] zeroesBytes = new byte[16 * blocksPerPass];

        public static Action<BytesView> GetAesStreamFilter(bool isEncrypt, byte[] key) // is actually AES OFB
        {
            int keySize = key.Length * 8, blockSize = 128;
            int blockBytesSize = blockSize / 8;
            int encryptedZerosSize = blockBytesSize;
            byte[] buf = new byte[blockSize / 8];
            BytesView bvBuf = new BytesView(buf);
            var aesalg = new AesCryptoServiceProvider();
            aesalg.Mode = CipherMode.CBC; // to generate OFB keystream, use CBC with all zeros byte array as input.
            aesalg.KeySize = keySize;
            aesalg.BlockSize = blockSize;
            aesalg.Key = key;
            ICryptoTransform enc = null;
            byte[] keystreamBuffer = null;
            int keystreamBufferPos = 0;
            return bv => {
                if (enc == null) {
                    if (isEncrypt) {
                        bv.nextNode = bv.Clone();
                        bv.Set(aesalg.IV);
                        bv = bv.nextNode;
                    } else {
                        aesalg.IV = bv.GetBytes(0, blockBytesSize);
                        for (int i = 0; i < bv.len - blockBytesSize; i++) {
                            bv[i] = bv[i + blockBytesSize];
                        }
                        bv.len -= blockBytesSize;
                    }
                    enc = aesalg.CreateEncryptor();
                    if (enc.CanTransformMultipleBlocks) {
                        encryptedZerosSize *= blocksPerPass;
                    }
                    keystreamBuffer = new byte[encryptedZerosSize];
                    keystreamBufferPos = encryptedZerosSize;
                }
                unsafe {
                    fixed (byte* ksBuf = keystreamBuffer)
                        do {
                            var pos = bv.offset;
                            var end = pos + bv.len;
                            if (bv.bytes == null)
                                throw new ArgumentNullException("bv.bytes");
                            if (bv.bytes.Length < end)
                                throw new ArgumentException("bv.bytes.Length < offset + len");
                            fixed (byte* bytes = bv.bytes)
                                while (pos < end) {
                                    var remainningTmp = encryptedZerosSize - keystreamBufferPos;
                                    if (remainningTmp == 0) {
                                        remainningTmp = encryptedZerosSize;
                                        keystreamBufferPos = 0;
                                        enc.TransformBlock(zeroesBytes, 0, encryptedZerosSize, keystreamBuffer, 0);
                                    }
                                    var tmpEnd = pos + remainningTmp;
                                    var thisEnd = end < tmpEnd ? end : tmpEnd;
                                    var thisCount = thisEnd - pos;
                                    NaiveUtils.XorBytesUnsafe(ksBuf + keystreamBufferPos, bytes + pos, thisCount);
                                    keystreamBufferPos += thisCount;
                                    pos += thisCount;
                                }
                            bv = bv.nextNode;
                        }
                        while (bv != null);
                }
            };
        }

        public static Action<BytesView> GetDeflateFilter(bool isCompress)
        {
            return (bv) => {
                using (var tostream = new MemoryStream()) {
                    using (var ds = new DeflateStream(
                        isCompress ? tostream : new MemoryStream(bv.bytes, bv.offset, bv.len),
                        isCompress ? CompressionMode.Compress : CompressionMode.Decompress)) {
                        if (isCompress) {
                            ds.Write(bv.bytes, bv.offset, bv.len);
                            ds.Flush();
                        } else {
                            NaiveUtils.StreamToStream(ds, tostream);
                        }
                    }
                    bv.Set(tostream.ToArray());
                }
            };
        }

        public static Action<BytesView> GetHashFilter(bool isWrite)
        {
            return (bv) => {
                if (isWrite) {
                    if (bv == null || bv.tlen == 0)
                        return;
                    var bytes = bv.GetBytes();
                    using (var alg = new MD5CryptoServiceProvider()) {
                        var b = alg.ComputeHash(bytes);
                        bv.lastNode.nextNode = new BytesView(b);
                    }
                } else {
                    if (bv == null || bv.tlen == 0)
                        return;
                    using (var alg = new MD5CryptoServiceProvider()) {
                        var hb = alg.HashSize / 8;
                        var bytes = bv.GetBytes(0, bv.tlen - hb);
                        var hash = alg.ComputeHash(bytes);
                        var rhash = bv.GetBytes(bv.tlen - hb, hb);
                        if (!hash.SequenceEqual(rhash)) {
                            Logging.error("wrong hash!");
                            throw new Exception("wrong hash");
                        }
                        bv.len -= hb;
                    }
                }
            };
        }
    }

    public static class BytesSegmentExt
    {
        public static void Write(this Stream stream, BytesSegment bs)
        {
            stream.Write(bs.Bytes, bs.Offset, bs.Len);
        }

        public static Task WriteAsync(this Stream stream, BytesSegment bs)
        {
            return stream.WriteAsync(bs.Bytes, bs.Offset, bs.Len);
        }

        public static Task WriteAsync(this Stream stream, BytesSegment bs, CancellationToken cancellationToken)
        {
            return stream.WriteAsync(bs.Bytes, bs.Offset, bs.Len, cancellationToken);
        }

        public static int Read(this Stream stream, BytesSegment bs)
        {
            return stream.Read(bs.Bytes, bs.Offset, bs.Len);
        }

        public static Task<int> ReadAsync(this Stream stream, BytesSegment bs)
        {
            return stream.ReadAsync(bs.Bytes, bs.Offset, bs.Len);
        }

        public static Task<int> ReadAsync(this Stream stream, BytesSegment bs, CancellationToken cancellationToken)
        {
            return stream.ReadAsync(bs.Bytes, bs.Offset, bs.Len, cancellationToken);
        }
    }
}

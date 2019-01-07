using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{
    // Frame format:
    // [header byte] [payload]
    // header byte: sbyte, short (extended), int (extended)
    //   if eq MinValue: reserved channel ([MinValue] [channel id] [opcode])
    //   else if is sending frame: channel id
    //   else if is recving frame: negatived channel id

    // Channel ID:
    //             MinValue : reserved channel (used for opcodes)
    // (MinValue + 1) to -1 : can be created by remote
    //                    0 : main channel
    //        1 to MaxValue : can be created by local

    public class IncrNumberGenerator
    {
        private int Id;
        public int Get()
        {
            return Interlocked.Increment(ref Id);
        }
    }

    public class NaiveMultiplexing
    {
        public NaiveMultiplexing(IMsgStream baseStream)
        {
            BaseStream = baseStream;
            MainChannel = new Channel(this, MainId);
            ReservedChannel = new Channel(this, ReservedId);
            addChannel_NoLock(MainChannel);
            addChannel_NoLock(ReservedChannel);
        }

        public IMsgStream BaseStream;

        public const int MainId = 0, ReservedId = int.MinValue,
                            MaxId = int.MaxValue,
                            MinId = int.MinValue + 1;

        private static IncrNumberGenerator idGen = new IncrNumberGenerator();
        public int Id { get; } = idGen.Get();

        public bool SendChannelCreatingFrame = false;
        public bool NoNegativeId;

        public int TotalLocalChannels { get; private set; }
        public int TotalRemoteChannels { get; private set; }

        public bool Closed { get; private set; }
        private void ThrowIfClosed()
        {
            if (Closed)
                throw new DisconnectedException($"{this} have been closed.");
        }

        public Channel MainChannel;
        internal Channel ReservedChannel;

        public Dictionary<int, Channel> Channels = new Dictionary<int, Channel>();

        private int sendChannelIdLength = 1, recvChannelIdLength = 1;

        object _channelsLock => Channels;
        object _sendLock = new object();

        int currentMaxId => sendChannelIdLength == 1 ? sbyte.MaxValue :
                            sendChannelIdLength == 2 ? short.MaxValue :
                            sendChannelIdLength == 4 ? int.MaxValue :
                            throw new Exception($"unexpected recvChannelIdLength ({recvChannelIdLength})");

        public event Action<Channel> NewRemoteChannel;

        private Task _mainReadTask;

        private Channel getChannelById(int id)
        {
            Channels.TryGetValue(id, out var ch); // ch == null if not found
            return ch;
        }

        private void addChannel_NoLock(Channel ch)
        {
            var id = ch.Id;
            try {
                Channels.Add(id, ch);
            } catch (ArgumentException e) {
                throw new Exception($"channel id {ch.Id} already exists in {this}.", e);
            }
            if (id != MainId & id != ReservedId)
                if (ch.Id > 0)
                    TotalLocalChannels++;
                else
                    TotalRemoteChannels++;
        }

        private void removeChannel(Channel ch)
        {
            var id = ch.Id;
            if (Channels.ContainsKey(id) == false) {
                throw new Exception($"channel id {ch.Id} does not exist in {this}.");
            }
            Channels.Remove(id);
            if (id != MainId & id != ReservedId)
                if (ch.Id > 0)
                    TotalLocalChannels--;
                else
                    TotalRemoteChannels--;
        }

        void extendChannels_NoSendLock()
        {
            if (sendChannelIdLength == 1) {
                Logging.info($"{this}: extending IdLength to 16 bits.");
                SendParentOpcode(ParentOpcode.ExtendIdLength16).Forget();
                sendChannelIdLength = 2;
            } else if (sendChannelIdLength == 2) {
                Logging.info($"{this}: extending IdLength to 32 bits.");
                SendParentOpcode(ParentOpcode.ExtendIdLength32).Forget();
                sendChannelIdLength = 4;
            } else {
                throw new Exception($"sendChannelIdLength == {sendChannelIdLength}");
            }
        }

        public async Task Start()
        {
            if (_mainReadTask != null) {
                await _mainReadTask;
                return;
            }
            try {
                await (_mainReadTask = MainReadLoop()).CAF();
                Logging.warning($"{this} stopped.");
            } catch (Exception e) {
                if (e.IsConnectionException()) {
                    Logging.error($"{this} stopped: {e.Message}");
                } else {
                    Logging.exception(e, Logging.Level.Error, $"{this} stopped with exception.");
                }
            } finally {
                Close(true);
            }
        }

        public void Close(bool closeBaseStream = true)
        {
            lock (_channelsLock) {
                if (Closed)
                    return;
                Closed = true;
                foreach (var item in Channels) {
                    item.Value.ParentChannelsClosed();
                }
                Channels.Clear();
            }
            if (closeBaseStream)
                BaseStream.Close(new CloseOpt(CloseType.Close));
        }

        private int _latestId = 0;
        public Task<Channel> CreateChannel()
        {
            lock (_channelsLock) {
                ThrowIfClosed();
                var id = _latestId + 1;
                var findMax = currentMaxId;
            retry:
                for (int i = 1; i <= findMax; i++) {
                    if (id > findMax) {
                        id = 1;
                    }
                    if (getChannelById(id) == null) {
                        _latestId = id;
                        return CreateChannel(id);
                    }
                    id++;
                }
                if (findMax < MaxId) {
                    findMax = MaxId;
                    goto retry;
                }
            }
            throw new Exception("no channel id available.");
        }

        public async Task<Channel> CreateChannel(int id)
        {
            if (id <= 0)
                throw new ArgumentOutOfRangeException(nameof(id), "channel id <= 0.");
            if (id > MaxId) // should never happen since MaxId == Int32.MaxValue
                throw new ArgumentOutOfRangeException(nameof(id), $"channel id > {MaxId}.");
            Channel ch;
            lock (_channelsLock) {
                ThrowIfClosed();
                lock (_sendLock)
                    while (id > currentMaxId)
                        extendChannels_NoSendLock();
                if (getChannelById(id) != null)
                    throw new Exception($"channel {id} already exists in {this}.");
                ch = new Channel(this, id);
                addChannel_NoLock(ch);
            }
            if (SendChannelCreatingFrame) {
                await ch.SendRsvOpcode(Channel.Opcode.Create).CAF();
            }
            return ch;
        }

        private Channel CreateRemoteChannel(int id)
        {
            var ch = new Channel(this, id);
            lock (_channelsLock) {
                ThrowIfClosed();
                addChannel_NoLock(ch);
            }
            return ch;
        }

        private async Task MainReadLoop()
        {
            int syncCount = 0;
            while (true) {
                if (syncCount > 64) {
                    syncCount = 0;
                    await Task.Yield();
                }
                Msg r = await BaseStream.RecvMsgR(null).SyncCounter(ref syncCount);
                if (r.IsEOF)
                    throw new DisconnectedException("EOF Msg.");
                var frame = Frame.unpack(this, r.Data);
                if (!NoNegativeId & frame.Id != ReservedId) {
                    frame.Id = -frame.Id;
                }
                var msg = new Msg(frame.Data);
                if (frame.Id == ReservedId) {
                    handleRsvMsg(msg);
                    continue;
                }
                Channel ch;
                lock (_channelsLock)
                    ch = getChannelById(frame.Id);
                if (ch != null) {
                    ch.MsgReceived(msg);
                } else {
                    // Enqueue the msg before invoking the event to reduce some waiting overhead.
                    ch = CreateRemoteChannel(frame.Id);
                    ch.MsgReceived(msg);
                    NewRemoteChannel?.Invoke(ch);
                }
            }
        }

        private void handleRsvMsg(Msg msg)
        {
            var data = msg.Data;
            int cur = 0;
            int chid = Frame.getChId(this, data, ref cur);
            if (!NoNegativeId & chid != ReservedId) {
                chid = -chid;
            } else if (chid == ReservedId) {
                var opcode = (ParentOpcode)data[cur];
                if (opcode == ParentOpcode.ExtendIdLength16 || opcode == ParentOpcode.ExtendIdLength32) {
                    recvChannelIdLength = opcode == ParentOpcode.ExtendIdLength16 ? 2 : 4;
                    if (sendChannelIdLength < recvChannelIdLength) {
                        lock (_sendLock) {
                            SendParentOpcode(opcode).Forget();
                            sendChannelIdLength = recvChannelIdLength;
                        }
                        //Logging.info($"{this}: IdLength is extended to {recvChannelIdLength * 8} by remote.");
                    } else {
                        Logging.info($"{this}: IdLength is extended to {recvChannelIdLength * 8} bits by local.");
                    }
                } else {
                    Logging.error($"BUG?: {this} got unknown opcode {opcode}.");
                    SendParentOpcode(ParentOpcode.UnknownOpcodeReceived).Forget();
                }
                return;
            }
            var ch = getChannelById(chid);
            if (ch == null) {
                var opcode = (Channel.Opcode)data[cur]; // no ++ here
                if (opcode != Channel.Opcode.Create) {
                    Logging.error($"BUG: {this} got opcode {opcode} but channel {chid} does not exist.");
                    return;
                }
                ch = CreateRemoteChannel(chid);
            }
            data.SubSelf(cur);
            ch.RsvMsgReceived(msg);
        }

        public Task SendMsg(Channel ch, Msg msg)
        {
            ThrowIfClosed();
            lock (_sendLock) {
                return SendMsg_NoLock(ch, msg);
            }
        }

        private Task SendMsg_NoLock(Channel ch, Msg msg)
        {
            var frame = new Frame(ch.Id, msg.Data).pack(this);
            return BaseStream.SendMsg(frame);
        }

        private Task SendParentOpcode(ParentOpcode opcode)
        {
            return SendOpcode(ReservedChannel, (byte)opcode);
        }

        internal Task SendOpcode(Channel ch, Channel.Opcode opcode)
        {
            return SendOpcode(ch, (byte)opcode);
        }

        internal async Task SendOpcode(Channel ch, byte opcode)
        {
            BytesView bv = new BytesView(new byte[4 + 1]);
            Task task = null;
            lock (_sendLock) {
                int cur = 0;
                Frame.writeChId(this, bv, ch.Id, ref cur);
                bv[cur++] = opcode;
                task = this.SendMsg_NoLock(ReservedChannel, bv);
            }
            await task.CAF();
        }

        internal void Remove(Channel ch)
        {
            removeChannel(ch);
        }

        public override string ToString()
        {
            return $"{{chs#{Id}{(Closed ? " closed" : "")} count={TotalLocalChannels}+{TotalRemoteChannels}}}";
        }

        enum ParentOpcode
        {
            Rsv,
            UnknownOpcodeReceived,
            ExtendIdLength16,
            ExtendIdLength32
        }

        private struct Frame
        {
            public int Id;
            public BytesView Data;

            public Frame(int id, BytesView data)
            {
                Id = id;
                Data = data;
            }

            public static Frame unpack(NaiveMultiplexing m, BytesView bv)
            {
                var cur = 0;
                int id = getChId(m, bv, ref cur);
                bv.SubSelf(cur);
                return new Frame { Id = id, Data = bv };
            }

            public static int getChId(NaiveMultiplexing m, BytesView bv, ref int cur)
            {
                int id = 0;
                int idLen = m.recvChannelIdLength;
                if (idLen == 1) {
                    id = (sbyte)bv[cur++];
                    if (id == sbyte.MinValue)
                        id = ReservedId;
                } else if (idLen == 2) {
                    id = (short)(bv[cur++] << 8 | bv[cur++]);
                    if (id == short.MinValue)
                        id = ReservedId;
                } else if (idLen == 4) {
                    for (int i = 4 - 1; i >= 0; i--) {
                        id |= (bv[cur++] << (i * 8));
                    }
                } else {
                    string message = $"BUG?: unexpected recvChannelIdLength ({idLen})";
                    Logging.error($"{m}: " + message);
                    throw new Exception(message);
                }
                return id;
            }

            public static int getSendChIdLen(NaiveMultiplexing m) => m.sendChannelIdLength;

            public static void writeChId(NaiveMultiplexing m, BytesView bv, int id, ref int cur)
            {
                int idLen = m.sendChannelIdLength;
                if (idLen == 1) {
                    var i = id == ReservedId ? (sbyte.MinValue) : (sbyte)id;
                    bv[cur++] = (byte)i;
                } else if (idLen == 2) {
                    var i = id == ReservedId ? (short.MinValue) : (short)id;
                    bv[cur++] = (byte)(i >> 8);
                    bv[cur++] = (byte)(i);
                } else if (idLen == 4) {
                    for (int i = 4 - 1; i >= 0; i--) {
                        bv[cur++] = (byte)(id >> (i * 8));
                    }
                } else {
                    string message = $"BUG?: unexpected recvChannelIdLength ({m.recvChannelIdLength})";
                    Logging.error($"{m}: " + message);
                    throw new Exception(message);
                }
            }

            public BytesView pack(NaiveMultiplexing m, BytesView headerBv = null)
            {
                Debug.Assert(Id <= m.currentMaxId, $"Id > currentMaxId ({m})");
                if (headerBv == null)
                    headerBv = new BytesView(new byte[getSendChIdLen(m)]);
                var cur = 0;
                writeChId(m, headerBv, Id, ref cur);
                headerBv.len = cur;
                headerBv.nextNode = Data;
                return headerBv;
            }
        }
    }

    public class Channel : IMsgStream, IDisposable, IMsgStreamReadR
    {
        public NaiveMultiplexing Parent { get; }
        public int Id { get; }

        FilterBase _dataFilter;
        public FilterBase DataFilter => _dataFilter ?? (_dataFilter = new FilterBase());

        public static int DefaultBlockThreshold = 256 * 1024;
        public static int DefaultResumeThreshold = 128 * 1024;
        public int ResumeThreshold = DefaultResumeThreshold;
        public int BlockThreshold = DefaultBlockThreshold;

        MsgStreamStatus IMsgStream.State
        {
            get {
                return IsClosingOrClosed ? MsgStreamStatus.Close :
                    State == StateEnum.EOFSent ? MsgStreamStatus.Shutdown :
                    State == StateEnum.EOFReceived ? MsgStreamStatus.RemoteShutdown :
                    MsgStreamStatus.Open;
            }
        }

        public StateEnum State
        {
            get { return _state; }
            private set {
                this.setState(value);
            }
        }

        private void setState(StateEnum value, bool noLog = false)
        {
            if (!noLog)
                debug("state", value);
            var old = _state;
            var oldClosed = IsClosed;
            var oldRecvEOF = IsRecvEOF(old);
            _state = value;
            if (!oldRecvEOF & IsRecvEOF(value))
                onRecvEOF();
            if (!oldClosed & IsClosed)
                onClosed();
        }

        private static bool IsRecvEOF(StateEnum value)
        {
            return value >= StateEnum.Closed
                | value == StateEnum.EOFReceived
                | value == StateEnum.ClosingByRemote;
        }

        public enum StateEnum
        {
            Open = 0,
            // if 'EOF' received, then -> EOFReceived
            // if 'Close' received, then reply 'Close' -> ClosedByRemote (fast close by remote)
            // if Close(CloseType.Shutdown) called, then send 'EOF' -> EOFSent
            // if Close(CloseType.Close) called, then send 'Close' -> ClosingByLocal (fast close by local)

            EOFReceived = 1, // waiting for Close(), then reply 'Close' -> ClosingByRemote
                             //             'Close', then reply 'Close' -> ClosedByRemote

            //// Shutdown: (no sending except 'BlockRemote')
            EOFSent = 2, // waiting for 'EOF', then reply 'Close' -> ClosingByLocal
                         //             'Close', then reply 'Close' -> ClosedByLocal

            //// Closing: (no sending)
            Closing = 3,
            ClosingByLocal = 3, // waiting for 'Close', then -> ClosedByLocal
            ClosingByRemote = 7, // waiting for 'Close', then -> ClosedByRemote

            //// Channel ID is released:
            Closed = 11,
            ClosedByLocal = 11, // 'Close' has been sent & received
            ClosedByRemote = 15, // 'Close' has been sent & received

            ParentClosed = 19

            // normal path (active close): Open -> EOFSent -> ClosedByLocal
            // normal path (passive close): Open -> EOFReceived -> ClosedByRemote
        }

        private bool readEOF = false;

        public bool IsShutdownOrClosed => State >= StateEnum.EOFSent;
        public bool IsClosed => State >= StateEnum.Closed;
        public bool IsClosing => State >= StateEnum.Closing & State < StateEnum.Closed;
        public bool IsClosingOrClosed => State >= StateEnum.Closing;

        private bool blockingRemote = false;
        private int queuedSize;

        private object _syncroot => recvQueue;

        private JustAwaiter blockSendAwaiter = JustAwaiter.NewCompleted();
        private AsyncQueue<Msg> recvQueue = new AsyncQueue<Msg>();
        private StateEnum _state;

        internal Channel(NaiveMultiplexing channels, int id)
        {
            Parent = channels;
            Id = id;
            State = StateEnum.Open;
        }

        internal void MsgReceived(Msg msg)
        {
            if (recvQueue.Enqueue(msg) && !msg.IsEOF) {
                Interlocked.Add(ref queuedSize, msg.Data.tlen);
                CheckBlockingRemote();
            }
        }

        private void CheckBlockingRemote()
        {
            var blocking = _CheckBlocking();
            if (blocking == blockingRemote)
                return;
            lock (_syncroot) {
                blocking = _CheckBlocking();
                if (blocking == blockingRemote)
                    return;
                blockingRemote = blocking;
                if (State == StateEnum.EOFSent | State == StateEnum.Open)
                    SendRsvOpcode(blocking ? Opcode.BlockSend : Opcode.ResumeSend);
            }
        }

        private bool _CheckBlocking()
        {
            bool blocking = blockingRemote;
            if (queuedSize < ResumeThreshold && blocking)
                blocking = false;
            else if (queuedSize > BlockThreshold && !blocking)
                blocking = true;
            return blocking;
        }

        private void SetBlockingSend(bool blocking)
        {
            blockSendAwaiter.SetBlocking(blocking);
        }

        private void logUnexpectedOpcode(Opcode opcode)
        {
            Logging.warning($"{this} BUG?: unexpected '{opcode}' when {State}.");
        }

        public enum Opcode : byte
        {
            Rsv,
            Create,
            EOF,
            BlockSend,
            ResumeSend,
            Close,
            UnknownOpcodeReceived
        }

        public void RsvMsgReceived(Msg msg)
        {
            var bv = msg.Data;
            var opcode = (Opcode)bv[0];
            debug("<- opcode", opcode);
            lock (_syncroot) {
                if (opcode == Opcode.Create) {

                } else if (opcode == Opcode.EOF) {
                    if (State == StateEnum.Open) {
                        State = StateEnum.EOFReceived;
                    } else if (State == StateEnum.EOFSent) {
                        // simultaneous:
                        // EOF      ->      <- EOF
                        EnterClosing(false);
                    } else if (IsClosing) {
                        // simultaneous:
                        // Close    ->      <- EOF
                        // TODO: a new state in this case
                        State = StateEnum.ClosingByRemote;
                    } else {
                        logUnexpectedOpcode(opcode);
                    }
                } else if (opcode == Opcode.Close) {
                    if (State >= StateEnum.Closed) {
                        logUnexpectedOpcode(opcode);
                        return;
                    }
                    if (IsClosing) {
                        State = GetLastCloseReceivedState(State == StateEnum.ClosingByRemote);
                    } else {
                        SendRsvOpcodeThenChangeState(Opcode.Close, GetLastCloseReceivedState(State != StateEnum.EOFSent));
                    }
                } else if (opcode == Opcode.BlockSend) {
                    if (IsClosed) {
                        if (!IsClosing)
                            logUnexpectedOpcode(opcode);
                        return;
                    }
                    Logging.warning("block send");
                    SetBlockingSend(true);
                } else if (opcode == Opcode.ResumeSend) {
                    if (IsClosed) {
                        if (!IsClosing)
                            logUnexpectedOpcode(opcode);
                        return;
                    }
                    Logging.warning("resume send");
                    SetBlockingSend(false);
                } else {
                    Logging.warning($"{this} BUG?: unknown opcode: {opcode}");
                    SendRsvOpcode(Opcode.UnknownOpcodeReceived);
                }
            }
        }

        public Task SendMsg(Msg msg)
        {
            ThrowIfCantSend();
            if (blockSendAwaiter.IsCompleted == false) {
                return _SendMsg_Blocking(msg);
            } else {
                return _SendMsg(msg);
            }
        }

        private async Task _SendMsg_Blocking(Msg msg)
        {
            debug("blocking send", msg.Data?.tlen);
            await blockSendAwaiter;
            debug("resumed send", msg.Data?.tlen);
            await _SendMsg(msg);
        }

        private Task _SendMsg(Msg msg)
        {
            _dataFilter?.OnWrite(msg.Data);
            Task task;
            lock (_syncroot) {
                ThrowIfCantSend();
                write += msg.Data?.tlen ?? 0;
                writec++;
                task = Parent.SendMsg(this, msg);
            }
            return task;
        }

        internal Task SendRsvOpcode(Opcode opcode)
        {
            debug("-> opcode", opcode);
            return Parent.SendOpcode(this, opcode);
        }

        internal void SendRsvOpcodeThenChangeState(Opcode opcode, StateEnum state)
        {
            debug("-> opstate", opcode, state);
            Parent.SendOpcode(this, opcode).Forget();
            setState(state, true);
        }

        public async Task<Msg> RecvMsg(BytesView buf)
        {
            return await RecvMsgR(buf);
        }

        ReusableAwaiter<Msg> raR = new ReusableAwaiter<Msg>();
        ReusableAwaiter<Msg> raR_deq;
        Action raR_continuation;
        bool raR_noWaiting;

        public AwaitableWrapper<Msg> RecvMsgR(BytesView buf)
        {
            if (readEOF)
                ThrowInvalidOperation();
            raR.Reset();
            raR_deq = recvQueue.DequeueAsync(out raR_noWaiting);
            if (raR_continuation == null) {
                raR_continuation = () => {
                    Msg m;
                    var tmp = raR_deq;
                    raR_deq = null;
                    if (!tmp.TryGetResult(out m, out var ex)) {
                        raR.SetException(ex);
                    } else {
                        try {
                            m = rar_continuation2(m);
                        } catch (Exception e) {
                            raR.SetException(e);
                            return;
                        }
                        raR.SetResult(m);
                    }
                };
            }
            raR_deq.OnCompleted(raR_continuation);

            return new AwaitableWrapper<Msg>(raR);
        }

        private Msg rar_continuation2(Msg m)
        {
            if (raR_noWaiting && !m.IsEOF) {
                Interlocked.Add(ref queuedSize, -m.Data.tlen);
                CheckBlockingRemote();
            }
            if (m.IsEOF) {
                readEOF = true;
                if (recvQueue.Count > 0) {
                    Logging.warning($"{this} BUG: unexpected item(s) (count={recvQueue.Count}) in recv queue after EOF");
                }
            } else {
                _dataFilter?.OnRead(m.Data);
                read += m.Data.tlen;
                readc++;
            }
            return m;
        }

        /// <summary>
        /// Not throw if Open or EOFReceived
        /// </summary>
        private void ThrowIfCantSend()
        {
            if (IsShutdownOrClosed)
                ThrowInvalidOperation();
        }

        /// <summary>
        /// Not throw if Open or EOFReceived or EOFSent
        /// </summary>
        private void ThrowIfClosingOrClosed()
        {
            if (IsClosingOrClosed)
                ThrowInvalidOperation();
        }

        private void ThrowInvalidOperation()
        {
            throw new InvalidOperationException($"Channel status: {State}");
        }

        public void Shutdown()
        {
            lock (_syncroot) {
                Shutdown_NoLock();
            }
        }

        private void Shutdown_NoLock()
        {
            ThrowIfCantSend();
            if (State == StateEnum.Open) {
                SendRsvOpcodeThenChangeState(Opcode.EOF, StateEnum.EOFSent);
            } else { // EOFReceived
                EnterClosing(true);
            }
        }

        public void Close()
        {
            lock (_syncroot) {
                Close_NoLock();
            }
        }

        private void Close_NoLock()
        {
            if (State == StateEnum.EOFReceived) {
                EnterClosing(true);
            } else {
                ThrowIfClosingOrClosed();
                EnterClosing(false);
            }
        }

        public void CloseIfOpen()
        {
            lock (_syncroot) {
                if (!IsClosingOrClosed)
                    Close_NoLock();
            }
        }

        public Task Close(CloseOpt closeOpt)
        {
            if (closeOpt.CloseType == CloseType.Shutdown) {
                Shutdown();
            } else {
                CloseIfOpen();
            }
            return AsyncHelper.CompletedTask;
        }

        private void onRecvEOF()
        {
            recvQueue.Enqueue(Msg.EOF);
        }

#if DEBUG
        private bool isClosed;
#endif

        private void onClosed()
        {
            lock (_syncroot) {
#if DEBUG
                if (isClosed) {
                    Logging.logWithStackTrace($"BUG: {this} onClosed more than 1 time", Logging.Level.Warning);
                    return;
                }
                isClosed = true;
#endif
                SetBlockingSend(false);
            }
        }

        private void EnterClosing(bool byRemote)
        {
            SendRsvOpcodeThenChangeState(Opcode.Close, byRemote ? StateEnum.ClosingByRemote : StateEnum.ClosingByLocal);
        }

        private StateEnum GetLastCloseReceivedState(bool byRemote)
        {
            Parent.Remove(this);
            return byRemote ? StateEnum.ClosedByRemote : StateEnum.ClosedByLocal;
        }

        internal void ParentChannelsClosed()
        {
            // this causes "Collection was modified" exception:
            //Parent.Remove(this);
            State = StateEnum.ParentClosed;
        }

        public static bool Debug =
#if DEBUG
            true;
#else
            false;
#endif

        private void debug<T>(string str, T obj)
        {
            if (Debug)
                debugForce(str, obj);
        }

        private void debugForce<T>(string str, T obj)
        {
            Logging.log($"{this} {str}: {obj}", Logging.Level.Debug);
        }

        private void debug<T1, T2>(string str, T1 obj1, T2 obj2)
        {
            if (Debug)
                Logging.log($"{this} {str}: {obj1}, {obj2}", Logging.Level.Debug);
        }

        long write = 0, read = 0;
        int writec = 0, readc = 0;

        public BytesCounterValue CounterR => new BytesCounterValue { Bytes = read, Packets = readc };
        public BytesCounterValue CounterW => new BytesCounterValue { Bytes = write, Packets = writec };

        public override string ToString()
        {
            var chname = Id == NaiveMultiplexing.ReservedId ? "Rsv" : Id.ToString();
            return "{#" + Parent?.Id + "ch" + chname + " R/W=" + CounterR + "/" + CounterW + (queuedSize > 0 ? " queued=" + queuedSize : null) + " " + State + "}";
        }

        public void Dispose()
        {
            CloseIfOpen();
        }
    }

    public class AsyncQueue<T>
    {
        private ReusableAwaiter<T> awaiter = new ReusableAwaiter<T>();
        private Queue<T> queue = new Queue<T>();
        private bool isWaiting = false;

        public int Count => queue.Count;

        private SpinLock _lock = new SpinLock(false);
        private const int lockTimeout = 1000;

        public bool IsWaiting => isWaiting;

        public bool Enqueue(T obj)
        {
            bool lt = false;
            _lock.TryEnter(lockTimeout, ref lt);
            if (isWaiting) {
                isWaiting = false;
                _lock.Exit();
                awaiter.SetResult(obj);
                return false;
            }
            queue.Enqueue(obj);
            _lock.Exit();
            return true;

        }

        public ReusableAwaiter<T> DequeueAsync(out bool noAwaiting)
        {
            bool lt = false;
            if (isWaiting) {
                throw new Exception("another dequeuing task is running");
            }
            awaiter.Reset();
            _lock.TryEnter(lockTimeout, ref lt);
            if (queue.Count > 0) {
                var result = queue.Dequeue();
                _lock.Exit();
                noAwaiting = true;
                awaiter.SetResult(result);
                return awaiter;
            } else {
                isWaiting = true;
                _lock.Exit();
                noAwaiting = false;
                return awaiter;
            }
        }

        public bool TryDequeue(out T value)
        {
            bool lt = false;
            _lock.TryEnter(lockTimeout, ref lt);
            if (queue.Count > 0) {
                value = queue.Dequeue();
                _lock.Exit();
                return true;
            }
            _lock.Exit();
            value = default(T);
            return false;
        }
    }
}
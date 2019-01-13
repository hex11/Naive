﻿using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public abstract class InConnection : ConnectArgument
    {
        protected InConnection(IAdapter inAdapter) : base(inAdapter)
        {
            var adap = inAdapter.GetAdapter();
            BytesCountersRW = new BytesCountersRW(adap.BytesCountersRW);
        }

        public virtual IMyStream DataStream { get; set; }

        public ConnectResult ConnectResult { get; private set; }
        public bool IsHandled => ConnectResult != null;

        public AdapterRef Redirected { get; set; }
        public bool IsRedirected => Redirected != null;

        public IAdapter RunningHandler { get; internal set; }

        public bool IsStoppingRequested { get; private set; }
        public bool IsFinished { get; internal set; }

        static IncrNumberGenerator idGenerator = new IncrNumberGenerator();
        public int Id { get; } = idGenerator.Get();

        public BytesCountersRW BytesCountersRW;

        public void Stop()
        {
            IsStoppingRequested = true;
            var stream = DataStream;
            if (stream == null)
                stream = ConnectResult.Stream;
            if (stream == null) {
                Controller.Logger.warning(this + ": Can not get the stream, failed to stop.");
            } else {
                Controller.Logger.info("Closing stream " + stream + " to stop connection " + this);
                MyStream.CloseWithTimeout(stream);
            }
        }

        public void RedirectTo(AdapterRef redirectedName)
        {
            Redirected = redirectedName;
        }

        protected abstract Task OnConnectionResult(ConnectResult result);

        public async Task<IMyStream> HandleAndGetStream(ConnectResult result)
        {
            if (IsHandled)
                throw new InvalidOperationException("the Connection has been already handled.");
            var handlingAdapter = result.Adapter?.GetAdapter();
            if (handlingAdapter != null)
                System.Threading.Interlocked.Increment(ref handlingAdapter.HandledConnections);
            ConnectResult = result;
            if (result.destEP == null)
                result.destEP = new IPEndPoint(0, 0);
            await OnConnectionResult(result);
            return DataStream;
        }

        public Task<IMyStream> HandleAndGetStream(IAdapter adapter)
        {
            return HandleAndGetStream(new ConnectResult(adapter, ConnectResultEnum.Conneceted));
        }

        public Task HandleFailed(IAdapter adapter)
        {
            return HandleAndGetStream(new ConnectResult(adapter, ConnectResultEnum.Failed));
        }

        public async Task HandleAndPutStream(IAdapter outAdapter, IMyStream stream, Task waitForReadFromStream = null)
        {
            var result = new ConnectResult(outAdapter, ConnectResultEnum.Conneceted) { Stream = stream };
            var thisStream = await HandleAndGetStream(result);
            var copier = new MyStream.TwoWayCopier(stream, thisStream) {
                WhenCanReadFromLeft = waitForReadFromStream,
                Logger = new Logger("->" + outAdapter.Name, InAdapter.GetAdapter().Logger)
            };
            copier.SetCounters(outAdapter.GetAdapter().BytesCountersRW, this.BytesCountersRW);
            EnsureSniffer();
            Sniffer.ListenToCopier(copier.CopierFromRight, copier.CopierFromLeft);
            await copier.Run();
        }

        public virtual string GetInfoStr() => null;

        public void EnsureSniffer()
        {
            if (Sniffer == null)
                Sniffer = new SimpleSniffer();
        }

        public SimpleSniffer Sniffer { get; private set; }

        public string GetSniffingInfo()
        {
            if (Sniffer == null)
                return null;
            var sb = new StringBuilder();
            GetSniffingInfo(sb);
            return sb.ToString();
        }

        public void GetSniffingInfo(StringBuilder sb)
        {
            Sniffer?.GetInfo(sb, TryGetDestWithOriginalName().Host);
        }

        public override string ToString()
        {
            var sb = new StringBuilder(64);
            ToString(sb, ToStringFlags.Default);
            return sb.ToString();
        }

        [Flags]
        public enum ToStringFlags
        {
            None = 0,
            AdditionFields = 1,
            Time = 2,
            Bytes = 4,
            OutAdapter = 8,
            Id = 16,
            OutStream = 32,
            All = AdditionFields | Time | Bytes | OutAdapter | Id | OutStream,
            Default = All
        }

        public void ToString(StringBuilder sb, ToStringFlags flags)
        {
            sb.Append('{');
            if ((flags & ToStringFlags.Id) != 0)
                sb.Append("cxn#").Append(Id).Append(' ');
            sb.Append('\'').Append(InAdapter?.Name).Append('\'');
            var outAdapter = ConnectResult?.Adapter ?? RunningHandler;
            if (outAdapter != null && (flags & ToStringFlags.OutAdapter) != 0)
                sb.Append("->'").Append(outAdapter.Name).Append('\'');
            if ((flags & ToStringFlags.Time) != 0)
                sb.Append(' ').Append("T=").AppendFormat((WebSocket.CurrentTime - CreateTime).ToString("N0"));
            if ((flags & ToStringFlags.Bytes) != 0 && BytesCountersRW.TotalValue.Packets > 0)
                sb.Append(' ').Append(BytesCountersRW.ToString());
            var addition = ((flags & ToStringFlags.AdditionFields) != 0) ? GetInfoStr() : null;
            if (addition != null)
                sb.Append(' ').Append(addition);
            sb.Append(' ').Append("dest=").Append(Dest.Host);
            if (DestOriginalName != null) {
                sb.Append('(').Append(DestOriginalName).Append(')');
            }
            sb.Append(':').Append(Dest.Port);
            if (ConnectResult != null) {
                if (ConnectResult.Result == ConnectResultEnum.Conneceted) {
                    sb.Append(' ').Append("(OK)");
                    if ((flags & ToStringFlags.OutStream) != 0 && ConnectResult.Stream != null) {
                        sb.Append("->").Append(ConnectResult.Stream);
                    }
                } else if (ConnectResult.Result == ConnectResultEnum.Failed) {
                    sb.Append(' ').Append("(FAIL)");
                } else if (ConnectResult.IsRedirected) {
                    sb.Append(' ').Append("(REDIR->'").Append(ConnectResult.Redirected.Adapter?.Name).Append("')");
                }
            }
            if (IsStoppingRequested)
                sb.Append(' ').Append("(STOPPING)");
            if (IsFinished)
                sb.Append(' ').Append("(END)");
            sb.Append('}');
        }

        public delegate Task<IMyStream> ConnectionCallbackDelegate(ConnectResult cr);

        public static InConnection Create(IAdapter inAdapter, AddrPort dest, ConnectionCallbackDelegate connectionCallback, Func<string> getInfoStr = null)
            => new InConnectionImpl(inAdapter, dest, connectionCallback, getInfoStr);

        public static InConnection Create(IAdapter inAdapter, AddrPort dest, IMyStream dataStream, string getInfoStr = null)
            => new InConnectionImpl(inAdapter, dest, async (r) => dataStream, () => getInfoStr);

        private class InConnectionImpl : InConnection
        {
            private readonly ConnectionCallbackDelegate _onConnectionCallback;
            private readonly Func<string> _getInfoStr;

            public InConnectionImpl(IAdapter adapter, AddrPort dest, ConnectionCallbackDelegate onConnectionCallback, Func<string> getInfoStr = null)
                : base(adapter)
            {
                _onConnectionCallback = onConnectionCallback;
                _getInfoStr = getInfoStr;
                this.Dest = dest;
            }

            protected override async Task OnConnectionResult(ConnectResult result)
            {
                DataStream = await _onConnectionCallback(result);
            }

            public override string GetInfoStr()
            {
                return _getInfoStr?.Invoke();
            }
        }
    }

    public abstract class ConnectRequest : InConnection
    {
        public ConnectRequest(IAdapter adapter) : base(adapter)
        {
        }

        protected override Task OnConnectionResult(ConnectResult result)
        {
            return NaiveUtils.CompletedTask;
        }

        public static ConnectRequest Create(IAdapter inAdapter, AddrPort dest, Func<string> getInfoStr = null)
            => new ConnectRequestImpl(inAdapter, dest, getInfoStr);

        public static ConnectRequest Create(IAdapter inAdapter, AddrPort dest, string getInfoStr = null)
            => new ConnectRequestImpl(inAdapter, dest, () => getInfoStr);

        private class ConnectRequestImpl : ConnectRequest
        {
            private readonly Func<string> _getInfoStr;

            public ConnectRequestImpl(IAdapter adapter, AddrPort dest, Func<string> getInfoStr = null) : base(adapter)
            {
                _getInfoStr = getInfoStr;
                this.Dest = dest;
            }

            public override string GetInfoStr()
            {
                return _getInfoStr?.Invoke();
            }
        }
    }

    public struct ConnectResponse
    {
        public ConnectResponse(ConnectResult r, ConnectRequest req)
        {
            this.Result = r;
            this.Request = req;
        }

        public ConnectResult Result { get; }
        public ConnectRequest Request { get; }

        public Task OnConnectionException(Exception e) => Request.Controller.onConnectionException(Request, e);
        public Task OnConnectionEnd() => Request.Controller.onConnectionEnd(Request);

        public MyStream.Copier CreateCopier(Adapter adapter, IMyStream myStream, bool toDest)
        {
            var dest = Result.Stream;
            var ctrFrom = toDest ? adapter.BytesCountersRW : Result.Adapter.GetAdapter().BytesCountersRW;
            var ctrTo = !toDest ? adapter.BytesCountersRW : Result.Adapter.GetAdapter().BytesCountersRW;
            var c = new MyStream.Copier(toDest ? myStream : dest, !toDest ? myStream : dest) {
                CounterR = ctrFrom.R,
                CounterW = ctrTo.W,
                Logger = adapter.Logger
            };
            Request.EnsureSniffer();
            Request.Sniffer.ListenToCopier(toDest ? c : null, !toDest ? null : c);
            return c;
        }

        public AwaitableWrapper WriteToDest(BytesSegment bs)
        {
            OnWriteToDest(bs);
            return Result.Stream.WriteAsyncR(bs);
        }

        public void OnWriteToDest(BytesSegment bs)
        {
            Request.EnsureSniffer();
            Request.Sniffer.ClientData(Request, bs);
            Request.BytesCountersRW.R.Add(bs.Len);
            Result.Adapter.GetAdapter().BytesCountersRW.W.Add(bs.Len);
        }

        public void OnReadFromDest(BytesSegment bs)
        {
            Request.EnsureSniffer();
            Request.Sniffer.ServerData(Request, bs);
            Request.BytesCountersRW.W.Add(bs.Len);
            Result.Adapter.GetAdapter().BytesCountersRW.R.Add(bs.Len);
        }
    }

    public enum ConnectResultEnum
    {
        Conneceted,
        Failed,
    }

    public class ConnectResult
    {
        public ConnectResult(IAdapter adapter, ConnectResultEnum result)
        {
            Adapter = adapter;
            Result = result;
        }

        public ConnectResult(IAdapter adapter, ConnectResultEnum result, IPEndPoint destEP) : this(adapter, result)
        {
            this.destEP = destEP;
        }

        public ConnectResult(IAdapter adapter, ConnectResultEnum result, IMyStream destStream) : this(adapter, result)
        {
            Stream = destStream;
        }

        public ConnectResult(IAdapter adapter, IMyStream destStream) : this(adapter, ConnectResultEnum.Conneceted, destStream)
        {
        }

        public ConnectResult(IAdapter adapter, string failedReason) : this(adapter, ConnectResultEnum.Failed)
        {
            FailedReason = failedReason;
        }

        public IAdapter Adapter { get; }

        public ConnectResultEnum Result;
        public IPEndPoint destEP;
        public string FailedReason;
        public Exception Exception;

        public IMyStream Stream;
        public Task WhenCanRead = NaiveUtils.CompletedTask;

        public bool Ok => Result == ConnectResultEnum.Conneceted;

        public AdapterRef Redirected;
        public bool IsRedirected => Redirected != null;

        public void ThrowIfFailed()
        {
            if (Result != ConnectResultEnum.Conneceted) {
                throw Exception ?? new Exception("connect result: failed: " + FailedReason);
            }
        }

        public static ConnectResult RedirectTo(IAdapter adapter, AdapterRef redirectTo)
        {
            return new ConnectResult(adapter, ConnectResultEnum.Failed) { Redirected = redirectTo };
        }
    }
}
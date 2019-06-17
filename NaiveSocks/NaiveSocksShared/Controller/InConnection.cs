using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class InConnectionDns : InConnection
    {
        protected InConnectionDns(IAdapter creator) : base(creator)
        {
        }

        public override string Type => "dns";

        public DnsRequest DnsRequest { get; private set; }

        public string RequestName => DnsRequest.Name;
        public DnsRequestType RequestType => DnsRequest.Type;

        public DnsResponse Response => this.ConnectResult as DnsResponse;

        public static InConnectionDns Create(IAdapter creator, DnsRequest request) =>
            new InConnectionDns(creator) { Dest = new AddrPort(request.Name, 0), DnsRequest = request };
    }

    public abstract class InConnectionTcp : InConnection
    {
        protected InConnectionTcp(IAdapter creator) : base(creator)
        {
        }

        public override string Type => "tcp";

        public new ConnectResult ConnectResult
        {
            get { return base.ConnectResult as ConnectResult; }
            protected set { base.ConnectResult = value; }
        }

        public virtual IMyStream DataStream { get; set; }

        public Task HandleAndPutStream(IAdapter outAdapter, IMyStream stream, Task waitForReadFromStream = null)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            var result = new ConnectResult(outAdapter, ConnectResultEnum.OK, stream) { WhenCanRead = waitForReadFromStream };
            return SetResult(result);
        }

        public async Task<IMyStream> HandleAndGetStream(ConnectResult result)
        {
            await SetResult(result);
            return DataStream;
        }

        protected override async Task OnConnectionResult(ConnectResultBase result)
        {
            if (result.Ok) {
                var r = (ConnectResult)result;
                if (r.Stream != null) {
                    var handler = r.Adapter;
                    var copier = new MyStream.TwoWayCopier(r.Stream, DataStream) {
                        WhenCanReadFromLeft = r.WhenCanRead,
                        Logger = new Logger("->" + handler.Name, InAdapter.GetAdapter().Logger)
                    };
                    copier.SetCounters(handler.GetAdapter().BytesCountersRW, this.BytesCountersRW);
                    EnsureSniffer();
                    Sniffer.ListenToCopier(copier.CopierFromRight, copier.CopierFromLeft);
                    await copier.Run();
                }
            }
        }

        public Task<IMyStream> HandleAndGetStream(IAdapter adapter)
        {
            return HandleAndGetStream(new ConnectResult(adapter, ConnectResultEnum.OK));
        }

        protected override Task OnFinish()
        {
            var dataStream = DataStream;
            if (dataStream != null)
                MyStream.CloseWithTimeout(dataStream).Forget();
            return base.OnFinish();
        }

        public override void Stop()
        {
            base.Stop();
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

        public delegate Task<IMyStream> ConnectionCallbackDelegate(ConnectResult cr);

        public static InConnectionTcp Create(IAdapter inAdapter, AddrPort dest, ConnectionCallbackDelegate connectionCallback, Func<string> getInfoStr = null)
            => new Impl(inAdapter, dest, connectionCallback, getInfoStr);

        public static InConnectionTcp Create(IAdapter inAdapter, AddrPort dest, IMyStream dataStream, string getInfoStr = null)
            => new Impl(inAdapter, dest, async (r) => dataStream, () => getInfoStr);

        private class Impl : InConnectionTcp
        {
            private readonly ConnectionCallbackDelegate _onConnectionCallback;
            private readonly Func<string> _getInfoStr;

            public Impl(IAdapter adapter, AddrPort dest, ConnectionCallbackDelegate onConnectionCallback, Func<string> getInfoStr = null)
                : base(adapter)
            {
                _onConnectionCallback = onConnectionCallback;
                _getInfoStr = getInfoStr;
                this.Dest = dest;
            }

            protected override async Task OnConnectionResult(ConnectResultBase result)
            {
                if (result is ConnectResult cr) {
                    DataStream = await _onConnectionCallback(cr);
                } else {
                    await _onConnectionCallback(new ConnectResult(null, ConnectResultEnum.Failed));
                }
                await base.OnConnectionResult(result);
            }

            public override string GetInfoStr()
            {
                return _getInfoStr?.Invoke();
            }
        }
    }

    public abstract class InConnection : ConnectArgument
    {
        protected InConnection(IAdapter creator) : base(creator)
        {
            var adap = creator.GetAdapter();
            BytesCountersRW = new BytesCountersRW(adap.BytesCountersRW);
        }

        protected virtual Task OnConnectionResult(ConnectResultBase result)
        {
            return NaiveUtils.CompletedTask;
        }

        public virtual string Type => "cxn";

        public ConnectResultBase ConnectResult { get; protected set; }
        public bool IsHandled { get; private set; }

        public AdapterRef Redirected { get; set; }
        public bool IsRedirected => Redirected != null;

        public IAdapter RunningHandler { get; internal set; }

        public bool IsStoppingRequested { get; private set; }
        public bool IsFinished { get; internal set; }

        static IncrNumberGenerator idGenerator = new IncrNumberGenerator();
        public int Id { get; } = idGenerator.Get();

        public BytesCountersRW BytesCountersRW;

        public virtual void Stop()
        {
            IsStoppingRequested = true;
        }

        public void RedirectTo(AdapterRef redirectedName)
        {
            Redirected = redirectedName;
        }

        public virtual Task SetResult(ConnectResultBase result)
        {
            if (IsHandled) throw new InvalidOperationException("the Connection has been already handled.");
            IsHandled = true;
            var handlingAdapter = result.Adapter?.GetAdapter();
            if (handlingAdapter != null)
                System.Threading.Interlocked.Increment(ref handlingAdapter.HandledConnections);
            this.ConnectResult = result;
            return OnConnectionResult(result);
        }

        public Task Finish()
        {
            if (IsFinished) return NaiveUtils.CompletedTask;
            IsFinished = true;
            return OnFinish();
        }

        protected virtual Task OnFinish()
        {
            if (IsHandled == false) {
                return SetResult(new ConnectResult(null, ConnectResultEnum.Failed) { FailedReason = "Not handled." });
            }
            return NaiveUtils.CompletedTask;
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

        private static bool Has(ToStringFlags flag, ToStringFlags has) => (flag & has) != 0;

        public void ToString(StringBuilder sb, ToStringFlags flags)
        {
            sb.Append('{');
            if (Has(flags, ToStringFlags.Id)) {
                sb.Append(Type).Append("#").Append(Id).Append(' ');
            }
            sb.Append('\'').Append(InAdapter?.Name).Append('\'');
            var outAdapter = ConnectResult?.Adapter ?? RunningHandler;
            if (outAdapter != null && Has(flags, ToStringFlags.OutAdapter))
                sb.Append("->'").Append(outAdapter.Name).Append('\'');
            if (Has(flags, ToStringFlags.Time))
                sb.Append(" T=").AppendFormat((WebSocket.CurrentTime - CreateTime).ToString("N0"));
            if (Has(flags, ToStringFlags.Bytes) && BytesCountersRW.TotalValue.Packets > 0)
                sb.Append(' ').Append(BytesCountersRW.ToString());
            var addition = (Has(flags, ToStringFlags.AdditionFields)) ? GetInfoStr() : null;
            if (addition != null)
                sb.Append(' ').Append(addition);
            sb.Append(" dest=").Append(Dest.Host);
            if (DestOriginalName != null) {
                sb.Append('(').Append(DestOriginalName).Append(')');
            }
            sb.Append(':').Append(Dest.Port);
            if (ConnectResult != null) {
                if (ConnectResult.Result == ConnectResultEnum.OK) {
                    if (ConnectResult is ConnectResult tcp && Has(flags, ToStringFlags.OutStream) && tcp.Stream != null) {
                        sb.Append(" ->").Append(tcp.Stream);
                    }
                } else if (ConnectResult.Result == ConnectResultEnum.Failed) {
                    sb.Append(" (FAIL)");
                } else if (ConnectResult.IsRedirected) {
                    sb.Append(" (REDIR->'").Append(ConnectResult.Redirected.Adapter?.Name).Append("')");
                }
            } else {
                sb.Append(" (...)");
            }
            if (IsStoppingRequested)
                sb.Append(" (STOPPING)");
            if (IsFinished)
                sb.Append(" (END)");
            sb.Append('}');
        }
    }

    public abstract class ConnectRequest : InConnectionTcp
    {
        public ConnectRequest(IAdapter adapter) : base(adapter)
        {
        }

        internal TaskCompletionSource<ConnectResponse> tcs;

        private TaskCompletionSource<VoidType> tcsConnectionFinish;

        protected override Task OnConnectionResult(ConnectResultBase resultBase)
        {
            if (!resultBase.Ok) {
                SetConnectResponse(new ConnectResult(resultBase.Adapter, ConnectResultEnum.Failed) { FailedReason = resultBase.FailedReason });
                return NaiveUtils.CompletedTask;
            }
            var result = (ConnectResult)resultBase;
            if (result.Stream == null) {
                var lo = new LoopbackStream();
                DataStream = lo;
                result.Stream = lo.Another;
                SetConnectResponse(result);
                return NaiveUtils.CompletedTask;
            } else {
                SetConnectResponse(result);
                tcsConnectionFinish = new TaskCompletionSource<VoidType>();
                return tcsConnectionFinish.Task;
            }
        }

        void SetConnectResponse(ConnectResult r)
        {
            tcs.SetResult(new ConnectResponse(r, this));
        }

        protected override Task OnFinish()
        {
            tcsConnectionFinish?.SetResult(0);
            return base.OnFinish();
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
        OK,
        Failed,
    }

    public class ConnectResult : ConnectResultBase
    {
        public ConnectResult(IAdapter adapter, ConnectResultEnum result) : base(adapter, result)
        {
        }

        public ConnectResult(IAdapter adapter, string failedReason) : base(adapter, failedReason)
        {
        }

        public ConnectResult(IAdapter adapter, ConnectResultEnum result, IPEndPoint destEP) : this(adapter, result)
        {
            this.destEP = destEP;
        }

        public ConnectResult(IAdapter adapter, ConnectResultEnum result, IMyStream destStream) : this(adapter, result)
        {
            Stream = destStream;
        }

        public ConnectResult(IAdapter adapter, IMyStream destStream) : this(adapter, ConnectResultEnum.OK, destStream)
        {
        }

        public IPEndPoint destEP;

        /// <summary>
        /// If null, then the handler is requesting a client stream, otherwise the dest stream is provided.
        /// </summary>
        public IMyStream Stream;

        public Task WhenCanRead = NaiveUtils.CompletedTask;
    }

    public class ConnectResultBase
    {
        public ConnectResultBase(IAdapter adapter, ConnectResultEnum result)
        {
            Adapter = adapter;
            Result = result;
        }

        public ConnectResultBase(IAdapter adapter, string failedReason) : this(adapter, ConnectResultEnum.Failed)
        {
            FailedReason = failedReason;
        }

        public IAdapter Adapter { get; }

        public ConnectResultEnum Result;
        public string FailedReason;
        public Exception Exception;

        public bool Ok => Result == ConnectResultEnum.OK;

        public AdapterRef Redirected;
        public bool IsRedirected => Redirected != null;

        public void ThrowIfFailed()
        {
            if (Result != ConnectResultEnum.OK) {
                throw Exception ?? new Exception("connect result: failed: " + FailedReason);
            }
        }

        public static ConnectResult RedirectTo(IAdapter adapter, AdapterRef redirectTo)
        {
            return new ConnectResult(adapter, ConnectResultEnum.Failed) { Redirected = redirectTo };
        }
    }
}

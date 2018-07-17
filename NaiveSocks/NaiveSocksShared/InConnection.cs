using System;
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
            var thisStream = await HandleAndGetStream(outAdapter);
            var copier = new MyStream.TwoWayCopier(stream, thisStream) {
                WhenCanReadFromLeft = waitForReadFromStream,
                Logger = new Logger("->" + outAdapter.Name, InAdapter.GetAdapter().Logger)
            };
            copier.SetCounters(outAdapter.GetAdapter().BytesCountersRW, this.BytesCountersRW);
            await copier.Run();
        }

        public virtual string GetInfoStr() => null;

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
            All = AdditionFields | Time | Bytes | OutAdapter | Id,
            Default = All
        }

        public void ToString(StringBuilder sb, ToStringFlags flags)
        {
            sb.Append('{');
            if ((flags & ToStringFlags.Id) != 0)
                sb.Append("cxn#").Append(Id).Append(' ');
            sb.Append('\'').Append(InAdapter?.Name).Append('\'');
            var outAdapter = ConnectResult?.Adapter;
            if (outAdapter != null && (flags & ToStringFlags.OutAdapter) != 0)
                sb.Append("->'").Append(outAdapter.Name).Append('\'');
            if ((flags & ToStringFlags.Time) != 0)
                sb.Append(' ').Append("T=").AppendFormat((WebSocket.CurrentTime - CreateTime).ToString("N0"));
            if ((flags & ToStringFlags.Bytes) != 0 && BytesCountersRW.TotalValue.Packets > 0)
                sb.Append(' ').Append(BytesCountersRW.ToString());
            var addition = ((flags & ToStringFlags.AdditionFields) != 0) ? GetInfoStr() : null;
            if (addition != null)
                sb.Append(' ').Append(addition);
            sb.Append(' ').Append("dest=").Append(Dest);
            if (ConnectResult != null) {
                if (ConnectResult.Result == ConnectResultEnum.Conneceted)
                    sb.Append(' ').Append("(OK)");
                else if (ConnectResult.Result == ConnectResultEnum.Failed)
                    sb.Append(' ').Append("(FAIL)");
                else if (ConnectResult.IsRedirected)
                    sb.Append(' ').Append("(REDIR->'").Append(ConnectResult.Redirected.Adapter?.Name).Append("')");
            }
            if (IsStoppingRequested)
                sb.Append(' ').Append("(STOPPING)");
            if (IsFinished)
                sb.Append(' ').Append("(END)");
            sb.Append('}');
        }

        public delegate Task<IMyStream> ConnectionCallbackDelegate(ConnectResult cr);

        public static InConnection Create(IAdapter inAdapter, AddrPort dest, ConnectionCallbackDelegate connectionCallback, Func<string> getInfoStr = null)
            => new lambdaHelper(inAdapter, dest, connectionCallback, getInfoStr);

        public static InConnection Create(IAdapter inAdapter, AddrPort dest, IMyStream dataStream, string getInfoStr = null)
            => new lambdaHelper(inAdapter, dest, async (r) => dataStream, () => getInfoStr);

        private class lambdaHelper : InConnection
        {
            private readonly ConnectionCallbackDelegate _onConnectionCallback;
            private readonly Func<string> _getInfoStr;

            public lambdaHelper(IAdapter iAdapter, AddrPort dest, ConnectionCallbackDelegate onConnectionCallback, Func<string> getInfoStr = null)
                : base(iAdapter)
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

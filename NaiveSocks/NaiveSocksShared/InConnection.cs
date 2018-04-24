using System;
using System.Net;
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

        public BytesCountersRW BytesCountersRW;

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
            var copier = new MyStream.TwoWayCopier(stream, thisStream) { WhenCanReadFromLeft = waitForReadFromStream };
            copier.SetCounters(outAdapter.GetAdapter().BytesCountersRW, this.BytesCountersRW);
            await copier.Run();
        }

        public virtual string GetInfoStr() => null;

        public override string ToString()
        {
            return ToString(GetInfoStr());
        }

        public string ToString(string addition)
        {
            return $"{{'{InAdapter?.Name}' T={WebSocket.CurrentTime - CreateTime:N0}{(addition == null ? "" : " ")}{addition} {BytesCountersRW.ToString()} dest={Dest}{((ConnectResult?.Ok == true) ? " (OK)" : "")}}}";
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

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
        }
        //public ConnectionResult Result;

        public virtual IMyStream DataStream { get; set; }

        public ConnectResult ConnectResult { get; private set; }
        public bool CallbackCalled => ConnectResult != null;

        public AdapterRef Redirected { get; set; }
        public bool IsRedirected => Redirected != null;

        public void RedirectTo(AdapterRef redirectedName)
        {
            Redirected = redirectedName;
        }

        protected virtual Task OnConnectionResult(ConnectResult result)
        {
            return NaiveUtils.CompletedTask;
        }

        public Task SetConnectResult(ConnectResults result)
        {
            return SetConnectResult(new ConnectResult(result));
        }

        public Task SetConnectResult(ConnectResults result, IPEndPoint destEP)
        {
            return SetConnectResult(new ConnectResult(result, destEP));
        }

        public Task SetConnectResult(ConnectResult result)
        {
            if (CallbackCalled)
                throw new InvalidOperationException("ConnectResult has been already set.");
            ConnectResult = result;
            if (result.destEP == null)
                result.destEP = new IPEndPoint(0, 0);
            return OnConnectionResult(result);
        }

        public async Task RelayWith(IMyStream stream, Task waitForReadFromStream = null)
        {
            if (!CallbackCalled) {
                await SetConnectResult(ConnectResults.Conneceted, null);
            } else if (!ConnectResult.Ok) {
                throw new InvalidOperationException("ConnectResult has been already set to Failed.");
            }
            await MyStream.Relay(stream, DataStream, waitForReadFromStream);
        }

        public virtual string GetInfoStr() => null;

        public override string ToString()
        {
            return ToString(GetInfoStr());
        }

        public string ToString(string addition)
        {
            return $"{{'{InAdapter?.Name}'{(addition == null ? "" : " ")}{addition} dest={Dest}{(CallbackCalled ? " (connected)" : "")}}}";
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

    public enum ConnectResults
    {
        Conneceted,
        Failed,
    }

    public class ConnectResult
    {
        public ConnectResult(ConnectResults result)
        {
            Result = result;
        }

        public ConnectResult(ConnectResults result, IPEndPoint destEP) : this(result)
        {
            this.destEP = destEP;
        }

        public ConnectResult(ConnectResults result, IMyStream destStream) : this(result)
        {
            Stream = destStream;
        }

        public ConnectResult(IMyStream destStream) : this(ConnectResults.Conneceted, destStream)
        {
        }

        public ConnectResults Result;
        public IPEndPoint destEP;
        public string FailedReason;
        public Exception Exception;

        public IMyStream Stream;
        public Task WhenCanRead = NaiveUtils.CompletedTask;

        public bool Ok => Result == ConnectResults.Conneceted;

        public AdapterRef Redirected;
        public bool IsRedirected => Redirected != null;

        public void ThrowIfFailed()
        {
            if (Result != ConnectResults.Conneceted) {
                throw Exception ?? new Exception("connect result: failed: " + FailedReason);
            }
        }

        public static ConnectResult RedirectTo(AdapterRef redirectTo)
        {
            return new ConnectResult(ConnectResults.Failed) { Redirected = redirectTo };
        }
    }
}

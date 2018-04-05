using Naive.HttpSvr;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NaiveSocks
{
    public interface IConnectionHandler : IAdapter
    {
        Task HandleConnection(InConnection connection);
    }

    public interface IDnsProvider : IAdapter
    {
        Task<IPAddress[]> ResolveName(string name);
    }

    public interface IConnectionProvider
    {
        Task<ConnectResult> Connect(ConnectArgument arg);
    }

    public class ConnectArgument
    {
        public override string ToString() => $"{{Request from {InAdapter} dest={Dest}}}";
        public IAdapter InAdapter { get; }
        public Controller Controller => InAdapter.Controller;

        public AddrPort Dest;
        public string Url { get; set; }
        public CancellationToken CancellationToken { get; set; }

        public ConnectArgument(IAdapter inAdapter)
        {
            InAdapter = inAdapter;
        }
    }

    public abstract class OutAdapter : Adapter, IConnectionHandler
    {
        public abstract Task HandleConnection(InConnection connection);
    }

    public abstract class OutAdapter2 : OutAdapter, IConnectionProvider
    {
        public AdapterRef if_failed { get; set; }

        public abstract Task<ConnectResult> ProtectedConnect(ConnectArgument arg);

        public async Task<ConnectResult> Connect(ConnectArgument arg)
        {
            try {
                var result = await ProtectedConnect(arg);
                if (!result.IsRedirected && !result.Ok && if_failed != null) {
                    Logging.error(ToString() + $": {arg} failed ({result.FailedReason}), redirecting to {if_failed}.");
                    return ConnectResult.RedirectTo(if_failed);
                }
                return result;
            } catch (Exception ex) when (if_failed != null) {
                Logging.exception(ex, Logging.Level.Error, ToString() + $": {arg} failed, redirecting to {if_failed}.");
                return ConnectResult.RedirectTo(if_failed);
            } catch (Exception) {
                throw;
            }
        }

        public override async Task HandleConnection(InConnection connection)
        {
            Exception e = null;
            ConnectResult connectResult = null;
            try {
                connectResult = await Connect(connection);
            } catch (Exception ex) when (if_failed != null) {
                Logging.exception(ex, Logging.Level.Error, $"{this}: {connection} failed ({connectResult.FailedReason}), redirecting to {if_failed}.");
                connection.RedirectTo(if_failed);
                return;
            }
            if (!connectResult.Ok && if_failed != null) {
                Logging.warning($": {connection} failed ({connectResult.FailedReason}), redirecting to {if_failed}.");
                connection.RedirectTo(if_failed);
                return;
            }
            try {
                await connection.SetConnectResult(connectResult);
            } catch (Exception) {
                if (connectResult.Ok)
                    MyStream.CloseWithTimeout(connectResult.Stream);
                throw;
            }
            if (connectResult.Ok) {
                await connection.RelayWith(connectResult.Stream, connectResult.WhenCanRead);
            }
        }

        public static Task<ConnectResult> Connect(Func<InConnection, Task> handleConnection, ConnectArgument arg)
        {
            var tcs = new TaskCompletionSource<ConnectResult>();
            var newinc = InConnection.Create(arg.InAdapter, arg.Dest, async (r) => {
                if (r.Ok) {
                    var stream = new LoopbackStream();
                    r.Stream = stream;
                    tcs.SetResult(r);
                    return stream.Another;
                } else {
                    tcs.SetResult(r);
                    return null;
                }
            });
            newinc.Url = arg.Url;
            NaiveUtils.RunAsyncTask(async () => {
                try {
                    await handleConnection(newinc).CAF();
                } catch (Exception e) {
                    tcs.SetException(e);
                    return;
                }
                if (newinc.IsRedirected && tcs.Task.IsCompleted == false) {
                    tcs.SetResult(ConnectResult.RedirectTo(newinc.Redirected));
                } else {
                    tcs.SetException(new Exception("handleConnection() did nothing."));
                }
            });
            return tcs.Task;
        }
    }

    public class FailAdapter : OutAdapter, IConnectionProvider, IHttpRequestAsyncHandler
    {
        public string reason { get; set; }

        public override async Task HandleConnection(InConnection connection)
        {
            await connection.SetConnectResult(GetConnectResult());
        }

        public Task<ConnectResult> Connect(ConnectArgument arg)
        {
            return Task.FromResult(GetConnectResult());
        }

        private ConnectResult GetConnectResult()
        {
            return new ConnectResult(ConnectResults.Failed) { FailedReason = reason };
        }

        public Task HandleRequestAsync(HttpConnection p)
        {
            p.Handled = true;
            p.ResponseStatusCode = "404 Not Found";
            return AsyncHelper.CompletedTask;
        }
    }
}

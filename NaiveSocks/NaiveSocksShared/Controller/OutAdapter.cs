using Naive.HttpSvr;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NaiveSocks
{
    public interface IConnectionHandler : IAdapter
    {
        Task HandleTcpConnection(InConnectionTcp connection);
    }

    public interface IConnectionHandler2 : IConnectionHandler
    {
        Task HandleConnection(InConnection connection);
    }

    public enum DnsRequestType
    {
        A = 1,
        AAAA = 2,
        AnAAAA = 3
    }

    public class DnsRequest
    {
        public string Name { get; }
        public DnsRequestType Type { get; }

        public DnsRequest(string name, DnsRequestType type)
        {
            Name = name;
            Type = type;
        }
    }

    public class DnsResponse : ConnectResultBase
    {
        public IPAddress[] Addresses;
        public int? TTL;

        public DnsResponse(IAdapter adapter, IPAddress singleAddress) : this(adapter, new IPAddress[] { singleAddress })
        {
        }

        public DnsResponse(IAdapter adapter, IPAddress[] addresses) : base(adapter, ConnectResultEnum.OK)
        {
            Addresses = addresses;
        }

        public static DnsResponse Empty(IAdapter adapter) => new DnsResponse(adapter, new IPAddress[] { });
    }

    public class ConnectArgument
    {
        public override string ToString() => $"{{Request from {InAdapter} dest={Dest}}}";
        public IAdapter InAdapter { get; }
        public Controller Controller => InAdapter.Controller;

        public AddrPort Dest;
        public string DestOriginalName;
        public string Url { get; set; }
        public CancellationToken CancellationToken { get; set; }

        public AddrPort TryGetDestWithOriginalName()
        {
            return new AddrPort() { HostObject = DestOriginalName ?? Dest.HostObject, Port = Dest.Port };
        }

        public string GetDestStringWithOriginalName()
        {
            if (DestOriginalName == null)
                return Dest.ToString();
            else
                return Dest.Host + "(" + DestOriginalName + "):" + Dest.Port;
        }

        public int CreateTime { get; } = WebSocket.CurrentTime;

        public ConnectArgument(IAdapter inAdapter)
        {
            InAdapter = inAdapter;
        }
    }

    public abstract class OutAdapter : Adapter, IConnectionHandler
    {
        public abstract Task HandleTcpConnection(InConnectionTcp connection);
    }

    public abstract class OutAdapter2 : OutAdapter, IConnectionHandler2
    {
        public AdapterRef if_failed { get; set; }

        public abstract Task<ConnectResult> ProtectedConnect(ConnectArgument arg);

        public async Task<ConnectResult> Connect(ConnectArgument arg)
        {
            try {
                var result = await ProtectedConnect(arg);
                if (!result.IsRedirected && !result.Ok && if_failed != null) {
                    Logging.error(ToString() + $": {arg} failed ({result.FailedReason}), redirecting to {if_failed}.");
                    return ConnectResult.RedirectTo(this, if_failed);
                }
                return result;
            } catch (Exception ex) when (if_failed != null) {
                Logging.exception(ex, Logging.Level.Error, ToString() + $": {arg} failed, redirecting to {if_failed}.");
                return ConnectResult.RedirectTo(this, if_failed);
            } catch (Exception) {
                throw;
            }
        }

        public override async Task HandleTcpConnection(InConnectionTcp connection)
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
                if (connectResult.Ok) {
                    await connection.HandleAndPutStream(this, connectResult.Stream, connectResult.WhenCanRead);
                } else {
                    await connection.HandleAndGetStream(connectResult);
                }
            } finally {
                if (connectResult.Ok)
                    MyStream.CloseWithTimeout(connectResult.Stream);
            }
        }

        public static Task<ConnectResult> ConnectWrapper(IConnectionHandler handler, ConnectArgument arg)
        {
            var tcs = new TaskCompletionSource<ConnectResult>();
            var newinc = InConnectionTcp.Create(arg.InAdapter, arg.Dest, async (r) => {
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
            newinc.DestOriginalName = arg.DestOriginalName;
            NaiveUtils.RunAsyncTask(async () => {
                try {
                    await handler.HandleTcpConnection(newinc).CAF();
                } catch (Exception e) {
                    tcs.SetException(e);
                    return;
                }
                if (newinc.IsRedirected && tcs.Task.IsCompleted == false) {
                    tcs.SetResult(ConnectResult.RedirectTo(handler, newinc.Redirected));
                } else {
                    tcs.SetException(new Exception("handleConnection() did nothing."));
                }
            });
            return tcs.Task;
        }

        public virtual Task HandleConnection(InConnection connection)
        {
            if (connection is InConnectionTcp tcp) return this.HandleTcpConnection(tcp);
            else throw new NotSupportedException("cannot handle this type of connection.");
        }
    }

    public class FailAdapter : OutAdapter, IHttpRequestAsyncHandler
    {
        public string reason { get; set; }

        public override async Task HandleTcpConnection(InConnectionTcp connection)
        {
            await connection.HandleAndGetStream(GetConnectResult());
        }

        public Task<ConnectResult> Connect(ConnectArgument arg)
        {
            return Task.FromResult(GetConnectResult());
        }

        private ConnectResult GetConnectResult()
        {
            return new ConnectResult(this, ConnectResultEnum.Failed) { FailedReason = reason };
        }

        public Task HandleRequestAsync(HttpConnection p)
        {
            p.Handled = true;
            p.ResponseStatusCode = "404 Not Found";
            return AsyncHelper.CompletedTask;
        }
    }
}

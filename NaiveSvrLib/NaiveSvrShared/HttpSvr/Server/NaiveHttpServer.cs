using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Net.Security;
using NaiveSocks;

namespace Naive.HttpSvr
{
    public delegate void HttpRequestHandler(HttpConnection p);
    public delegate Task HttpRequestAsyncHandler(HttpConnection p);

    public abstract class NaiveHttpServerAsync : NaiveHttpServer, IHttpRequestAsyncHandler
    {
        public override void HandleRequest(HttpConnection p)
        {
            HandleRequestAsync(p).RunSync();
        }

        public abstract Task HandleRequestAsync(HttpConnection p);
    }

    public abstract class NaiveHttpServer : IHttpRequestHandler
    {
        public List<NaiveHttpListener> Listeners = new List<NaiveHttpListener>();

        public IHttpRequestHandler handler;

        public Logger Logger = new Logger("HttpSvr", Logging.RootLogger);

        private string _mark;
        public string mark
        {
            get {
                return _mark;
            }
            set {
                _mark = value;
                Logger.Stamp = string.IsNullOrEmpty(value) ? "HttpSvr" : $"HttpSvr:{value}";
            }
        }

        [Obsolete("Use AddListener method")]
        public NaiveHttpServer(int port) : this()
        {
            mark = port.ToString();
            AddListener(port);
        }

        public NaiveHttpServer()
        {
            handler = this;
        }

        public NaiveHttpListener AddListener(int port)
        {
            return AddListener(new IPEndPoint(IPAddress.Any, port));
        }

        public NaiveHttpListener AddListener(IPAddress address, int port)
        {
            return AddListener(new IPEndPoint(address, port));
        }

        public NaiveHttpListener AddListener(IPEndPoint ipEndPoint)
        {
            return AddListener(new TcpListener(ipEndPoint));
        }

        public NaiveHttpListener AddListener(string stringAddress, int port)
        {
            return AddListener(IPAddress.Parse(stringAddress), port);
        }

        public NaiveHttpListener AddListener(TcpListener listener)
        {
            var fsl = new NaiveHttpListener(this, listener);
            Listeners.Add(fsl);
            return fsl;
        }

        /// <summary>
        /// (async) Start listeners.
        /// </summary>
        public Task Run()
        {
            if (Listeners.Count == 0) {
                Logger.warning($"Run(): No listner.");
            }
            List<Task> tasks = new List<Task>(Listeners.Count);
            foreach (var listener in Listeners) {
                tasks.Add(RunOne(listener));
            }
            return Task.WhenAll(tasks);
        }

        public Task RunOne(NaiveHttpListener listener) => listener.Listen();

        /// <summary>
        /// Is any listener running.
        /// </summary>
        public bool IsRunning
        {
            get {
                foreach (var listener in Listeners) {
                    if (listener.IsRunning) {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Stop listeners.
        /// </summary>
        public virtual void Stop()
        {
            foreach (var listener in Listeners) {
                listener.Stop();
            }
        }

        /// <summary>
        /// write 500 error page
        /// </summary>
        public virtual async Task errorPage(HttpConnection p, Exception e)
        {
            p.ResponseStatusCode = "500 Internal Server Error";
            await p.writeAsync("<h1>500 Internal Server Error</h1>"
                + $"<h2>{HttpUtil.HtmlEncode(e.GetType().FullName)}</h2>"
                + $"<pre>{HttpUtil.HtmlEncode(e.Message)}</pre>");
        }
        /// <summary>
        /// Create <see cref="HttpConnection"/>
        /// </summary>
        /// <param name="client">Accepted tcp client</param>
        /// <returns></returns>
        protected virtual HttpConnection CreateHttpConnectionObject(TcpClient client, Stream stream, EPPair ePPair)
        {
            return new HttpConnection(stream, ePPair, this) { socket = client.Client };
        }
        /// <summary>
        /// Implements <see cref="IHttpRequestHandler"/>
        /// </summary>
        public abstract void HandleRequest(HttpConnection p);

        /// <summary>
        /// The same as <see cref="log(string, Logging.Level)"/>
        /// </summary>
        public void info(string text) => log(text, Logging.Level.Info);

        public virtual void log(string text, Logging.Level level)
        {
#if !DEBUG
            if (level < Logging.Level.Warning) {
                return;
            }
#endif
            Logger.log(text, level);
        }

        public virtual void logException(Exception ex, Logging.Level level, string text)
        {
            Logger.exception(ex, level, text);
        }

        public virtual void OnHttpConnectionException(Exception ex, HttpConnection p)
        {
            if (p.disconnecting)
                return;
            Logger.exception(ex, Logging.Level.Error, $"({p.myStream}) httpConnection processing");
        }

        public class NaiveHttpListener
        {
            public TcpListener tcpListener { get; }
            public NaiveHttpServer server { get; }
            public IPEndPoint localEP { get; }
            public bool IsRunning { get; private set; } = false;
            public bool LogInfo { get; set; } = false;
            public Logger Logger => server.Logger;

            public NaiveHttpListener(NaiveHttpServer server, TcpListener tcpListener)
            {
                this.server = server;
                this.tcpListener = tcpListener;
                tcpListener.Server.NoDelay = true;
                this.localEP = tcpListener.LocalEndpoint as IPEndPoint;
            }

            public async Task Listen()
            {
                try {
                    await _listen();
                } catch (Exception e) {
                    if (IsRunning) {
                        server.logException(e, Logging.Level.Error, $"({localEP}) Run():");
                    }
                } finally {
                    IsRunning = false;
                    if (LogInfo)
                        server.log($"({localEP}) listening thread exiting", Logging.Level.Warning);
                }
            }

            private async Task _listen()
            {
                IsRunning = true;
                try {
                    tcpListener.Start();
                } catch (Exception e) {
                    Logger.error($"starting listening on {localEP}: {e.Message}");
                    return;
                }
                if (LogInfo)
                    Logger.info($"listening on {localEP}");
                while (true) {
                    try {
                        var client = await tcpListener.AcceptTcpClientAsync();
                        NaiveUtils.ConfigureSocket(client.Client);
                        Task.Run(() => server.HandleAcceptedTcp(client)).Forget();
                    } catch (Exception e) {
                        if (IsRunning) {
                            server.logException(e, Logging.Level.Error, $"({localEP}) accepting connection:");
                            await Task.Delay(300);
                        } else {
                            return;
                        }
                    }
                }
            }

            public void Stop()
            {
                try {
                    IsRunning = false;
                    tcpListener.Stop();
                } catch (Exception ex) {
                    Logger.exception(ex, Logging.Level.Warning, $"stopping TcpListener ({localEP})");
                }
            }
        }

        protected virtual async Task HandleAcceptedTcp(TcpClient tcpClient)
        {
            EPPair epPair = new EPPair();
            HttpConnection connection = null;
            try {
                epPair = EPPair.FromSocket(tcpClient.Client);
                var myStream = MyStream.FromSocket(tcpClient.Client);
                var stream = myStream.ToStream();
                connection = this.CreateHttpConnectionObject(tcpClient, stream, epPair);
                if (connection == null) {
                    try {
                        tcpClient.Client.Close();
                    } catch (Exception) { }
                    return;
                }
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Error, $"({epPair}) httpConnection creating");
                return;
            }
            try {
                await connection.Process();
            } catch (Exception e) {
                try {
                    this.OnHttpConnectionException(e, connection);
                } catch (Exception e2) {
                    Logger.exception(e2, Logging.Level.Error, "In OnHttpConnectionExceptionExit");
                }
            }
        }
    }

    public class DisconnectedException : Exception
    {
        public DisconnectedException() { }
        public DisconnectedException(string msg) : base(msg) { }
    }

    public interface IHttpRequestAsyncHandler
    {
        Task HandleRequestAsync(HttpConnection p);
    }

    public interface IHttpRequestHandler : IHttpRequestHandler<HttpConnection>
    {
    }

    public interface IHttpRequestHandler<T> where T : HttpConnection
    {
        void HandleRequest(T p);
    }
}

using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks
{
    public abstract class WebBaseAdapter : OutAdapter, IHttpRequestAsyncHandler, ICanReload
    {
        HttpSvrImpl _httpSvr;
        public virtual NaiveHttpServerAsync HttpSvr
        {
            get {
                if (_httpSvr == null) {
                    _httpSvr = new HttpSvrImpl(this);
                    _httpSvr.Logger = Logger;
                }
                return _httpSvr;
            }
        }

        public override async Task HandleTcpConnection(InConnectionTcp connection)
        {
            var stream = await connection.HandleAndGetStream(this);
            HttpConnection httpConnection = CreateHttpConnectionFromMyStream(stream, HttpSvr);
            await httpConnection.Process();
        }

        public Task HandleRequestAsync(HttpConnection p)
        {
            if (_newInstance != null)
                return _newInstance.HandleRequestAsync(p);
            return HandleRequestAsyncImpl(p);
        }

        public abstract Task HandleRequestAsyncImpl(HttpConnection p);

        WebBaseAdapter _newInstance, _oldInstance;

        public static HttpConnection CreateHttpConnectionFromMyStream(IMyStream myStream, NaiveHttpServer httpSvr)
        {
            EPPair epPair;
            if (myStream is SocketStream ss) {
                epPair = ss.EPPair;
            } else {
                // use fake eppair
                epPair = new EPPair(new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2));
            }
            return new HttpConnection(myStream.ToStream(), epPair, httpSvr);
        }

        public virtual bool Reloading(object oldInstance)
        {
            if (oldInstance is WebBaseAdapter wb) {
                _oldInstance = wb;
            }
            return false;
        }

        protected override void OnStart()
        {
            base.OnStart();
            if (_oldInstance != null) {
                _oldInstance._newInstance = this;
                _oldInstance = null;
            }
        }

        class HttpSvrImpl : NaiveHttpServerAsync
        {
            public HttpSvrImpl(WebBaseAdapter adapter)
            {
                Adapter = adapter;
            }

            public WebBaseAdapter Adapter { get; }

            public override Task HandleRequestAsync(HttpConnection p)
            {
                return Adapter.HandleRequestAsync(p);
            }
        }
    }
}

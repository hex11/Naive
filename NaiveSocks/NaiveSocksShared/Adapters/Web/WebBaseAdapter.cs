using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks
{
    public abstract class WebBaseAdapter : OutAdapter, IHttpRequestAsyncHandler
    {
        HttpSvrImpl _httpSvr;
        public virtual NaiveHttpServerAsync HttpSvr
        {
            get {
                return _httpSvr ?? (_httpSvr = new HttpSvrImpl(this));
            }
        }

        public override async Task HandleConnection(InConnection connection)
        {
            await connection.SetConnectResult(ConnectResults.Conneceted);
            HttpConnection httpConnection = CreateHttpConnectionFromMyStream(connection.DataStream, HttpSvr);
            await httpConnection.Process();
        }

        public abstract Task HandleRequestAsync(HttpConnection p);

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

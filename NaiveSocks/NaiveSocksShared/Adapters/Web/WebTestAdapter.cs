using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class WebTestAdapter : WebBaseAdapter
    {
        public override Task HandleRequestAsyncImpl(HttpConnection p)
        {
            p.setContentTypeTextPlain();
            return p.EndResponseAsync(p.RawRequest);
        }
    }
}

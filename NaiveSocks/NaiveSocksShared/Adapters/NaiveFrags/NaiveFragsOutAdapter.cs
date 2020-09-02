using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks
{
    /// <summary>
    /// "NaiveFrags" Client
    /// </summary>
    class NaiveFragsOutAdapter : OutAdapter2
    {
        public AddrPort server { get; set; }

        public string key { get; set; }

        public string template_request { get; set; } = "GET / HTTP/1.1\r\n\r\n";

        public string template_response { get; set; } = "200 OK HTTP/1.1\r\n\r\n";

        public override Task<ConnectResult> ProtectedConnect(ConnectArgument arg)
        {
            throw new NotImplementedException();
        }
    }
}

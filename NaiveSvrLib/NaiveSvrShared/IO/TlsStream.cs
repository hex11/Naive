using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class TlsStream : MyStream.StreamWrapperBase
    {
        public IMyStream RealBaseStream { get; }
        public SslStream SslStream { get; }

        public override string ToString() => $"{{Tls on {RealBaseStream}}}";

        public TlsStream(IMyStream baseStream) : base(null)
        {
            RealBaseStream = baseStream;
            BaseStream = SslStream = new SslStream(baseStream.ToStream(), false);
        }

        public Task AuthAsClient(string targetHost)
        {
            return AuthAsClient(targetHost, NaiveUtils.TlsProtocols);
        }

        public Task AuthAsClient(string targetHost, SslProtocols protocols)
        {
            return SslStream.AuthenticateAsClientAsync(targetHost, null, protocols, false);
        }
    }
}

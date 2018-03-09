﻿using System;
using System.Collections.Generic;
using System.IO;
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

        public override Stream BaseStream => SslStream;

        public override string ToString() => $"{{Tls on {RealBaseStream}}}";

        public TlsStream(IMyStream baseStream)
        {
            RealBaseStream = baseStream;
            SslStream = new SslStream(baseStream.ToStream(), false);
        }

        public Task AuthAsClient(string targetHost)
        {
            return AuthAsClient(targetHost, NaiveUtils.TlsProtocols);
        }

        public Task AuthAsClient(string targetHost, SslProtocols protocols)
        {
            return SslStream.AuthenticateAsClientAsync(targetHost, null, protocols, false);
        }

        public Task AuthAsServer(System.Security.Cryptography.X509Certificates.X509Certificate certificate)
        {
            return SslStream.AuthenticateAsServerAsync(certificate);
        }
    }
}

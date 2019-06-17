using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Naive.HttpSvr;
using Nett;

namespace NaiveSocks
{
    public class SocksInAdapter : InAdapterWithListener
    {
        public bool fastreply { get; set; } = false;

        public User[] users { get; set; }

        public AdapterRef rdns { get; set; }

        [ConfType]
        public struct User
        {
            public string name { get; set; }
            public string passwd { get; set; }
            public AdapterRef @out { get; set; }
        }

        bool _allowNoAuth;
        AdapterRef _noAuthOut;

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            _noAuthOut = @out;
            if (users == null) {
                _allowNoAuth = true;
            } else {
                foreach (var x in users) {
                    if (x.name.IsNullOrEmpty() && x.passwd.IsNullOrEmpty()) {
                        _allowNoAuth = true;
                        _noAuthOut = x.@out ?? @out;
                        break;
                    }
                }
            }
        }

        public override void OnNewConnection(TcpClient client)
        {
            new SocksInConnection(client, this).Start();
        }

        private class SocksInConnection : InConnectionTcp
        {
            private Socks5Server socks5svr;
            private readonly EPPair _eppair;
            private readonly SocksInAdapter _adapter;
            private readonly IMyStream _stream;
            private AdapterRef outRef;

            public SocksInConnection(TcpClient tcp, SocksInAdapter adapter) : base(adapter)
            {
                _eppair = EPPair.FromSocket(tcp.Client);
                _adapter = adapter;
                _stream = adapter.GetMyStreamFromSocket(tcp.Client);
                socks5svr = new Socks5Server(_stream);

                Socks5Server.Methods methods = Socks5Server.Methods.None;
                if (adapter._allowNoAuth)
                    methods |= Socks5Server.Methods.NoAuth;
                if (adapter.users != null)
                    methods |= Socks5Server.Methods.UsernamePassword;
                socks5svr.AcceptMethods = methods;

                outRef = adapter._noAuthOut;

                socks5svr.Auth = Auth;
            }

            private bool Auth(Socks5Server s)
            {
                if (s.Username.IsNullOrEmpty() && s.Password.IsNullOrEmpty() && _adapter._allowNoAuth)
                    return true;
                foreach (var x in _adapter.users) {
                    if ((x.name ?? "") == s.Username && (x.passwd ?? "") == s.Password) {
                        outRef = x.@out ?? _adapter.@out;
                        return true;
                    }
                }
                _adapter.Logger.warning("Auth failed: " + _eppair.RemoteEP);
                return false;
            }

            public async void Start()
            {
                try {
                    if (!await socks5svr.ProcessAsync())
                        return;
                    this.Dest.Host = socks5svr.TargetAddr;
                    this.Dest.Port = socks5svr.TargetPort;
                    this.DataStream = _stream;
                    if (_adapter.rdns?.Adapter is DnsInAdapter dnsIn) {
                        try {
                            dnsIn.HandleRdns(this);
                        } catch (Exception e) {
                            _adapter.Logger.exception(e, Logging.Level.Error, "rdns handling");
                        }
                    }
                    if (_adapter.fastreply)
                        await OnConnectionResult(new ConnectResult(null, ConnectResultEnum.OK)).CAF();

                    await _adapter.Controller.HandleInConnection(this, outRef);
                } catch (Exception e) {
                    _adapter.Logger.exception(e, Logging.Level.Warning, "listener");
                } finally {
                    MyStream.CloseWithTimeout(_stream).Forget();
                }
            }

            protected override async Task OnConnectionResult(ConnectResultBase result)
            {
                if (socks5svr != null) {
                    var tmp = socks5svr;
                    socks5svr = null;
                    await tmp.WriteConnectionResult(new IPEndPoint(0, Dest.Port), (result.Ok) ? Socks5Server.Rep.succeeded : Socks5Server.Rep.Connection_refused);
                }
                await base.OnConnectionResult(result);
            }

            public override string GetInfoStr() => _eppair.ToString();
        }
    }
}

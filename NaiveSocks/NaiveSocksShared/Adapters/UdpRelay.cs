using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NaiveSocks
{
    public class UdpRelay : InAdapterWithListenField
    {
        public IPEndPoint redirect_dns { get; set; }
        public bool verbose { get; set; }

        SocketStream listenUdp;

        // map client endpoint to connection
        Dictionary<IPEndPoint, Connection> map = new Dictionary<IPEndPoint, Connection>();

        LinkedList<Connection> list = new LinkedList<Connection>();

        const int MaxConnections = 512;
        const int UdpBufferSize = 64 * 1024;

        int sentPackets, receivedPackets;

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("sent", sentPackets);
            ctx.AddField("recv", receivedPackets);
        }

        protected override void OnStart()
        {
            base.OnStart();
            Listen();
        }

        protected override void OnStop()
        {
            base.OnStop();
            var tmp = listenUdp;
            listenUdp = null;
            tmp.Close();
        }

        static readonly IPEndPoint anyEp = new IPEndPoint(0, 0);

        async void Listen()
        {
            var bea = new BeginEndAwaiter();
            byte[] buffer = null;
            listenUdp = CreateUdpSocket();
            try {
                listenUdp.Socket.Bind(listen);
                buffer = BufferPool.GlobalGet(UdpBufferSize);
                while (true) {
                    var socket = listenUdp;
                    var r = await socket.ReadFromAsyncR(new BytesSegment(buffer, 0, UdpBufferSize), anyEp);
                    var read = r.Read;
                    var clientEp = r.From;
                    var cur = 0;
                    var dest = ParseHeader(buffer, ref cur);
                    if (cur > read) dest = null;
                    bool redns = redirect_dns != null && dest.Port == 53;
                    if (verbose)
                        Logger.debugForce((read - cur) + " B from " + clientEp + " dest " + (dest ?? (object)"(null)") + (redns ? " (dns)" : null));
                    if (redns) dest = redirect_dns;

                    bool v;
                    Connection cxn;
                    lock (list)
                        v = map.TryGetValue(clientEp, out cxn);
                    if (v == false) {
                        cxn = new Connection(this, clientEp);
                        lock (list) {
                            map.Add(cxn.clientEP, cxn);
                            list.AddFirst(cxn.node);
                            if (list.Count > MaxConnections) {
                                list.Last.Value.RemoveAndClose();
                            }
                        }
                        await cxn.Send(new BytesSegment(buffer, cur, read - cur), dest);
                        cxn.StartReceive();
                    } else {
                        ActiveConnection(cxn);
                        try {
                            await cxn.Send(new BytesSegment(buffer, cur, read - cur), dest);
                            Interlocked.Increment(ref sentPackets);
                        } catch (Exception e) {
                            Logger.warning("sending from " + clientEp + " dest " + dest + ": " + e.Message);
                        }
                    }
                }
            } catch (Exception e) {
                if (listenUdp != null)
                    Logger.exception(e, Naive.HttpSvr.Logging.Level.Error, "listener");
            } finally {
                listenUdp?.Close();
                //if (buffer != null) BufferPool.GlobalPut(buffer);
            }
        }

        private IPEndPoint ParseHeader(BytesSegment buffer, ref int cur)
        {
            cur += 1; // skip rsv
            var frag = buffer[cur++] << 8 | buffer[cur++];
            if (frag != 0) { // TODO: hope it doesn't use fragments :(
                Logger.warning("frag=" + frag);
            }
            var atyp = (Socks5Server.AddrType)buffer[cur++];
            switch (atyp) {
                case Socks5Server.AddrType.IPv4Address:
                    uint ip = (uint)(buffer[cur++] | buffer[cur++] << 8 | buffer[cur++] << 16 | buffer[cur++] << 24);
                    int port = buffer[cur++] << 8 | buffer[cur++];
                    return new IPEndPoint(ip, port);
                case Socks5Server.AddrType.IPv6Address:
                // TODO
                default:
                    return null;
            }
        }

        const int headerLen = 10;

        private void BuildHeader(BytesSegment bs, ref int cur, IPEndPoint addr)
        {
            bs[cur++] = 0; // rsv
            bs[cur++] = 0; bs[cur++] = 0; // frag
            if (addr.AddressFamily == AddressFamily.InterNetwork) {
                bs[cur++] = (byte)Socks5Server.AddrType.IPv4Address;
                long ip = addr.Address.Address;
                int port = addr.Port;
                for (int i = 0; i < 4; i++) {
                    bs[cur++] = (byte)(ip >> (i * 8));
                }
                bs[cur++] = (byte)(port >> 8);
                bs[cur++] = (byte)port;
            } else {
                throw new NotSupportedException("address family not supported.");
            }
        }

        static SocketStream CreateUdpSocket() => new SocketStream1(new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp));

        // move to the first
        void ActiveConnection(Connection c)
        {
            if (list.First == c.node) return;
            lock (list) {
                list.Remove(c.node);
                list.AddFirst(c.node);
            }
        }

        // won't throw if already removed
        void RemoveConnection(Connection c)
        {
            lock (list) {
                if (map.Remove(c.clientEP))
                    list.Remove(c.node);
            }
        }

        class Connection
        {
            public UdpRelay relay { get; }
            public IPEndPoint clientEP { get; }
            public SocketStream remoteUdp;

            public LinkedListNode<Connection> node;

            int recv;

            public Connection(UdpRelay relay, IPEndPoint clientEP)
            {
                this.node = new LinkedListNode<Connection>(this);
                this.relay = relay;
                this.clientEP = clientEP;
                this.remoteUdp = CreateUdpSocket();
            }

            public AwaitableWrapper Send(BytesSegment bs, IPEndPoint destEP)
            {
                Interlocked.Increment(ref relay.sentPackets);
                return remoteUdp?.WriteToAsyncR(bs, destEP) ?? AwaitableWrapper.GetCompleted();
            }

            public async void StartReceive()
            {
                // Timed out if no any response from the dest in 60 seconds.
                AsyncHelper.SetTimeout(this, 60 * 1000, (x) => {
                    if (x.recv == 0) {
                        if (relay.verbose)
                            relay.Logger.debugForce("timed out for client " + clientEP);
                        x.RemoveAndClose();
                    }
                });

                var buffer = new byte[UdpBufferSize];

                var bea = new BeginEndAwaiter();
                try {
                    while (true) {
                        var r = await remoteUdp.ReadFromAsyncR(new BytesSegment(buffer, headerLen, UdpBufferSize - headerLen), anyEp);
                        var destEP = r.From;
                        var cur = 0;
                        relay.BuildHeader(buffer, ref cur, destEP);
                        recv++;
                        Interlocked.Increment(ref relay.receivedPackets);
                        if (relay.verbose)
                            relay.Logger.debugForce(r.Read + " B from dest " + destEP + " to " + clientEP);
                        await relay.listenUdp.WriteToAsyncR(new BytesSegment(buffer, 0, headerLen + r.Read), clientEP);
                        relay.ActiveConnection(this);
                    }
                } catch (Exception e) {
                    if (remoteUdp != null)
                        relay.Logger.exception(e, Logging.Level.Error, "receiving packet for client " + clientEP);
                } finally {
                    RemoveAndClose();
                    //if (buffer != null) BufferPool.GlobalPut(buffer);
                }
            }

            public void RemoveAndClose()
            {
                var tmp = Interlocked.Exchange(ref remoteUdp, null);
                if (tmp == null) return;
                if (relay.verbose)
                    relay.Logger.debugForce("close connection " + clientEP);
                relay.RemoveConnection(this);
                tmp?.Close();
            }
        }
    }
}

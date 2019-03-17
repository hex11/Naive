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

        // map client endpoints to connections
        Dictionary<IPEndPoint, Connection> map = new Dictionary<IPEndPoint, Connection>();
        LinkedList<Connection> list = new LinkedList<Connection>();

        const int MaxConnections = 128;
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

        async void Listen()
        {
            var bea = new BeginEndAwaiter();
            byte[] buffer = null;
            var anyEp = new IPEndPoint(0, 0);
            listenUdp = CreateUdpSocket();
            try {
                listenUdp.Socket.Bind(listen);
                buffer = BufferPool.GlobalGet(UdpBufferSize);
                while (true) {
                    var socket = listenUdp;
                    var r = await socket.ReadFromAsyncR(new BytesSegment(buffer, 0, UdpBufferSize), anyEp);
                    var read = r.Read;
                    var clientEp = r.From;
                    Interlocked.Increment(ref receivedPackets);
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
                    if (v && dest.Equals(cxn.destEP) == false) {
                        if (verbose)
                            Logger.debugForce("dest changed (" + cxn.destEP + " -> " + dest + ") from " + clientEp);
                        v = false;
                        cxn.RemoveAndClose();
                    }
                    if (v == false) {
                        cxn = new Connection(this, clientEp, dest);
                        lock (list) {
                            map.Add(clientEp, cxn);
                            list.AddFirst(cxn.node);
                            if (list.Count > MaxConnections) {
                                list.Last.Value.RemoveAndClose();
                            }
                        }
                        cxn.StartReceive(new BytesSegment(buffer, 0, read), cur);
                    } else {
                        ActiveConnection(cxn);
                        try {
                            await cxn.Send(new BytesSegment(buffer, cur, read - cur));
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
            cur += 1 + 2; // skip rsv and flag
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
            public UdpRelay relay;
            public IPEndPoint clientEP, destEP;
            public SocketStream remoteUdp;

            public LinkedListNode<Connection> node;

            int recv;

            public Connection(UdpRelay relay, IPEndPoint clientEP, IPEndPoint destEP)
            {
                this.node = new LinkedListNode<Connection>(this);
                this.relay = relay;
                this.clientEP = clientEP;
                this.destEP = destEP;
                this.remoteUdp = CreateUdpSocket();
            }

            public AwaitableWrapper Send(BytesSegment bs)
            {
                return remoteUdp.WriteToAsyncR(bs, destEP);
            }

            public async void StartReceive(BytesSegment firstpacket, int headerLen)
            {
                // Note that firstpacket should not be used after any awaiting.

                AsyncHelper.SetTimeout(this, 15 * 1000, (x) => {
                    if (x.recv == 0) {
                        if (relay.verbose)
                            relay.Logger.debugForce("timed out from dest " + destEP + " to " + clientEP);
                        x.RemoveAndClose();
                    }
                });

                var bea = new BeginEndAwaiter();
                byte[] buffer = null;
                try {
                    int len = firstpacket.Len;
                    buffer = BufferPool.GlobalGet(UdpBufferSize);
                    firstpacket.CopyTo(buffer);

                    await Send(new BytesSegment(buffer, headerLen, len - headerLen));
                    Interlocked.Increment(ref relay.sentPackets);

                    while (true) {
                        var r = await remoteUdp.ReadFromAsyncR(new BytesSegment(buffer, headerLen, UdpBufferSize - headerLen), destEP);
                        len = headerLen + r.Read;
                        recv++;
                        Interlocked.Increment(ref relay.receivedPackets);
                        if (relay.verbose)
                            relay.Logger.debugForce(r.Read + " B from dest " + destEP + " to " + clientEP);
                        await relay.listenUdp.WriteToAsyncR(new BytesSegment(buffer, 0, len), clientEP);
                        Interlocked.Increment(ref relay.sentPackets);
                        relay.ActiveConnection(this);
                    }
                } catch (Exception e) {
                    if (remoteUdp != null)
                        relay.Logger.exception(e, Logging.Level.Error, "receiving packet from dest " + destEP + " to " + clientEP);
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
                    relay.Logger.debugForce("close connection " + clientEP + " <-> " + destEP);
                relay.RemoveConnection(this);
                tmp?.Close();
            }
        }
    }
}

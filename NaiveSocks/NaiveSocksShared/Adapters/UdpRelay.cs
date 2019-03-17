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

        Socket listenUdp;

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
                listenUdp.Bind(listen);
                buffer = BufferPool.GlobalGet(UdpBufferSize);
                while (true) {
                    EndPoint tmpEp = anyEp;
                    var socket = listenUdp;
                    socket.BeginReceiveFrom(buffer, 0, UdpBufferSize, SocketFlags.None, ref tmpEp, BeginEndAwaiter.Callback, bea);
                    var read = socket.EndReceiveFrom(await bea, ref tmpEp);
                    Interlocked.Increment(ref receivedPackets);
                    var cur = 0;
                    var dest = ParseHeader(buffer, ref cur);
                    if (cur > read) dest = null;
                    var clientEp = (IPEndPoint)tmpEp;
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
                        cxn.StartReceive(new BytesSegment(buffer, 0, cur));
                    } else {
                        ActiveConnection(cxn);
                    }
                    try {
                        cxn.Send(new BytesSegment(buffer, cur, read - cur));
                        Interlocked.Increment(ref sentPackets);
                    } catch (Exception e) {
                        Logger.warning("sending from " + clientEp + " dest " + dest + ": " + e.Message);
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

        static Socket CreateUdpSocket() => new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

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
            public Socket remoteUdp;

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

            public void Send(BytesSegment bs)
            {
                remoteUdp.SendTo(bs.Bytes, bs.Offset, bs.Len, SocketFlags.None, destEP);
            }

            public async void StartReceive(BytesSegment header)
            {
                // Note that header should not be used after any awaiting.

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
                    buffer = BufferPool.GlobalGet(UdpBufferSize);
                    header.CopyTo(buffer);
                    while (true) {
                        EndPoint tmpEp = destEP;
                        var socket = remoteUdp;
                        socket.BeginReceiveFrom(buffer, header.Len, UdpBufferSize - header.Len, SocketFlags.None, ref tmpEp, BeginEndAwaiter.Callback, bea);
                        var read = socket.EndReceiveFrom(await bea, ref tmpEp);
                        recv++;
                        Interlocked.Increment(ref relay.receivedPackets);
                        if (relay.verbose)
                            relay.Logger.debugForce(read + " B from dest " + destEP + " to " + clientEP);
                        relay.listenUdp.SendTo(buffer, 0, header.Len + read, SocketFlags.None, clientEP);
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

﻿using Naive.HttpSvr;
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
        const int UdpBufferSize = 64 * 1024;

        public IPEndPoint redirect_dns { get; set; }
        public bool verbose { get; set; }
        public int max_maps { get; set; } = 256;
        public int timeout { get; set; } = 60;

        SocketStream listenUdp;

        // map client endpoint to connection
        Dictionary<IPEndPoint, Connection> map = new Dictionary<IPEndPoint, Connection>();

        LinkedList<Connection> list = new LinkedList<Connection>();

        int sentPackets, receivedPackets;

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("sent", sentPackets);
            ctx.AddField("recv", receivedPackets);
            ctx.AddField("maps", map.Count);
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

            lock (list) {
                while (list.Last != null) {
                    list.Last.Value.RemoveAndClose();
                }
            }
        }

        static readonly IPEndPoint anyEp = new IPEndPoint(0, 0);

        async void Listen()
        {
            var bea = new BeginEndAwaiter();
            listenUdp = CreateUdpSocket();
            try {
                listenUdp.Socket.Bind(listen);
                while (true) {
                    var socket = listenUdp;
                    var r = await socket.ReadFromAsyncR(UdpBufferSize, anyEp);
                    var read = r.Read;
                    var clientEp = r.From;
                    var buffer = r.Buffer.Bytes;
                    var cur = r.Buffer.Offset;
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
                        cxn = NewConnection(clientEp);
                        await cxn.Send(new BytesSegment(buffer, cur, read - cur), dest);
                        cxn.StartReceive();
                    } else {
                        ActiveConnection(cxn);
                        try {
                            await cxn.Send(new BytesSegment(buffer, cur, read - cur), dest);
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

        private Connection NewConnection(IPEndPoint clientEp)
        {
            var cxn = new Connection(this, clientEp, CreateUdpSocket());
            lock (list) {
                map.Add(cxn.clientEP, cxn);
                list.AddFirst(cxn.node);
                if (list.Count > max_maps) {
                    list.Last.Value.RemoveAndClose();
                }
                CheckTask();
            }
            return cxn;
        }

        bool taskRunning;

        private void CheckTask()
        {
            bool shouldRun = list.Count > 0 && timeout > 0;
            if (taskRunning == shouldRun) return;
            if (shouldRun) {
                taskRunning = true;
                WebSocket.AddManagementTask(() => {
                    lock (list) {
                        var node = list.Last;
                        if (node == null) {
                            if (verbose) Logger.debugForce("management task stopped.");
                            taskRunning = false;
                            return true;
                        }
                        do {
                            var cxn = node.Value;
                            var prev = node.Previous;
                            if (WebSocket.CurrentTimeRough - cxn.lastActive > timeout) {
                                if (verbose) Logger.debugForce("timed out: " + cxn.clientEP);
                                cxn.RemoveAndClose();
                            } else {
                                // No need to check the more active connections.
                                break;
                            }
                            node = prev;
                        } while (node != null);
                        return false;
                    }
                });
                if (verbose) Logger.debugForce("management task registered.");
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

        static SocketStream CreateUdpSocket() => MyStream.FromSocket(new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp));

        // move to the first
        void ActiveConnection(Connection c)
        {
            c.lastActive = WebSocket.CurrentTimeRough;
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
            public IUdpSocket remoteUdp;

            public LinkedListNode<Connection> node;

            public int lastActive;

            public Connection(UdpRelay relay, IPEndPoint clientEP, IUdpSocket remoteUdp)
            {
                this.node = new LinkedListNode<Connection>(this);
                this.relay = relay;
                this.clientEP = clientEP;
                this.remoteUdp = remoteUdp;
            }

            public AwaitableWrapper Send(BytesSegment bs, IPEndPoint destEP)
            {
                relay.ActiveConnection(this);
                Interlocked.Increment(ref relay.sentPackets);
                return remoteUdp?.WriteToAsyncR(bs, destEP) ?? AwaitableWrapper.GetCompleted();
            }

            public async void StartReceive()
            {
                try {
                    while (true) {
                        var r = await remoteUdp.ReadFromAsyncR(UdpBufferSize - headerLen, anyEp);
                        Interlocked.Increment(ref relay.receivedPackets);
                        var destEP = r.From;
                        if (relay.verbose)
                            relay.Logger.debugForce(r.Read + " B from dest " + destEP + " to " + clientEP);

                        var sendBuffer = BufferPool.GlobalGetBs(headerLen + r.Read);
                        var cur = 0;
                        relay.BuildHeader(sendBuffer, ref cur, destEP);
                        r.Buffer.CopyTo(sendBuffer.Sub(headerLen));
                        BufferPool.GlobalPut(r.Buffer.Bytes);
                        await relay.listenUdp.WriteToAsyncR(sendBuffer, clientEP);
                        BufferPool.GlobalPut(sendBuffer.Bytes);
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
                try {
                    if (relay.verbose)
                        relay.Logger.debugForce("close connection " + clientEP);
                    relay.RemoveConnection(this);
                    tmp?.Close();
                } catch (Exception e) {
                    relay.Logger.exception(e, Logging.Level.Error, "closing connection");
                }
            }
        }
    }
}

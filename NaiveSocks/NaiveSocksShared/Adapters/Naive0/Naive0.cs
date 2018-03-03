using Naive.HttpSvr;
using Nett;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static NaiveSocks.Socks5Server;

namespace NaiveSocks
{
    static class Naive0
    {
        public class Connection
        {
            private readonly Func<bool, IIVEncryptor> enc;
            public readonly WebSocket ws;

            object _syncRoot = new object();

            private bool ivSent, ivReceived;

            public SessionStream CurrentSession { get; private set; }

            public bool PerSessionIV { get; set; }

            public Connection(WebSocket ws, Func<bool, IIVEncryptor> enc)
            {
                this.ws = ws;
                this.enc = enc;
            }

            public bool CanOpen => CurrentSession == null
                || (CurrentSession.MState.IsClosed && CurrentSession.FinReceived);

            public int UsedCount { get; private set; }

            public SessionStream Open()
            {
                return new SessionStream(this);
            }

            private Task SendMsg(Msg msg)
            {
                if (ivSent) {
                    return ws.SendMsg(msg);
                }
                ivSent = true;
                return NaiveUtils.RunAsyncTask(async () => {
                    var writeEnc = enc(true);
                    await ws.SendBytesAsync(writeEnc.IV);
                    ws.AddWriteFilter(FilterBase.GetStreamFilterFromIVEncryptor(true, writeEnc, true));

                    await ws.SendMsg(msg);
                });
            }

            public void Close()
            {
                ws.Close();
            }

            public class SessionStream : IMsgStream
            {
                public MsgStreamStatus State => (MsgStreamStatus)MState;
                public MyStreamState MState { get; private set; }

                public int Generation { get; }

                public override string ToString() => $"{{Naive0 gen{Generation} on {_conn.ws.BaseStream}}}";

                object _syncRoot;

                Task lastRecv, lastSend;
                public bool FinReceived { get; private set; }
                private readonly Connection _conn;
                WebSocket ws => _conn.ws;

                public MsgStreamToMyStream AsMyStream { get; }

                public SessionStream(Connection conn)
                {
                    lock (conn._syncRoot) {
                        if (!conn.CanOpen)
                            throw new InvalidOperationException("!CanOpen");
                        _syncRoot = conn._syncRoot;
                        Generation = conn.UsedCount++;
                        conn.CurrentSession = this;
                    }
                    _conn = conn;
                    AsMyStream = new MsgStreamToMyStream(this);
                }

                public async Task<AddrPort> ReadHeader()
                {
                    var header = (await RecvMsg(null)).Data;
                    return AddrPort.FromSocks5Bytes(header);
                }

                public Task WriteHeader(AddrPort dest)
                {
                    var header = dest.ToSocks5Bytes();
                    return SendMsg(new Msg(header));
                }

                public Task SendMsg(Msg msg)
                {
                    lock (_syncRoot) {
                        if (MState.HasShutdown)
                            throw new InvalidOperationException("local shutdown");
                        if (lastSend?.IsCompleted == false)
                            throw new Exception("another SendMsg() task is running");
                        return lastSend = _conn.SendMsg(msg);
                    }
                }

                public Task<Msg> RecvMsg(BytesView buf)
                {
                    lock (_syncRoot) {
                        if (MState.HasRemoteShutdown)
                            throw new InvalidOperationException("remote shutdown");
                        if (lastRecv?.IsCompleted == false)
                            throw new Exception("another RecvMsg() task is running");
                        var t = _RecvMsg(buf);
                        lastRecv = t;
                        return t;
                    }
                }

                public async Task<Msg> _RecvMsg(BytesView buf)
                {
                    if (!_conn.ivReceived) {
                        _conn.ivReceived = true;
                        var recvEnc = _conn.enc(false);
                        recvEnc.IV = (await ws.ReadAsync()).payload;
                        ws.AddReadFilter(FilterBase.GetStreamFilterFromIVEncryptor(false, recvEnc, true));
                    }
                    var frame = await ws.ReadAsync();
                    if (frame.opcode == 0x01) {
                        OnFinReceived();
                        return Msg.EOF;
                    }
                    return frame.payload;
                }

                public Task Close(CloseOpt closeOpt)
                {
                    if (closeOpt.CloseType == CloseType.Close) {
                        Shutdown(SocketShutdown.Both);
                    } else {
                        Shutdown(closeOpt.ShutdownType);
                    }
                    return NaiveUtils.CompletedTask;
                }

                public void Shutdown(SocketShutdown direction)
                {
                    lock (_syncRoot) {
                        if (direction == SocketShutdown.Send || direction == SocketShutdown.Both) {
                            if (!MState.HasShutdown) {
                                MState |= MyStreamState.LocalShutdown;
                                var ls = lastSend;
                                if (ls?.IsCompleted == false) {
                                    lastSend = NaiveUtils.RunAsyncTask(async () => {
                                        try {
                                            await ls;
                                        } catch (Exception) {
                                            ;
                                        }
                                        SendFin().Forget();
                                    });
                                } else {
                                    SendFin().Forget();
                                }
                            }
                        }
                        if (direction == SocketShutdown.Receive || direction == SocketShutdown.Both) {
                            MState |= MyStreamState.RemoteShutdown;
                        }
                    }
                }

                private void OnFinReceived()
                {
                    MState |= MyStreamState.RemoteShutdown;
                    FinReceived = true;
                    if (_conn.PerSessionIV) {
                        ws.ReadFilter = null;
                        _conn.ivReceived = false;
                    }
                }

                private async Task SendFin()
                {
                    await ws.SendStringAsync("fin");
                    if (_conn.PerSessionIV) {
                        ws.WriteFilter = null;
                        _conn.ivSent = false;
                    }
                }

                public async Task<bool> TryShutdownForReuse()
                {
                    if (MState.IsClosed == false) {
                        Shutdown(SocketShutdown.Both);
                    }
                    if (!FinReceived) {
                        if (await TryReadUntilFin().WithTimeout(10 * 1000))
                            return false;
                    }
                    return FinReceived;
                }

                async Task TryReadUntilFin()
                {
                    // assume that this stream is closed,
                    // so read/write tasks of this stream are no longer working.

                    // wait for possible running tasks, which had run before stream closing.
                    await lastRecv.CompletedOnNull();
                    await lastSend.CompletedOnNull();

                    var triedLength = 0;
                    while (!FinReceived) {
                        try {
                            var frame = await _RecvMsg(null);
                            if ((triedLength += frame.Data.len) >= 512 * 1024) {
                                break;
                            }
                        } catch (Exception) {
                            break;
                        }
                    }
                }
            }
        }
    }
}

using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Reflection;

namespace NaiveSocks
{
    /// <summary>
    /// Fake async SocketStream implementation
    /// </summary>
    public class SocketStreamFa : SocketStream
    {
        public SocketStreamFa(Socket socket) : base(socket)
        {
            _read_ra.Tag = this;
        }

        BytesSegment _read_bs;
        ReusableAwaiter<int> _read_ra = new ReusableAwaiter<int>();

        protected override AwaitableWrapper<int> ReadAsyncRImpl(BytesSegment bs)
        {
            _read_ra.Reset();
            _read_bs = bs;
            ThreadPool.UnsafeQueueUserWorkItem((s) => {
                var that = (SocketStreamFa)s;
                var ra = that._read_ra;
                var thatBs = that._read_bs;
                int result;
                try {
                    result = that.ReadSocketDirectSync(thatBs);
                } catch (Exception e) {
                    ra.SetException(e);
                    return;
                } finally {
                    that._read_bs.Bytes = null;
                }
                ra.SetResult(result);
            }, this);
            return new AwaitableWrapper<int>(_read_ra);
        }

        BytesSegment _write_bs;
        ReusableAwaiter<VoidType> _write_ra = new ReusableAwaiter<VoidType>();

        public override AwaitableWrapper WriteAsyncRImpl(BytesSegment bs)
        {
            _write_ra.Reset();
            _write_bs = bs;
            ThreadPool.UnsafeQueueUserWorkItem((s) => {
                var that = (SocketStreamFa)s;
                var ra = that._write_ra;
                var thatBs = that._write_bs;
                try {
                    that.Socket.Send(thatBs.Bytes, thatBs.Offset, thatBs.Len, SocketFlags.None);
                } catch (Exception e) {
                    ra.SetException(e);
                    return;
                } finally {
                    that._write_bs.Bytes = null;
                }
                ra.SetResult(VoidType.Void);
            }, this);
            return new AwaitableWrapper(_write_ra);
        }

        protected override async Task<int> ReadAsyncImpl(BytesSegment bs)
        {
            return await ReadAsyncRImpl(bs);
        }

        object _write_lock = new object();
        Task _write_last;

        public override Task WriteAsyncImpl(BytesSegment bs)
        {
            Interlocked.Increment(ref ctr.Wasync);
            lock (_write_lock) {
                if (_write_last?.IsCompleted == false) {
                    _write_last = WriteAsync_Wait(bs, _write_last);
                }
                _write_last = Task.Run(() => this.Socket.Send(bs.Bytes, bs.Offset, bs.Len, SocketFlags.None));
                return _write_last;
            }
        }

        async Task WriteAsync_Wait(BytesSegment bs, Task towait)
        {
            await towait;
            await Task.Run(() => this.Socket.Send(bs.Bytes, bs.Offset, bs.Len, SocketFlags.None));
        }
    }
}

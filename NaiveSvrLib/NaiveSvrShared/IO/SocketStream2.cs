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

    public class SocketStream2 : SocketStream, IMyStreamMultiBuffer
    {
        static SocketStream2()
        {
            const int CachedMax = 32;
            cachedTaskFromIntResult = new Task<int>[CachedMax];
            for (int i = 0; i < cachedTaskFromIntResult.Length; i++) {
                cachedTaskFromIntResult[i] = Task.FromResult(i);
            }
        }

        public SocketStream2(Socket socket) : base(socket)
        { }

        private static Task<int>[] cachedTaskFromIntResult;

        private static readonly ObjectPool<SocketAsyncEventArgs> readArgPool = new ObjectPool<SocketAsyncEventArgs>(
            createFunc: () => {
                var arg = new SocketAsyncEventArgs();
                arg.Completed += ReadCompletedCallback;
                arg.UserToken = new ReadUserToken();
                return arg;
            }) {
            MaxCount = 48
        };

        private static readonly ObjectPool<SocketAsyncEventArgs> writeArgPool = new ObjectPool<SocketAsyncEventArgs>(
            createFunc: () => {
                var arg = new SocketAsyncEventArgs();
                arg.Completed += WriteCompletedCallback;
                return arg;
            }) {
            MaxCount = 16
        };

        private class ReadUserToken
        {
            public TaskCompletionSource<int> tcs;
            public SocketStream2 sw;
            public void Reset()
            {
                tcs = null;
                sw = null;
            }
        }

        TaskCompletionSource<int> _unusedWriteTcs, _unusedReadTcs;

        public override Task<int> ReadAsync(BytesSegment bv)
        {
            var e = readArgPool.GetValue();
            var userToken = ((ReadUserToken)e.UserToken);
            var tcs = _unusedReadTcs ?? new TaskCompletionSource<int>();
            _unusedReadTcs = null;
            userToken.tcs = tcs;
            var sw = userToken.sw = this;
            e.SetBuffer(bv.Bytes, bv.Offset, bv.Len);
            try {
                if (Socket.ReceiveAsync(e)) { // if opearation not completed synchronously
                    return tcs.Task;
                }
            } catch (Exception) {
                recycleReadArgs(e, userToken);
                throw;
            }
            var r = ReadCompleted(e, userToken, sw, out var ex);
            if (r < 0)
                throw ex;
            _unusedReadTcs = tcs;
            if (r < cachedTaskFromIntResult.Length)
                return cachedTaskFromIntResult[r];
            else
                return Task.FromResult(r);
        }

        private static void recycleReadArgs(SocketAsyncEventArgs e, ReadUserToken userToken)
        {
            userToken.Reset();
            e.SetBuffer(null, 0, 0);
            if (!readArgPool.PutValue(e))
                e.Dispose();
        }

        private static void ReadCompletedCallback(object sender, SocketAsyncEventArgs e)
        {
            try {
                var userToken = (ReadUserToken)e.UserToken;
                var sw = userToken.sw;
                var tcs = userToken.tcs;
                var r = ReadCompleted(e, userToken, sw, out var ex);
                if (r < 0)
                    tcs.SetException(ex);
                else
                    tcs.SetResult(r);
            } catch (Exception ex) {
                Logging.exception(ex, Logging.Level.Error, "ReadCompletedCallback");
                throw;
            }
        }

        private static int ReadCompleted(SocketAsyncEventArgs e, ReadUserToken userToken, SocketStream2 sw, out Exception exception)
        {
            int bytesTransferred = e.BytesTransferred;
            SocketError socketError = e.SocketError;
            recycleReadArgs(e, userToken);
            if (socketError == SocketError.Success) {
                if (bytesTransferred == 0) {
                    Logging.debug($"{sw}: remote shutdown");
                    sw.State |= MyStreamState.RemoteShutdown;
                }
                exception = null;
                return bytesTransferred;
            } else {
                exception = new SocketException((int)socketError);
                return -1;
            }
        }

        public override Task WriteAsync(BytesSegment bv)
        {
            var e = writeArgPool.GetValue();
            e.SetBuffer(bv.Bytes, bv.Offset, bv.Len);
            return SendAsync(e);
        }

        public Task WriteMultipleAsync(BytesView bv)
        {
            if (bv.nextNode == null)
                return WriteAsync(new BytesSegment(bv));
            var e = writeArgPool.GetValue();
            int count = 0;
            foreach (var cur in bv) {
                if (cur.len > 0)
                    count++;
            }
            var bufList = e.BufferList = new ArraySegment<byte>[count];
            var index = 0;
            foreach (var cur in bv) {
                if (cur.len > 0)
                    bufList[index++] = new ArraySegment<byte>(cur.bytes, cur.offset, cur.len);
            }
            return SendAsync(e);
        }

        private Task SendAsync(SocketAsyncEventArgs e)
        {
            var tcs = _unusedWriteTcs ?? new TaskCompletionSource<int>();
            _unusedWriteTcs = null;
            e.UserToken = tcs;
            try {
                if (Socket.SendAsync(e)) { // if opearation not completed synchronously
                    return tcs.Task;
                }
            } catch (Exception) {
                recycleWriteArgs(e);
                throw;
            }
            if (!WriteCompleted(e, out var ex))
                throw ex;
            _unusedWriteTcs = tcs;
            return NaiveUtils.CompletedTask;
        }

        private static void WriteCompletedCallback(object sender, SocketAsyncEventArgs e)
        {
            try {
                var tcs = e.UserToken as TaskCompletionSource<int>;
                if (WriteCompleted(e, out var ex))
                    tcs.SetResult(0);
                else
                    tcs.SetException(ex);
            } catch (Exception ex) {
                Logging.exception(ex, Logging.Level.Error, "WriteCompletedCallback");
                throw;
            }
        }

        private static bool WriteCompleted(SocketAsyncEventArgs e, out Exception exception)
        {
            SocketError socketError = e.SocketError;
            recycleWriteArgs(e);
            if (socketError == SocketError.Success) {
                exception = null;
                return true;
            } else {
                exception = new SocketException((int)socketError);
                return false;
            }
        }

        private static void recycleWriteArgs(SocketAsyncEventArgs e)
        {
            e.UserToken = null;
            if (e.BufferList == null)
                e.SetBuffer(null, 0, 0);
            else
                e.BufferList = null;
            if (!writeArgPool.PutValue(e))
                e.Dispose();
        }
    }
}

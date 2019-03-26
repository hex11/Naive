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
        public SocketStream2(Socket socket) : base(socket)
        {
            CheckInit(); // check in .ctor only
        }

        void CheckInit()
        {
            var tmp = nullIfInitialized;
            if (tmp != null) {
                lock (tmp) {
                    if (nullIfInitialized != null) {
                        nullIfInitialized = null;
                        readArgPool = new ObjectPool<SocketAsyncEventArgs>(
                            createFunc: () => {
                                var arg = new SocketAsyncEventArgs();
                                arg.Completed += ReadCompletedCallback;
                                arg.UserToken = new ReadUserToken();
                                return arg;
                            }) {
                            MaxCount = 32
                        };
                        writeArgPool = new ObjectPool<SocketAsyncEventArgs>(
                            createFunc: () => {
                                var arg = new SocketAsyncEventArgs();
                                arg.Completed += WriteCompletedCallback;
                                return arg;
                            }) {
                            MaxCount = 16
                        };
                    }
                }
            }
        }

        static object nullIfInitialized = new object();

        private static ObjectPool<SocketAsyncEventArgs> readArgPool;

        private static ObjectPool<SocketAsyncEventArgs> writeArgPool;

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

        TaskCompletionSource<VoidType> _unusedWriteTcs;
        TaskCompletionSource<int> _unusedReadTcs;

        protected override Task<int> ReadAsyncImpl(BytesSegment bs)
        {
            var e = readArgPool.GetValue();
            var userToken = ((ReadUserToken)e.UserToken);
            var tcs = _unusedReadTcs ?? new TaskCompletionSource<int>();
            _unusedReadTcs = null;
            userToken.tcs = tcs;
            var sw = userToken.sw = this;
            e.SetBuffer(bs.Bytes, bs.Offset, bs.Len);
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
            return NaiveUtils.GetCachedTaskInt(r);
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
                sw.OnAsyncReadCompleted(bytesTransferred);
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

        public override Task WriteAsyncImpl(BytesSegment bv)
        {
            var e = writeArgPool.GetValue();
            e.SetBuffer(bv.Bytes, bv.Offset, bv.Len);
            return SendAsync(e);
        }

        public AwaitableWrapper WriteMultipleAsyncR(BytesView bv)
        {
            return new AwaitableWrapper(WriteMultipleAsyncImpl(bv));
        }

        private Task WriteMultipleAsyncImpl(BytesView bv)
        {
            if (bv.nextNode == null)
                return WriteAsync(new BytesSegment(bv));
            var e = writeArgPool.GetValue();
            int count = 0;
            foreach (var cur in bv) {
                if (cur.len > 0)
                    count++;
            }
            var bufList = new ArraySegment<byte>[count];
            var index = 0;
            foreach (var cur in bv) {
                if (cur.len > 0)
                    bufList[index++] = new ArraySegment<byte>(cur.bytes, cur.offset, cur.len);
            }
            e.BufferList = bufList;
            return SendAsync(e);
        }

        private Task SendAsync(SocketAsyncEventArgs e)
        {
            var tcs = _unusedWriteTcs ?? new TaskCompletionSource<VoidType>();
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
            Interlocked.Increment(ref ctr.Wsync);
            return NaiveUtils.CompletedTask;
        }

        private static void WriteCompletedCallback(object sender, SocketAsyncEventArgs e)
        {
            try {
                var tcs = e.UserToken as TaskCompletionSource<VoidType>;
                if (WriteCompleted(e, out var ex)) {
                    Interlocked.Increment(ref ctr.Wasync);
                    tcs.SetResult(0);
                } else {
                    tcs.SetException(ex);
                }
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

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

    public class SocketStream1 : SocketStream, IMyStreamMultiBuffer
    {
        static FromAsyncTrimHelper<object, Socket, ArraySegment<byte>[]> helper;
        static FromAsyncTrimHelper<int, Socket, BytesSegment> helper2;

        static SocketStream1()
        {
            try {
                helper = new FromAsyncTrimHelper<object, Socket, ArraySegment<byte>[]>();
                helper2 = new FromAsyncTrimHelper<int, Socket, BytesSegment>();
            } catch (Exception e) {
                Logging.exception(e);
            }
        }

        public NetworkStream NetworkStream { get; }

        public SocketStream1(Socket socket) : base(socket)
        {
            this.NetworkStream = new NetworkStream(socket);
        }

        struct ReadWriteParameters
        {
            public byte[] Buffer;
            public int Offset, Count;
        }

        public override Task WriteAsync(BytesSegment bv)
        {
            return helper2.FromAsyncTrim(
                thisRef: Socket,
                args: bv,
                beginMethod: (socket, args, callback, state) => socket.BeginSend(args.Bytes, args.Offset, args.Len, SocketFlags.None, callback, state),
                endMethod: (socket, asyncResult) => {
                    socket.EndSend(asyncResult);
                    return 0;
                });
        }

        public override Task<int> ReadAsync(BytesSegment bv)
        {
            return helper2.FromAsyncTrim(
                thisRef: Socket,
                args: bv,
                beginMethod: (socket, args, callback, state) => socket.BeginReceive(args.Bytes, args.Offset, args.Len, SocketFlags.None, callback, state),
                endMethod: (socket, asyncResult) => socket.EndReceive(asyncResult));
        }

        public Task WriteMultipleAsync(BytesView bv)
        {
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
            return helper.FromAsyncTrim(
                Socket,
                bufList,
                (socket, args, callback, state) => {
                    return socket.BeginSend(args, SocketFlags.None, callback, state);
                },
                (socket, asyncResult) => {
                    socket.EndSend(asyncResult);
                    return null;
                });
        }
    }
    class FromAsyncTrimHelper<TResult, TInstance, TArgs> where TInstance : class
    {
        public FromAsyncTrimHelper()
        {
            var tftype = typeof(TaskFactory<TResult>);
            var delegateType = typeof(FromAsyncTrimDelegate<TInstance, TArgs>);
            var method = tftype
                .GetMethod("FromAsyncTrim", BindingFlags.Static | BindingFlags.NonPublic);

            _fromAsyncTrim = (FromAsyncTrimDelegate<TInstance, TArgs>)(method.MakeGenericMethod(typeof(TInstance), typeof(TArgs))
                .CreateDelegate(delegateType));
        }

        delegate Task<TResult> FromAsyncTrimDelegate<T1, T2>(
            T1 thisRef, T2 args,
            Func<T1, T2, AsyncCallback, object, IAsyncResult> beginMethod,
            Func<T1, IAsyncResult, TResult> endMethod) where T1 : class;

        FromAsyncTrimDelegate<TInstance, TArgs> _fromAsyncTrim;

        /// <summary>
        /// Special internal-only FromAsync support used by System.IO to wrap
        /// APM implementations with minimal overhead, avoiding unnecessary closure
        /// and delegate allocations.
        /// </summary>
        /// <typeparam name="TInstance">Specifies the type of the instance on which the APM implementation lives.</typeparam>
        /// <typeparam name="TArg1">Specifies the type containing the arguments.</typeparam>
        /// <param name="thisRef">The instance from which the begin and end methods are invoked.</param>
        /// <param name="beginMethod">The begin method.</param>
        /// <param name="endMethod">The end method.</param>
        /// <param name="args">The arguments.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public Task<TResult> FromAsyncTrim(
            TInstance thisRef, TArgs args,
            Func<TInstance, TArgs, AsyncCallback, object, IAsyncResult> beginMethod,
            Func<TInstance, IAsyncResult, TResult> endMethod)
        {
            return _fromAsyncTrim(thisRef, args, beginMethod, endMethod);
        }
    }
}

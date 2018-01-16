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
        public SocketStream1(Socket socket) : base(socket)
        {
        }

        public override Task<int> ReadAsync(BytesSegment bv)
        {
            return FromAsyncTrimHelper.FromAsyncTrim(
                thisRef: this,
                args: bv,
                beginMethod: (thisRef, args, callback, state) => thisRef.Socket.BeginReceive(args.Bytes, args.Offset, args.Len, SocketFlags.None, callback, state),
                endMethod: (thisRef, asyncResult) => {
                    var read = thisRef.Socket.EndReceive(asyncResult);
                    if (read == 0)
                        thisRef.State |= MyStreamState.RemoteShutdown;
                    return read;
                });
        }

        public override Task WriteAsync(BytesSegment bv)
        {
            return FromAsyncTrimHelper.FromAsyncTrim(
                thisRef: this,
                args: bv,
                beginMethod: (thisRef, args, callback, state) => thisRef.Socket.BeginSend(args.Bytes, args.Offset, args.Len, SocketFlags.None, callback, state),
                endMethod: (thisRef, asyncResult) => {
                    thisRef.Socket.EndSend(asyncResult);
                    return VoidType.Void;
                });
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
            return FromAsyncTrimHelper.FromAsyncTrim(
                thisRef: this,
                args: bufList,
                beginMethod: (thisRef, args, callback, state) => thisRef.Socket.BeginSend(args, SocketFlags.None, callback, state),
                endMethod: (thisRef, asyncResult) => {
                    thisRef.Socket.EndSend(asyncResult);
                    return VoidType.Void;
                });
        }
    }

    static class FromAsyncTrimHelper
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static Task<TResult> FromAsyncTrim<TResult, TInstance, TArgs>(
            TInstance thisRef, TArgs args,
            Func<TInstance, TArgs, AsyncCallback, object, IAsyncResult> beginMethod,
            Func<TInstance, IAsyncResult, TResult> endMethod) where TInstance : class
        {
            return FromAsyncTrimHelper<TResult, TInstance, TArgs>.FromAsyncTrim(thisRef, args, beginMethod, endMethod);
        }
    }

    static class FromAsyncTrimHelper<TResult, TInstance, TArgs> where TInstance : class
    {
        static FromAsyncTrimHelper()
        {
            try {
                _fromAsyncTrim = typeof(TaskFactory<TResult>)
                        .GetMethod("FromAsyncTrim", BindingFlags.Static | BindingFlags.NonPublic)
                        .MakeGenericMethod(typeof(TInstance), typeof(TArgs))
                        .CreateDelegate(typeof(FromAsyncTrimDelegate<TInstance, TArgs>))
                        as FromAsyncTrimDelegate<TInstance, TArgs>;
            } catch (Exception e) {
                Logging.exception(e, Logging.Level.Error, "cannot get FromAsyncTrim, will fallback to FromAsync.");
                _factory = new TaskFactory<TResult>();
            }
        }

        private delegate Task<TResult> FromAsyncTrimDelegate<T1, T2>(
            T1 thisRef, T2 args,
            Func<T1, T2, AsyncCallback, object, IAsyncResult> beginMethod,
            Func<T1, IAsyncResult, TResult> endMethod) where T1 : class;

        private static readonly FromAsyncTrimDelegate<TInstance, TArgs> _fromAsyncTrim;

        private static readonly TaskFactory<TResult> _factory;

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
        public static Task<TResult> FromAsyncTrim(
            TInstance thisRef, TArgs args,
            Func<TInstance, TArgs, AsyncCallback, object, IAsyncResult> beginMethod,
            Func<TInstance, IAsyncResult, TResult> endMethod)
        {
            if (_fromAsyncTrim != null)
                return _fromAsyncTrim(thisRef, args, beginMethod, endMethod);

            return _factory.FromAsync(beginMethod, (x) => endMethod(thisRef, x), thisRef, args, null, TaskCreationOptions.None);
        }
    }
}

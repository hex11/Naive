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

        protected override Task<int> ReadAsyncImpl(BytesSegment bs)
        {
            return TaskHelper.FromAsyncTrim(this, bs, ReadBeginMethod, ReadEndMethod);
        }

        private static IAsyncResult ReadBeginMethod(SocketStream1 thisRef, BytesSegment args, AsyncCallback callback, object state)
        {
            return thisRef.Socket.BeginReceive(args.Bytes, args.Offset, args.Len, SocketFlags.None, callback, state);
        }

        private static int ReadEndMethod(SocketStream1 thisRef, IAsyncResult asyncResult)
        {
            var read = thisRef.Socket.EndReceive(asyncResult);
            thisRef.OnAsyncReadCompleted(read);
            if (read == 0)
                thisRef.State |= MyStreamState.RemoteShutdown;
            return read;
        }

        public override Task WriteAsyncImpl(BytesSegment bs)
        {
            return TaskHelper.FromAsyncTrim(this, bs, WriteBeginMethod, WriteEndMethod);
        }

        private static IAsyncResult WriteBeginMethod(SocketStream1 thisRef, BytesSegment args, AsyncCallback callback, object state)
        {
            return thisRef.Socket.BeginSend(args.Bytes, args.Offset, args.Len, SocketFlags.None, callback, state);
        }

        private static VoidType WriteEndMethod(SocketStream1 thisRef, IAsyncResult asyncResult)
        {
            if (asyncResult.CompletedSynchronously)
                Interlocked.Increment(ref ctr.Wsync);
            else
                Interlocked.Increment(ref ctr.Wasync);
            thisRef.Socket.EndSend(asyncResult);
            return VoidType.Void;
        }

        public Task WriteMultipleAsync(BytesView bv)
        {
            ArraySegment<byte>[] bufList = PrepareWriteMultiple(bv);
            return TaskHelper.FromAsyncTrim(this, bufList, WriteMultipleBegin, WriteMultipleEnd);
        }

        private static ArraySegment<byte>[] PrepareWriteMultiple(BytesView bv)
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

            return bufList;
        }

        private static IAsyncResult WriteMultipleBegin(SocketStream1 thisRef, ArraySegment<byte>[] args, AsyncCallback callback, object state)
        {
            return thisRef.Socket.BeginSend(args, SocketFlags.None, callback, state);
        }

        private static VoidType WriteMultipleEnd(SocketStream1 thisRef, IAsyncResult asyncResult)
        {
            if (asyncResult.CompletedSynchronously)
                Interlocked.Increment(ref ctr.Wsync);
            else
                Interlocked.Increment(ref ctr.Wasync);
            thisRef.Socket.EndSend(asyncResult);
            return VoidType.Void;
        }

        ReusableAwaiter<int>.BeginEndStateMachine<SocketStream1> raR;
        ReusableAwaiter<VoidType>.BeginEndStateMachine<SocketStream1> raW;
        ReusableAwaiter<VoidType>.BeginEndStateMachine<SocketStream1> raWm;

        protected override AwaitableWrapper<int> ReadAsyncRImpl(BytesSegment bs)
        {
            //return new AwaitableWrapper<int>(AsyncHelper.Run(async () => {
            //    this.Socket.BeginReceive(bs.Bytes, bs.Offset, bs.Len, SocketFlags.None, BeginEndAwaiter.Callback, bea);
            //    var read = Socket.EndReceive(await bea);
            //    this.OnAsyncReadCompleted(read);
            //    if (read == 0)
            //        this.State |= MyStreamState.RemoteShutdown;
            //    return read;
            //}));

            //// The above code was used to test BeginEndAwaiter, it works well, but the following code should be faster.
            if (raR == null)
                raR = new ReusableAwaiter<int>.BeginEndStateMachine<SocketStream1>(this, ReadEndMethod);
            raR.Reset();
            ReadBeginMethod(this, bs, raR.ArgCallback, raR.ArgState);
            return raR.ToWrapper();
        }

        public override AwaitableWrapper WriteAsyncRImpl(BytesSegment bs)
        {
            if (raW == null)
                raW = new ReusableAwaiter<VoidType>.BeginEndStateMachine<SocketStream1>(this, WriteEndMethod);
            raW.Reset();
            WriteBeginMethod(this, bs, raW.ArgCallback, raW.ArgState);
            return new AwaitableWrapper(raW);
        }

        public AwaitableWrapper WriteMultipleAsyncR(BytesView bv)
        {
            if (raWm == null)
                raWm = new ReusableAwaiter<VoidType>.BeginEndStateMachine<SocketStream1>(this, WriteMultipleEnd);
            raWm.Reset();
            ArraySegment<byte>[] bufList = PrepareWriteMultiple(bv);
            WriteMultipleBegin(this, bufList, raWm.ArgCallback, raWm.ArgState);
            return new AwaitableWrapper(raWm);
        }
    }

    static class TaskHelper
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static Task<TResult> FromAsyncTrim<TResult, TInstance, TArgs>(
            TInstance thisRef, TArgs args,
            Func<TInstance, TArgs, AsyncCallback, object, IAsyncResult> beginMethod,
            Func<TInstance, IAsyncResult, TResult> endMethod) where TInstance : class
        {
            return TaskHelper<TResult, TInstance, TArgs>.FromAsyncTrim(thisRef, args, beginMethod, endMethod);
        }
    }

    static class TaskHelper<TResult, TInstance, TArgs> where TInstance : class
    {
        // See: https://referencesource.microsoft.com/#mscorlib/system/threading/Tasks/FutureFactory.cs,88f023d3850066a9

        static TaskHelper()
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

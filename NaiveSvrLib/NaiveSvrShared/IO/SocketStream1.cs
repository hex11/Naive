﻿using System;
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

        static Counters ctr;

        public struct Counters
        {
            public int Rasync, Rsync, Rbuffer, Wasync, Wsync;

            public string StringRead => $"Read {Rsync + Rasync + Rbuffer} (async {Rasync}, sync completed {Rsync}, from buffer {Rbuffer})";
            public string StringWrite => $"Write {Wsync + Wasync} (async {Wasync}, sync completed {Wsync})";
        }

        public static Counters GlobalCounters => ctr;

        public static string GetCountString()
        {
            return ctr.StringRead + ", " + ctr.StringWrite;
        }

        public int SmartReadBufferSize { get; set; } = 256;
        BytesSegment readBuffer;

        public bool EnableSmartReadBuffer { get; set; } = true;
        public bool EnableSmartSyncRead { get; set; } = true;

        public override Task<int> ReadAsync(BytesSegment bs)
        {
            // We use an internal buffer, whice is larger, to reduce many recv() syscalls when `bs` is very small.
            // It's important, since there is a hardware bug named 'Meltdown' in Intel CPUs.
            // TESTED: This optimization made ReadAsync 20x faster when bs.Len == 4, 
            //         on Windows 10 x64 16299.192 with a laptop Haswell CPU.
            var bufRead = TryReadInternalBuffer(ref bs);
            if (bufRead > 0)
                return NaiveUtils.GetCachedTaskInt(bufRead);
            if (EnableSmartSyncRead | EnableSmartReadBuffer) {
                var available = Socket.Available;
                var bufRead2 = TryFillAndReadInternalBuffer(ref bs, available);
                if (bufRead2 > 0)
                    return NaiveUtils.GetCachedTaskInt(bufRead2);
                if (EnableSmartSyncRead && available > 0) {
                    // if the receive buffer of OS is not empty,
                    // use sync operation to reduce async overhead.
                    Interlocked.Increment(ref ctr.Rsync);
                    var read = Socket.Receive(bs.Bytes, bs.Offset, bs.Len, SocketFlags.None);
                    if (read == 0)
                        throw new Exception("WTF! It should not happen: Socket.Receive() returns 0 when Socket.Available > 0.");
                    return NaiveUtils.GetCachedTaskInt(read);
                }
            }
            Interlocked.Increment(ref ctr.Rasync);
            return TaskHelper.FromAsyncTrim(
                thisRef: this,
                args: bs,
                beginMethod: (thisRef, args, callback, state) => thisRef.Socket.BeginReceive(args.Bytes, args.Offset, args.Len, SocketFlags.None, callback, state),
                endMethod: (thisRef, asyncResult) => {
                    var read = thisRef.Socket.EndReceive(asyncResult);
                    if (read == 0)
                        thisRef.State |= MyStreamState.RemoteShutdown;
                    return read;
                });
        }

        private int TryReadInternalBuffer(ref BytesSegment bs)
        {
            if (readBuffer.Len > 0) {
                Interlocked.Increment(ref ctr.Rbuffer);
                var read = Math.Min(bs.Len, readBuffer.Len);
                readBuffer.CopyTo(bs, read);
                readBuffer.SubSelf(read);
                // We can check if readBuffer is emptied, then release readBuffer here,
                // but we are not doing that, because there may be more small ReadAsync() calls later.
                return read;
            }
            return 0;
        }

        private int TryFillAndReadInternalBuffer(ref BytesSegment bs, int socketAvailable)
        {
            var readBufferSize = this.SmartReadBufferSize;
            if (EnableSmartReadBuffer && (socketAvailable > bs.Len & bs.Len < readBufferSize)) {
                Interlocked.Increment(ref ctr.Rsync);
                if (readBuffer.Bytes == null || readBuffer.Bytes.Length < readBufferSize)
                    readBuffer.Bytes = new byte[readBufferSize];
                readBuffer.Offset = 0;
                var read = Socket.Receive(readBuffer.Bytes, 0, readBufferSize, SocketFlags.None);
                readBuffer.Len = read;
                if (read == 0)
                    throw new Exception("WTF! It should not happen: Socket.Receive() returns 0 when Socket.Available > 0.");
                read = Math.Min(read, bs.Len);
                readBuffer.CopyTo(bs, read);
                readBuffer.SubSelf(read);
                return read;
            }
            // There is a large reading opearation, so the internal buffer can be released now.
            readBuffer.Bytes = null;
            return 0;
        }

        public override int Read(BytesSegment bs)
        {
            var bufRead = TryReadInternalBuffer(ref bs);
            if (bufRead > 0)
                return bufRead;
            return base.Read(bs);
        }

        public override Task WriteAsync(BytesSegment bs)
        {
            return TaskHelper.FromAsyncTrim(
                thisRef: this,
                args: bs,
                beginMethod: (thisRef, args, callback, state) => thisRef.Socket.BeginSend(args.Bytes, args.Offset, args.Len, SocketFlags.None, callback, state),
                endMethod: (thisRef, asyncResult) => {
                    if (asyncResult.CompletedSynchronously)
                        Interlocked.Increment(ref ctr.Wsync);
                    else
                        Interlocked.Increment(ref ctr.Wasync);
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
            return TaskHelper.FromAsyncTrim(
                thisRef: this,
                args: bufList,
                beginMethod: (thisRef, args, callback, state) => thisRef.Socket.BeginSend(args, SocketFlags.None, callback, state),
                endMethod: (thisRef, asyncResult) => {
                    if (asyncResult.CompletedSynchronously)
                        Interlocked.Increment(ref ctr.Wsync);
                    else
                        Interlocked.Increment(ref ctr.Wasync);
                    thisRef.Socket.EndSend(asyncResult);
                    return VoidType.Void;
                });
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

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    public static class AsyncHelper
    {
        public static readonly Task CompletedTask = Task.FromResult<int>(0);

        public static Task AsDelay(this int timeout)
        {
            return Task.Delay(timeout);
        }

        public static void Forget(this Task task)
        {
            // nothing to do
        }

        public static Task NoNRE(this Task task)
        {
            return task ?? CompletedTask;
        }

        // returns true if timed out
        public static WrappedAwaiter<Task, Task, bool>.Awaitable WithTimeout(this Task task, int timeout)
        {
            var timeoutTask = Task.Delay(timeout);
            return Task.WhenAny(task, timeoutTask)
                .Wrap(timeoutTask, (completedTask, timedoutTask) => completedTask == timedoutTask);
        }

        public static WrappedAwaiter<TR, TState, TNR>.Awaitable Wrap<TR, TState, TNR>(this Task<TR> task, TState state, Func<TR, TState, TNR> func)
        {
            return new WrappedAwaiter<TR, TState, TNR>.Awaitable(task.GetAwaiter(), state, func);
        }

        public struct WrappedAwaiter<TR, TState, TNR> : ICriticalNotifyCompletion
        {
            public bool IsCompleted => awaiter.IsCompleted;

            private TaskAwaiter<TR> awaiter;
            private Func<TR, TState, TNR> func;
            private TState state;

            public void OnCompleted(Action continuation)
            {
                awaiter.OnCompleted(continuation);
            }

            public void UnsafeOnCompleted(Action continuation)
            {
                awaiter.UnsafeOnCompleted(continuation);
            }

            public TNR GetResult()
            {
                return func(awaiter.GetResult(), state);
            }

            public struct Awaitable
            {
                public static async Task test()
                {
                    await new Awaitable();
                }

                WrappedAwaiter<TR, TState, TNR> awaiter;

                public WrappedAwaiter<TR, TState, TNR> GetAwaiter() => awaiter;

                public Awaitable(TaskAwaiter<TR> awaiter, TState state, Func<TR, TState, TNR> wrapper)
                {
                    this.awaiter = new WrappedAwaiter<TR, TState, TNR> {
                        awaiter = awaiter,
                        func = wrapper,
                        state = state
                    };
                }

                public TNR Wait() => awaiter.GetResult();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ConfiguredTaskAwaitable CAF(this Task task)
        {
            return task.ConfigureAwait(false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ConfiguredTaskAwaitable<T> CAF<T>(this Task<T> task)
        {
            return task.ConfigureAwait(false);
        }

        public static Task Run(Func<Task> asyncTask)
        {
            return asyncTask();
        }

        public static Task<T> Run<T>(Func<Task<T>> asyncTask)
        {
            return asyncTask();
        }

        public static void RunSync(this Task task)
        {
            task.GetAwaiter().GetResult();
            //var t = Task.Run(async () => await task);
            //t.Wait();
            //if (t.IsFaulted) {
            //    throw t.Exception;
            //}
        }

        public static T RunSync<T>(this Task<T> task)
        {
            return task.GetAwaiter().GetResult();
            //var t = Task.Run(async () => await task);
            //t.Wait();
            //if (t.IsFaulted) {
            //    throw t.Exception;
            //}
            //return t.Result;
        }

        public static async Task SetTimeout(int timeout, Func<Task> asyncTask)
        {
            await Task.Delay(timeout);
            await asyncTask();
        }

        public static Task SetTimeout(TimeSpan timeout, Func<Task> asyncTask)
            => SetTimeout(timeout, asyncTask, CancellationToken.None);

        public static async Task SetTimeout(TimeSpan timeout, Func<Task> asyncTask, CancellationToken ct)
        {
            await Task.Delay(timeout, ct);
            await asyncTask();
        }

        public static void SetTimeout(int timeout, Action action)
        {
            new Timer((s) => action(), null, timeout, -1);
        }

        public static void SetTimeout<TState>(TState state, int timeout, Action<TState> action)
        {
            new Timer((s) => action((TState)s), state, timeout, -1);
        }
    }

    // https://stackoverflow.com/a/40689207
    public sealed class ReusableAwaiter<T> : INotifyCompletion, ICriticalNotifyCompletion
    {
        private Action _continuation = null;
        private Action _continuation_2 = null;
        private T _result = default(T);
        private Exception _exception = null;
        private SpinLock _lock = new SpinLock(false);
        private int _waitForGetResult = 0;

        public object Tag;

        public string GetContinuationInfo()
        {
            if (_continuation == null)
                return "(no continuation)";
            return "[" + _continuation + ", " + _continuation_2 + "]";
        }

        public static ReusableAwaiter<T> NewCompleted(T result)
        {
            return new ReusableAwaiter<T>() { IsCompleted = true, _result = result };
        }

        public bool IsBeingListening => _continuation != null;

        public bool IsCompleted
        {
            get;
            private set;
        }

        public Exception Exception => _exception;

        public T GetResult()
        {
            PreGetResult();
            if (_exception != null)
                throw _exception;
            return _result;
        }

        public bool TryGetResult(out T result, out Exception exception)
        {
            PreGetResult();
            if (_exception != null) {
                result = default(T);
                exception = _exception;
                return false;
            }
            exception = null;
            result = _result;
            return true;
        }

        private void PreGetResult()
        {
            if (!IsCompleted) {
                Logging.logWithStackTrace("GetResult() when not completed", Logging.Level.Warning);
                throw new InvalidOperationException("not completed");
            }
            if (_waitForGetResult > 0)
                _waitForGetResult--;
        }

        public void OnCompleted(Action continuation)
        {
            bool lt = false;
            _lock.Enter(ref lt);
            if (this.IsCompleted) {
                _lock.Exit();
                continuation();
            } else {
                _waitForGetResult++;
                if (_continuation == null) {
                    _continuation = continuation;
                    _lock.Exit();
                } else if (_continuation_2 == null) {
                    _continuation_2 = continuation;
                    _lock.Exit();
                    Logging.logWithStackTrace("continuation_2", Logging.Level.Warning);
                } else {
                    _lock.Exit();
                    //throw new InvalidOperationException("This ReusableAwaiter instance has already been listened");
                    throw new Exception("Too many continuations");
                }
            }
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            OnCompleted(continuation);
        }

        /// <summary>
        /// Attempts to transition the completion state.
        /// </summary>
        /// <param name="result"></param>
        /// <returns></returns>
        public bool TrySetResult(T result)
        {
            bool lt = false;
            if (!this.IsCompleted) {
                _lock.Enter(ref lt);
                if (this.IsCompleted) {
                    _lock.Exit();
                    return false;
                }

                this._result = result;
                this.IsCompleted = true;

                ExitLockAndTryRunContinuations();
                return true;
            }
            return false;
        }

        public void SetResult(T result)
        {
            if (!TrySetResult(result))
                throw new InvalidOperationException("failed to SetResult");
        }

        /// <summary>
        /// Attempts to transition the exception state.
        /// </summary>
        /// <param name="result"></param>
        /// <returns></returns>
        public bool TrySetException(Exception exception)
        {
            bool lt = false;
            if (!this.IsCompleted) {
                _lock.Enter(ref lt);
                if (this.IsCompleted) {
                    _lock.Exit();
                    return false;
                }

                this._exception = exception;
                this.IsCompleted = true;

                ExitLockAndTryRunContinuations();
                return true;
            }
            return false;
        }

        private void ExitLockAndTryRunContinuations()
        {
            var c1 = _continuation;
            var c2 = _continuation_2;
            _continuation = null;
            _continuation_2 = null;
            _lock.Exit();
            JustAwaiter.TryRunContinuation(c1);
            JustAwaiter.TryRunContinuation(c2);
        }

        public void SetException(Exception exception)
        {
            if (!TrySetException(exception))
                throw new InvalidOperationException("failed to SetException. the exception to set: " + exception.Message);
        }

        public bool CanResetNow()
        {
            return _continuation == null && _waitForGetResult == 0;
        }

        /// <summary>
        /// Reset the awaiter to initial status
        /// </summary>
        public void Reset()
        {
            if (_continuation != null) {
                throw new InvalidOperationException("Cannot reset: this awaiter is being listening. (complete this awaiter before reset)");
            }
            if (_waitForGetResult != 0) {
                throw new InvalidOperationException("Connot reset: GetResult() haven't been called. Race conditions may happen. _waitGetResult=" + _waitForGetResult);
            }
            this._result = default(T);
            this._continuation = null;
            this._continuation_2 = null;
            this._exception = null;
            this.IsCompleted = false;
        }

        public ReusableAwaiter<T> GetAwaiter()
        {
            return this;
        }

        public static ReusableAwaiter<T> FromBeginEnd<TInstance, TArgs, T>(
            TInstance thisRef, Func<TInstance, TArgs, AsyncCallback, object, IAsyncResult> beginMethod,
            Func<TInstance, IAsyncResult, T> endMethod, out Action<TArgs> reusableStart)
        {
            var _ra = new ReusableAwaiter<T>();
            var _thisRef = thisRef;
            AsyncCallback _callback = (ar) => {
                T result;
                try {
                    result = endMethod(_thisRef, ar);
                } catch (Exception e) {
                    _ra.SetException(e);
                    return;
                }
                _ra.SetResult(result);
            };
            reusableStart = (args) => {
                _ra.Reset();
                beginMethod(_thisRef, args, _callback, _ra);
            };
            return _ra;
        }

        public ReusableAwaiter<T> CAF() => this;

        public async Task<T> CreateTask()
        {
            return await this;
        }
    }

    public sealed class JustAwaiter : INotifyCompletion, ICriticalNotifyCompletion
    {
        private static readonly Action IS_COMPLETED = () => { };
        private Action _continuation = null;

        public static JustAwaiter NewCompleted()
        {
            return new JustAwaiter() { _continuation = IS_COMPLETED };
        }

        public bool IsBeingListening => _continuation != null;

        public bool IsCompleted => _continuation == IS_COMPLETED;

        public void GetResult()
        {
        }

        public void OnCompleted(Action continuation)
        {
            if (continuation == null)
                throw new ArgumentNullException(nameof(continuation));

            if (_continuation == IS_COMPLETED) {
                TryRunContinuation(continuation);
                return;
            }
            var v = Interlocked.Exchange(ref _continuation, continuation);
            if (v == null) {
                // nothing to do
            } else if (v == IS_COMPLETED) {
                v = Interlocked.Exchange(ref _continuation, IS_COMPLETED);
                if (v == continuation) {
                    TryRunContinuation(continuation);
                } // or the continuation is called somewhere else
            } else {
                throw new Exception("a continuation is already registered!");
            }
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            OnCompleted(continuation);
        }

        public void SetBlocking(bool blocking)
        {
            if (!blocking && (_continuation == IS_COMPLETED))
                return;
            if (blocking) {
                var v = Interlocked.Exchange(ref _continuation, null);
                if (v == IS_COMPLETED || v == null) {
                    // nothing to do
                } else {
                    // then it's a continuation
                    _continuation = v;
                }
            } else {
                var v = Interlocked.Exchange(ref _continuation, IS_COMPLETED);
                if (v != null && v != IS_COMPLETED) {
                    TryRunContinuation(v);
                }
            }
        }

        public void Reset()
        {
            _continuation = null;
        }

        public JustAwaiter GetAwaiter()
        {
            return this;
        }

        static WaitCallback waitCallback = (state) => ((Action)state)();

        internal static void TryRunContinuation(Action c)
        {
            if (c != null) {
                try {
                    c();
                    //Task.Run(c);
                    //ThreadPool.QueueUserWorkItem(waitCallback, c);
                    //ThreadPool.UnsafeQueueUserWorkItem(waitCallback, c);
                } catch (Exception e) {
                    Logging.exception(e, Logging.Level.Error, "continuation threw an exception.");
                }
            }
        }
    }

    public struct AwaitableWrapper<T> : INotifyCompletion, ICriticalNotifyCompletion
    {
        public static readonly object COMPLETED = new object();
        object awaitable;
        T result;

        public AwaitableWrapper(T result)
        {
            awaitable = COMPLETED;
            this.result = result;
        }

        public AwaitableWrapper(Task<T> task)
        {
            awaitable = task ?? throw new ArgumentNullException(nameof(task));
            result = default(T);
        }

        public AwaitableWrapper(ReusableAwaiter<T> ra)
        {
            awaitable = ra ?? throw new ArgumentNullException(nameof(ra));
            result = default(T);
        }

        public AwaitableWrapper<T> GetAwaiter() => this;

        private static ConfiguredTaskAwaitable<T>.ConfiguredTaskAwaiter GetTaskAwaiter(Task<T> task)
        {
            return task.CAF().GetAwaiter();
        }

        public bool IsCompleted
        {
            get {
                if (awaitable == COMPLETED) {
                    return true;
                } else if (awaitable is Task<T> task) {
                    return GetTaskAwaiter(task).IsCompleted;
                } else if (awaitable is ReusableAwaiter<T> ra) {
                    return ra.IsCompleted;
                } else {
                    throw WrongAwaitableType();
                }
            }
        }

        public T GetResult()
        {
            if (awaitable == COMPLETED) {
                return result;
            } else if (awaitable is Task<T> task) {
                return GetTaskAwaiter(task).GetResult();
            } else if (awaitable is ReusableAwaiter<T> ra) {
                return ra.GetResult();
            } else {
                throw WrongAwaitableType();
            }
        }

        public void OnCompleted(Action continuation)
        {
            if (awaitable == COMPLETED) {
                JustAwaiter.TryRunContinuation(continuation);
            } else if (awaitable is Task<T> task) {
                GetTaskAwaiter(task).OnCompleted(continuation);
            } else if (awaitable is ReusableAwaiter<T> ra) {
                ra.OnCompleted(continuation);
            } else {
                throw WrongAwaitableType();
            }
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            if (awaitable == COMPLETED) {
                JustAwaiter.TryRunContinuation(continuation);
            } else if (awaitable is Task<T> task) {
                GetTaskAwaiter(task).UnsafeOnCompleted(continuation);
            } else if (awaitable is ReusableAwaiter<T> ra) {
                ra.UnsafeOnCompleted(continuation);
            } else {
                throw WrongAwaitableType();
            }
        }

        public AwaitableWrapper<T> CAF() => this;

        private Exception WrongAwaitableType()
        {
            return new Exception("should not happend! awaitable=" + awaitable);
        }
    }

    public struct AwaitableWrapper : INotifyCompletion, ICriticalNotifyCompletion
    {
        public static readonly object COMPLETED = new object();
        object awaitable;

        public static AwaitableWrapper GetCompleted() => new AwaitableWrapper { awaitable = COMPLETED };

        public AwaitableWrapper(Task task)
        {
            awaitable = task ?? throw new ArgumentNullException(nameof(task));
        }

        public AwaitableWrapper(ReusableAwaiter<VoidType> ra)
        {
            awaitable = ra ?? throw new ArgumentNullException(nameof(ra));
        }

        public AwaitableWrapper GetAwaiter() => this;

        private static ConfiguredTaskAwaitable.ConfiguredTaskAwaiter GetTaskAwaiter(Task task)
        {
            return task.CAF().GetAwaiter();
        }

        public bool IsCompleted
        {
            get {
                if (awaitable == COMPLETED) {
                    return true;
                } else if (awaitable is Task task) {
                    return GetTaskAwaiter(task).IsCompleted;
                } else if (awaitable is ReusableAwaiter<VoidType> ra) {
                    return ra.IsCompleted;
                } else {
                    throw WrongAwaitableType();
                }
            }
        }

        public void GetResult()
        {
            if (awaitable == COMPLETED) {
                // noop
            } else if (awaitable is Task task) {
                GetTaskAwaiter(task).GetResult();
            } else if (awaitable is ReusableAwaiter<VoidType> ra) {
                ra.GetResult();
            } else {
                throw WrongAwaitableType();
            }
        }

        public void OnCompleted(Action continuation)
        {
            if (awaitable == COMPLETED) {
                JustAwaiter.TryRunContinuation(continuation);
            } else if (awaitable is Task task) {
                GetTaskAwaiter(task).OnCompleted(continuation);
            } else if (awaitable is ReusableAwaiter<VoidType> ra) {
                ra.OnCompleted(continuation);
            } else {
                throw WrongAwaitableType();
            }
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            if (awaitable == COMPLETED) {
                JustAwaiter.TryRunContinuation(continuation);
            } else if (awaitable is Task task) {
                GetTaskAwaiter(task).UnsafeOnCompleted(continuation);
            } else if (awaitable is ReusableAwaiter<VoidType> ra) {
                ra.UnsafeOnCompleted(continuation);
            } else {
                throw WrongAwaitableType();
            }
        }

        public AwaitableWrapper CAF() => this;

        private Exception WrongAwaitableType()
        {
            return new Exception("should not happend! awaitable=" + awaitable);
        }
    }
}

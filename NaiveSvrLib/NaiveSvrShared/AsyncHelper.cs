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

    public interface IAwaiter<T> : INotifyCompletion, ICriticalNotifyCompletion
    {
        T GetResult();
        bool IsCompleted { get; }
    }

    // https://stackoverflow.com/a/40689207
    public class ReusableAwaiter<T> : IAwaiter<T>
    {
        private object _continuation = null; // action or exception
        private T _result = default(T);
        private SpinLock _lock = new SpinLock(false);
        private State state;

        enum State : byte
        {
            Reset,
            ContRegistered,
            Completed
        }

        public object Tag;

        public string GetContinuationInfo()
        {
            if (_continuation == null)
                return "(no continuation)";
            return "[" + _continuation + "]";
        }

        public static ReusableAwaiter<T> NewCompleted(T result)
        {
            return new ReusableAwaiter<T>() { state = State.Completed, _result = result };
        }

        public bool IsBeingListening => _continuation is Action;

        public bool IsCompleted => state == State.Completed;

        public Exception Exception => _continuation as Exception;

        public T GetResult()
        {
            PreGetResult();
            if (_continuation is Exception ex)
                throw ex;
            return _result;
        }

        public bool TryGetResult(out T result, out Exception exception)
        {
            PreGetResult();
            if (_continuation is Exception ex) {
                result = default(T);
                exception = ex;
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
        }

        public void OnCompleted(Action continuation)
        {
            bool lt = false;
            _lock.Enter(ref lt);
            if (this.IsCompleted) {
                _lock.Exit();
                continuation();
            } else {
                if (_continuation == null) {
                    _continuation = continuation;
                    this.state = State.ContRegistered;
                    _lock.Exit();
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
                this.state = State.Completed;

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
                var c1 = _continuation;
                this._continuation = exception;
                this.state = State.Completed;
                _lock.Exit();
                if (c1 is Action action)
                    JustAwaiter.RunContinuation(action);
                return true;
            }
            return false;
        }

        private void ExitLockAndTryRunContinuations()
        {
            var c1 = _continuation;
            _continuation = null;
            _lock.Exit();
            if (c1 is Action action)
                JustAwaiter.RunContinuation(action);
        }

        public void SetException(Exception exception)
        {
            if (!TrySetException(exception))
                throw new InvalidOperationException("failed to SetException. the exception to set: " + exception.Message);
        }

        public bool CanResetNow()
        {
            return IsBeingListening;
        }

        /// <summary>
        /// Reset the awaiter to initial status
        /// </summary>
        public void Reset()
        {
            if (IsBeingListening) {
                throw new InvalidOperationException($"Cannot reset: this awaiter is being listening. (completed={IsCompleted})");
            }
            this._result = default(T);
            this._continuation = null;
            this.state = State.Reset;
        }

        public ReusableAwaiter<T> GetAwaiter()
        {
            return this;
        }

        public ReusableAwaiter<T> CAF() => this;

        public async Task<T> CreateTask()
        {
            return await this;
        }

        public class BeginEndStateMachine<TInstance, TArgs> : ReusableAwaiter<T>
        {
            TInstance _thisRef;
            Func<TInstance, TArgs, AsyncCallback, object, IAsyncResult> _beginMethod;
            Func<TInstance, IAsyncResult, T> _endMethod;

            public BeginEndStateMachine(TInstance thisRef, Func<TInstance, TArgs, AsyncCallback, object, IAsyncResult> beginMethod,
            Func<TInstance, IAsyncResult, T> endMethod)
            {
                _thisRef = thisRef;
                _beginMethod = beginMethod;
                _endMethod = endMethod;
            }

            private static readonly AsyncCallback _callback = Callback;
            private static void Callback(IAsyncResult ar)
            {
                var thiz = (BeginEndStateMachine<TInstance, TArgs>)ar.AsyncState;
                thiz.InstanceCallback(ar);
            }

            private void InstanceCallback(IAsyncResult ar)
            {
                T result;
                try {
                    result = _endMethod(_thisRef, ar);
                } catch (Exception e) {
                    SetException(e);
                    return;
                }
                SetResult(result);
            }

            public ReusableAwaiter<T> Start(TArgs args)
            {
                Reset();
                _beginMethod(_thisRef, args, _callback, this);
                return this;
            }
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
                RunContinuation(continuation);
                return;
            }
            var v = Interlocked.CompareExchange(ref _continuation, continuation, null);
            if (v == null) {
                // continuation is exchanged and nothing to do
            } else if (v == IS_COMPLETED) {
                RunContinuation(continuation);
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
            if (blocking && (_continuation == null))
                return;
            if (!blocking && (_continuation == IS_COMPLETED))
                return;
            if (blocking) {
                var v = Interlocked.CompareExchange(ref _continuation, null, IS_COMPLETED);
                // if v is null or a continuation: it's already blocking
                // else v will be IS_COMPLETED: successfully set to blocking state
            } else {
                var v = Interlocked.Exchange(ref _continuation, IS_COMPLETED);
                if (v != null && v != IS_COMPLETED) {
                    RunContinuation(v);
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

        internal static void RunContinuation(Action c)
        {
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

        public AwaitableWrapper(IAwaiter<T> ia)
        {
            awaitable = ia ?? throw new ArgumentNullException(nameof(ia));
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
                } else if (awaitable is IAwaiter<T> ia) {
                    return ia.IsCompleted;
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
            } else if (awaitable is IAwaiter<T> ia) {
                return ia.GetResult();
            } else {
                throw WrongAwaitableType();
            }
        }

        public void OnCompleted(Action continuation)
        {
            if (awaitable == COMPLETED) {
                JustAwaiter.RunContinuation(continuation);
            } else if (awaitable is Task<T> task) {
                GetTaskAwaiter(task).OnCompleted(continuation);
            } else if (awaitable is ReusableAwaiter<T> ra) {
                ra.OnCompleted(continuation);
            } else if (awaitable is IAwaiter<T> ia) {
                ia.OnCompleted(continuation);
            } else {
                throw WrongAwaitableType();
            }
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            if (awaitable == COMPLETED) {
                JustAwaiter.RunContinuation(continuation);
            } else if (awaitable is Task<T> task) {
                GetTaskAwaiter(task).UnsafeOnCompleted(continuation);
            } else if (awaitable is ReusableAwaiter<T> ra) {
                ra.UnsafeOnCompleted(continuation);
            } else if (awaitable is IAwaiter<T> ia) {
                ia.UnsafeOnCompleted(continuation);
            } else {
                throw WrongAwaitableType();
            }
        }

        public AwaitableWrapper<T> CAF() => this;

        /// <summary>
        /// Incr by 1 or reset to 0.
        /// </summary>
        public AwaitableWrapper<T> SyncCounter(ref int counter)
        {
            if (this.IsCompleted) {
                counter++;
            } else {
                counter = 0;
            }
            return this;
        }

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

        public AwaitableWrapper(IAwaiter<VoidType> ia)
        {
            awaitable = ia ?? throw new ArgumentNullException(nameof(ia));
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
                } else if (awaitable is IAwaiter<VoidType> ia) {
                    return ia.IsCompleted;
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
            } else if (awaitable is IAwaiter<VoidType> ia) {
                ia.GetResult();
            } else {
                throw WrongAwaitableType();
            }
        }

        public void OnCompleted(Action continuation)
        {
            if (awaitable == COMPLETED) {
                JustAwaiter.RunContinuation(continuation);
            } else if (awaitable is Task task) {
                GetTaskAwaiter(task).OnCompleted(continuation);
            } else if (awaitable is ReusableAwaiter<VoidType> ra) {
                ra.OnCompleted(continuation);
            } else if (awaitable is IAwaiter<VoidType> ia) {
                ia.OnCompleted(continuation);
            } else {
                throw WrongAwaitableType();
            }
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            if (awaitable == COMPLETED) {
                JustAwaiter.RunContinuation(continuation);
            } else if (awaitable is Task task) {
                GetTaskAwaiter(task).UnsafeOnCompleted(continuation);
            } else if (awaitable is ReusableAwaiter<VoidType> ra) {
                ra.UnsafeOnCompleted(continuation);
            } else if (awaitable is IAwaiter<VoidType> ia) {
                ia.UnsafeOnCompleted(continuation);
            } else {
                throw WrongAwaitableType();
            }
        }

        public AwaitableWrapper CAF() => this;

        /// <summary>
        /// Incr by 1 or reset to 0.
        /// </summary>
        public AwaitableWrapper SyncCounter(ref int counter)
        {
            if (this.IsCompleted) {
                counter++;
            } else {
                counter = 0;
            }
            return this;
        }

        private Exception WrongAwaitableType()
        {
            return new Exception("should not happend! awaitable=" + awaitable);
        }
    }
}

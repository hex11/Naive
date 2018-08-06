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

        public static async void SetTimeout(int timeout, Action action)
        {
            await Task.Delay(timeout);
            action();
        }
    }

    // https://stackoverflow.com/a/40689207
    public sealed class ReusableAwaiter<T> : INotifyCompletion
    {
        private Action _continuation = null;
        private T _result = default(T);
        private Exception _exception = null;
        private SpinLock _lock = new SpinLock(false);

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

        public T GetResult()
        {
            if (!IsCompleted)
                throw new InvalidOperationException("not completed");
            if (_exception != null)
                throw _exception;
            return _result;
        }

        public void OnCompleted(Action continuation)
        {
            if (_continuation != null)
                throw new InvalidOperationException("This ReusableAwaiter instance has already been listened");
            bool lt = false;
            _lock.Enter(ref lt);
            if (this.IsCompleted) {
                _lock.Exit();
                continuation();
            } else {
                _continuation = continuation;
                _lock.Exit();
            }
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

                var c = TryGetContinuation();
                _lock.Exit();
                c?.Invoke();
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

                var c = TryGetContinuation();
                _lock.Exit();
                c?.Invoke();
                return true;
            }
            return false;
        }

        private Action TryGetContinuation()
        {
            var tmp = _continuation;
            if (tmp != null) {
                _continuation = null;
                return tmp;
            }
            return null;
        }

        /// <summary>
        /// Reset the awaiter to initial status
        /// </summary>
        /// <returns></returns>
        public ReusableAwaiter<T> Reset()
        {
            if (_continuation != null) {
                throw new InvalidOperationException("Cannot reset: this awaiter is being listening. (complete this awaiter before reset)");
            }
            this._result = default(T);
            this._continuation = null;
            this._exception = null;
            this.IsCompleted = false;
            return this;
        }

        public ReusableAwaiter<T> GetAwaiter()
        {
            return this;
        }
    }
}

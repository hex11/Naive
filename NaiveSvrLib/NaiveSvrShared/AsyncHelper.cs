using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    public static class AsyncHelper
    {
        public static readonly Task CompletedTask = Task.FromResult<object>(null);

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
                        func = wrapper
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

        public static async Task SetTimeout(TimeSpan timeout, Func<Task> asyncTask)
        {
            await Task.Delay(timeout);
            await asyncTask();
        }

        public static async void SetTimeout(int timeout, Action action)
        {
            await Task.Delay(timeout);
            action();
        }
    }
}

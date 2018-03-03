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

        public static void Forget(this Task task)
        {
            // nothing to do
        }

        public static Task CompletedOnNull(this Task task)
        {
            return task ?? CompletedTask;
        }

        public static TimeoutAwaiter.Awaitable WithTimeout(this Task task, int timeout)
        {
            var timeoutTask = Task.Delay(timeout);
            return new TimeoutAwaiter.Awaitable(task, timeoutTask);
        }

        public struct TimeoutAwaiter : ICriticalNotifyCompletion
        {
            public bool IsCompleted => whenAnyTaskAwaiter.IsCompleted;

            private Task timeoutTask;
            private TaskAwaiter<Task> whenAnyTaskAwaiter;

            public void OnCompleted(Action continuation)
            {
                whenAnyTaskAwaiter.OnCompleted(continuation);
            }

            public void UnsafeOnCompleted(Action continuation)
            {
                whenAnyTaskAwaiter.UnsafeOnCompleted(continuation);
            }

            public bool GetResult()
            {
                return whenAnyTaskAwaiter.GetResult() == timeoutTask;
            }

            public struct Awaitable
            {
                public static async Task test()
                {
                    await new Awaitable();
                }

                TimeoutAwaiter awaiter;

                public TimeoutAwaiter GetAwaiter() => awaiter;

                public Awaitable(Task task, Task timeoutTask)
                {
                    awaiter = new TimeoutAwaiter {
                        timeoutTask = timeoutTask,
                        whenAnyTaskAwaiter = Task.WhenAny(task, timeoutTask).GetAwaiter()
                    };
                }
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
            var t = Task.Run(async () => await task);
            t.Wait();
            if (t.IsFaulted) {
                throw t.Exception;
            }
        }

        public static T RunSync<T>(this Task<T> task)
        {
            var t = Task.Run(async () => await task);
            t.Wait();
            if (t.IsFaulted) {
                throw t.Exception;
            }
            return t.Result;
        }

        public static async Task SetTimeout(int timeout, Func<Task> asyncTask)
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

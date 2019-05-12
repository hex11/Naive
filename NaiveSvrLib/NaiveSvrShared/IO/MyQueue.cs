using Naive.HttpSvr;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    public class MyQueue<T> : IEnumerable<T>
    {
        private T[] arr;
        private int firstIndex;
        private int count;

        public int Count => count;

        public MyQueue()
        {
        }

        public MyQueue(int initialCapacity)
        {
            arr = new T[initialCapacity];
        }

        public void Enqueue(T val)
        {
            if (arr == null) {
                arr = new T[4];
            } else if (arr.Length == count) {
                var newarr = new T[arr.Length * 4];
                for (int i = 0; i < count; i++) {
                    newarr[i] = PeetAtInternal(i);
                }
                arr = newarr;
                firstIndex = 0;
            }
            arr[(firstIndex + count) % arr.Length] = val;
            count++;
        }

        public T Dequeue()
        {
            if (!TryDequeue(out var val))
                throw new InvalidOperationException("cannot dequeue: queue is empty.");
            return val;
        }

        public T Peek()
        {
            if (!TryPeek(out var val))
                throw new InvalidOperationException("cannot peek: queue is empty.");
            return val;
        }

        public bool TryDequeue(out T val)
        {
            return DequeueCore(false, out val);
        }

        public bool TryPeek(out T val)
        {
            return DequeueCore(true, out val);
        }

        private bool DequeueCore(bool peek, out T val)
        {
            if (count == 0) {
                val = default(T);
                return false;
            }
            val = arr[firstIndex];
            if (!peek) {
                arr[firstIndex] = default(T); // to release references
                count--;
                if (count == 0) {
                    firstIndex = 0;
                } else {
                    firstIndex = (firstIndex + 1) % arr.Length;
                }
            }
            return true;
        }

        public void Clear()
        {
            for (int i = 0; i < count; i++) {
                arr[(firstIndex + i) % arr.Length] = default(T); // to release references
            }
            firstIndex = count = 0;
        }

        public T PeekAt(int index)
        {
            if (index < 0 || index >= count) {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return PeetAtInternal(index);
        }

        private T PeetAtInternal(int index)
        {
            return arr[(firstIndex + index) % arr.Length];
        }

        public void SetAt(int index, T val)
        {
            if (index < 0 || index >= count) {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            arr[(firstIndex + index) % arr.Length] = val;
        }

        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<T>
        {
            private readonly MyQueue<T> _queue;

            public Enumerator(MyQueue<T> queue)
            {
                _queue = queue;
                i = 0;
                Current = default(T);
            }

            private int i;

            public T Current { get; private set; }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (i < _queue.count) {
                    Current = _queue.PeekAt(i++);
                    return true;
                } else {
                    Current = default(T);
                    return false;
                }
            }

            public void Reset()
            {
                i = 0;
                Current = default(T);
            }
        }
    }
}

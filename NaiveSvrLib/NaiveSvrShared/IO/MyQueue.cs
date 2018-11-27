using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NaiveSocks
{
    public class MyQueue<T>
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
            try {
                if (arr.Length == count) {
                    var newarr = new T[arr.Length * 4];
                    for (int i = 0; i < count; i++) {
                        var readI = (firstIndex + i) % newarr.Length;
                        newarr[i] = arr[readI];
                    }
                    arr = newarr;
                    firstIndex = 0;
                }
            } catch (NullReferenceException) {
                arr = new T[4];
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
            return TryDequeueImpl(false, out val);
        }

        public bool TryPeek(out T val)
        {
            return TryDequeueImpl(true, out val);
        }

        public bool TryDequeueImpl(bool peek, out T val)
        {
            if (count == 0) {
                val = default(T);
                return false;
            }
            val = arr[firstIndex];
            if (!peek) {
                arr[firstIndex] = default(T); // to release references
                firstIndex = (firstIndex + 1) % arr.Length;
                count--;
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
            return arr[(firstIndex + index) % arr.Length];
        }

        public void SetAt(int index, T val)
        {
            if (index < 0 || index >= count) {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            arr[(firstIndex + index) % arr.Length] = val;
        }
    }
}

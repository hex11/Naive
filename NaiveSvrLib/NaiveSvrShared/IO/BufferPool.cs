using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Naive.HttpSvr
{
    public class BufferPool
    {
        public static BufferPool GlobalPool = new BufferPool(
            maxBuffers: 32,
            maxTotalSize: 2 * 1024 * 1024,
            maxBufferSize: 128 * 1024);

        public static byte[] GlobalGet(int size)
        {
            var buf = GlobalPool?.Get(size);
            //if (buf != null) {
            //    Logging.debug("get " + buf.Length + " > " + size);
            //} else {
            //    Logging.debug("get " + size + " failed");
            //}
            return buf ?? new byte[size];
        }

        public static void GlobalPut(byte[] buffer)
        {
            var ok = GlobalPool?.Put(buffer) == true;
            //if (ok) {
            //    Logging.debug("put " + buffer.Length);
            //} else {
            //    Logging.debug("put " + buffer.Length + " failed");
            //}
        }

        public BufferPool(int maxBuffers, int maxTotalSize, int maxBufferSize)
        {
            pool = new Item[maxBuffers];
            this.MaxTotalSize = maxTotalSize;
            MaxBufferSize = maxBufferSize;
        }

        public int MaxTotalSize { get; }
        public int MaxBufferSize { get; }

        public int CurrentTotalSize { get; private set; }

        Item[] pool;

        int bufferCount;

        struct Item
        {
            public int size;
            public byte[] buffer;
        }

        public byte[] Get(int minSize)
        {
            if (minSize < 0)
                throw new ArgumentOutOfRangeException();
            if (minSize == 0)
                return NaiveUtils.ZeroBytes;
            if (minSize > MaxBufferSize)
                return null;

            if (bufferCount == 0)
                return null;

            int itemIndex = -1;
            Item item = new Item() { buffer = null, size = Int32.MaxValue };
            lock (pool) {
                if (bufferCount == 0)
                    return null;
                for (int i = 0; i < pool.Length; i++) {
                    if (pool[i].size >= minSize && pool[i].size < item.size) {
                        itemIndex = i;
                        item = pool[i];
                    }
                }
                if (itemIndex >= 0) {
                    pool[itemIndex] = new Item();
                    bufferCount--;
                    CurrentTotalSize -= item.size;
                }
            }
            return item.buffer; // can be null
        }

        public byte[] GetOrNew(int size)
        {
            return Get(size) ?? new byte[size];
        }

        public bool Put(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            int bufLen = buffer.Length;
            if (bufLen == 0 || bufLen > MaxBufferSize)
                return false;

            if (bufferCount >= pool.Length || CurrentTotalSize + bufLen > MaxTotalSize)
                return false;

            lock (pool) {
                if (bufferCount >= pool.Length || CurrentTotalSize + bufLen > MaxTotalSize)
                    return false;
                for (int i = 0; i < pool.Length; i++) {
                    if (pool[i].size == 0) {
                        pool[i] = new Item { size = bufLen, buffer = buffer };
                        bufferCount++;
                        CurrentTotalSize += bufLen;
                        return true;
                    }
                }
            }
            return false;
        }

        public void Clear()
        {
            lock (pool) {
                if (bufferCount == 0)
                    return;
                for (int i = 0; i < pool.Length; i++) {
                    pool[i] = new Item();
                }
                bufferCount = 0;
            }
        }
    }
}

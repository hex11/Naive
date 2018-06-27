using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Naive.HttpSvr
{
    public class BufferHeap
    {
        public static BufferHeap GlobalPool;

        public static GetResult GlobalGet(int size)
        {
            return GlobalPool?.Get(size) ?? new GetResult { BytesSegment = new BytesSegment(new byte[size]) };
        }

        public BufferHeap(int size)
        {
            int blocksCount = GetBlocksCountFromSize(size);
            usedBlocks = new bool[blocksCount];
            baseBuffer = new byte[size];
        }

        const int BlockSize = 4096;

        bool[] usedBlocks;
        byte[] baseBuffer;

        public GetResult? Get(int size)
        {
            if (size < 0)
                throw new ArgumentOutOfRangeException();
            if (size == 0)
                return new GetResult() {
                    Handle = new Handle { pool = this },
                    BytesSegment = new BytesSegment(NaiveUtils.ZeroBytes, 0, 0)
                };

            int blkCount = GetBlocksCountFromSize(size);
            int freeCount = 0;
            lock (usedBlocks) {
                for (int i = 0; i < usedBlocks.Length; i++) {
                    if (!usedBlocks[i]) {
                        freeCount++;
                        if (freeCount == blkCount) {
                            var blkOffset = i - blkCount + 1;
                            for (int j = 0; j < blkCount; j++) {
                                usedBlocks[blkOffset + j] = true;
                            }
                            Logging.debugForce("get " + size);
                            return new GetResult() {
                                Handle = new Handle { pool = this, blkOffset = blkOffset, blkCount = blkCount },
                                BytesSegment = new BytesSegment(baseBuffer, blkOffset * BlockSize, size)
                            };
                        }
                    } else {
                        freeCount = 0;
                    }
                }
            }
            return null;
        }

        public GetResult GetOrNew(int size)
        {
            return Get(size) ?? new GetResult { BytesSegment = new BytesSegment(new byte[size]) };
        }

        public void Put(Handle h)
        {
            if (h.pool != this)
                throw new Exception("can not put: the handle is not belong to this pool.");
            PutInternal(h);
        }

        private void PutInternal(Handle h)
        {
            for (int i = 0; i < h.blkCount; i++) {
                usedBlocks[h.blkOffset + i] = false;
            }
            Logging.debugForce("put blocks: " + h.blkCount);
        }

        private static int GetBlocksCountFromSize(int size)
        {
            var blocksCount = Math.DivRem(size, BlockSize, out var rem);
            if (rem > 0)
                blocksCount++;
            return blocksCount;
        }

        public struct GetResult
        {
            public Handle Handle;
            public BytesSegment BytesSegment;
            public BytesView GetBytesView() => new BytesView(BytesSegment.Bytes, BytesSegment.Offset, BytesSegment.Len);
        }

        public struct Handle
        {
            public BufferHeap pool;
            public int blkOffset;
            public int blkCount;

            public bool TryPut()
            {
                if (pool != null) {
                    pool.PutInternal(this);
                    return true;
                }
                return false;
            }
        }
    }
}

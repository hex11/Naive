using System;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class MyStreamWrapper : MyStream, IMyStreamReadR, IMyStreamWriteR, IMyStreamNoBuffer
    {
        public MyStreamWrapper(IMyStream baseStream)
        {
            BaseStream = baseStream;
        }

        public IMyStream BaseStream { get; }

        public BytesSegment Queue;

        public AwaitableWrapper WaitBeforeRead = AwaitableWrapper.GetCompleted();

        public event Action<MyStreamWrapper, BytesSegment> WritingBaseStream;
        private void OnWriting(BytesSegment bs) => WritingBaseStream?.Invoke(this, bs);

        private int ReadFromQueue(BytesSegment bs)
        {
            if (Queue.Len == 0)
                return 0;
            var r = Math.Min(Queue.Len, bs.Len);
            Dequeue(bs, r);
            return r;
        }

        private async Task<int> WaitAndRead(BytesSegment bs)
        {
            await WaitBeforeRead;
            WaitBeforeRead = AwaitableWrapper.GetCompleted();
            return await ReadAsyncR(bs);
        }

        public override Task<int> ReadAsync(BytesSegment bs)
        {
            int r = ReadFromQueue(bs);
            if (r > 0)
                return NaiveUtils.GetCachedTaskInt(r);
            if (WaitBeforeRead.IsCompleted == false)
                return WaitAndRead(bs);
            return BaseStream.ReadAsync(bs);
        }

        public override Task WriteAsync(BytesSegment bs)
        {
            OnWriting(bs);
            return BaseStream.WriteAsync(bs);
        }

        public AwaitableWrapper<int> ReadAsyncR(BytesSegment bs)
        {
            int r = ReadFromQueue(bs);
            if (r > 0)
                return new AwaitableWrapper<int>(r);
            if (WaitBeforeRead.IsCompleted == false)
                return new AwaitableWrapper<int>(WaitAndRead(bs));
            return BaseStream.ReadAsyncR(bs);
        }

        public AwaitableWrapper WriteAsyncR(BytesSegment bs)
        {
            OnWriting(bs);
            return BaseStream.WriteAsyncR(bs);
        }

        public AwaitableWrapper<BytesSegment> ReadNBAsyncR(int maxSize)
        {
            if (Queue.Len > 0) {
                var len = Math.Min(Queue.Len, maxSize);
                var bs = BufferPool.GlobalGetBs(len);
                Dequeue(bs, len);
                return new AwaitableWrapper<BytesSegment>(bs);
            }
            if (WaitBeforeRead.IsCompleted == false)
                return new AwaitableWrapper<BytesSegment>(ReadNBAsyncR_Wait(maxSize));
            if (BaseStream is IMyStreamNoBuffer nb) {
                return nb.ReadNBAsyncR(maxSize);
            } else {
                return ReadNBAsyncRWrapper(maxSize);
            }
        }

        private async Task<BytesSegment> ReadNBAsyncR_Wait(int maxSize)
        {
            await WaitBeforeRead;
            WaitBeforeRead = AwaitableWrapper.GetCompleted();
            return await ReadNBAsyncR(maxSize);
        }

        ReusableAwaiter<BytesSegment> _nb_ra;
        AwaitableWrapper<int> _nb_awaiter;
        BytesSegment _nb_bs;
        Action _nb_continuation;

        private AwaitableWrapper<BytesSegment> ReadNBAsyncRWrapper(int maxSize)
        {
            if (_nb_ra == null)
                _nb_ra = new ReusableAwaiter<BytesSegment>();
            else
                _nb_ra.Reset();
            var bs = BufferPool.GlobalGetBs(maxSize);
            var awaiter = BaseStream.ReadAsyncR(bs);
            if (awaiter.IsCompleted) {
                _nb_ra.SetResult(bs.Sub(0, awaiter.GetResult()));
            } else {
                _nb_bs = bs;
                if (_nb_continuation == null)
                    _nb_continuation = () => {
                        var _bs = _nb_bs;
                        var _awaiter = _nb_awaiter;
                        _nb_bs.ResetSelf();
                        _nb_awaiter = default(AwaitableWrapper<int>);
                        int r;
                        try {
                            r = _awaiter.GetResult();
                        } catch (Exception e) {
                            _nb_ra.SetException(e);
                            return;
                        }
                        _nb_ra.SetResult(_bs.Sub(0, r));
                    };
                _nb_awaiter = awaiter;
                awaiter.OnCompleted(_nb_continuation);
            }
            return new AwaitableWrapper<BytesSegment>(_nb_ra);
        }

        private void Dequeue(BytesSegment bs, int len)
        {
            Queue.CopyTo(bs, len);
            Queue.SubSelf(len);
            if (Queue.Len == 0)
                Queue.ResetSelf();
        }

        public override string ToString()
        {
            if (Queue.Len > 0)
                return $"{{Queued={Queue.Len} {BaseStream}}}";
            return BaseStream.ToString();
        }
    }
}

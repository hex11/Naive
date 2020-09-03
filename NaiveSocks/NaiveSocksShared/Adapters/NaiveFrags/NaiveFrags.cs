using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks.NaiveFrags
{
    struct Header
    {
        public Guid SessionId;
        public int StreamId;
        public int Sequence;
        public int Length;

        public const int PACKED_LENGTH = 16 + 4 + 4 + 4;

        public void Pack(BytesSegment buf, ref int idx)
        {
            SessionId.ToByteArray().CopyTo(buf.Bytes, buf.Offset + idx);
            idx += 16;
            PackInt(buf, ref idx, StreamId);
            PackInt(buf, ref idx, Sequence);
            PackInt(buf, ref idx, Length);
        }

        public void Unpack(BytesSegment buf, ref int idx)
        {
            SessionId = new Guid(buf.Sub(idx, 16).GetBytes());
            idx += 16;
            StreamId = UnpackInt(buf, ref idx);
            Sequence = UnpackInt(buf, ref idx);
            Length = UnpackInt(buf, ref idx);
        }

        void PackInt(BytesSegment buf, ref int idx, int val)
        {
            for (int i = 4 - 1; i >= 0; i--)
                buf[idx++] = (byte)(val >> (i * 8));
        }

        int UnpackInt(BytesSegment buf, ref int idx)
        {
            int r = 0;
            for (int i = 4 - 1; i >= 0; i--)
                r |= buf[idx++] << (i * 8);
            return r;
        }
    }


    class NaiveFragsOptions
    {
        public byte[] KeyExtracted;
        public byte[] RequestTemplate;
        public byte[] ResponseTemplate;
    }

    class NaiveFragsHub
    {
        public NaiveFragsOptions Options { get; }

        Dictionary<Guid, NFSession> Sessions { get; } = new Dictionary<Guid, NFSession>();

        public NaiveFragsHub(NaiveFragsOptions options)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
        }

        NFSession GetSessionFromId(Guid id)
        {
            Sessions.TryGetValue(id, out var s);
            return s;
        }

        public async Task Handle(IMyStream stream)
        {
            var encStream = GetEncryptStream(stream);
            var header = await ReadHeader(stream, Options.RequestTemplate, encStream);
        }

        private IvEncryptStream GetEncryptStream(IMyStream stream)
        {
            return new IvEncryptStream(stream,
                new ChaCha20IetfEncryptor(Options.KeyExtracted),
                new ChaCha20IetfEncryptor(Options.KeyExtracted));
        }

        private async Task<Header> ReadHeader(IMyStream stream, byte[] template, IvEncryptStream encStream)
        {
            using (var h = BufferPool.GlobalGetHandle(template.Length))
            {
                var buf = h.Buffer;
                var pos = 0;
                while (pos < template.Length)
                {
                    var read = await stream.ReadAsyncR(buf.Sub(pos));
                    if (read == 0) throw new DisconnectedException("Unexpected EOF");
                    for (int i = 0; i < read; i++)
                    {
                        if (template[pos + i] != buf[pos + i])
                            throw new Exception("Connection header doesn't match template.");
                    }
                    pos += read;
                }
            }

            using (var h = BufferPool.GlobalGetHandle(Header.PACKED_LENGTH))
            {
                var buf = h.Buffer;
                await encStream.ReadFullAsyncR(buf);
                var header = new Header();
                var idx = 0;
                header.Unpack(buf, ref idx);
                return header;
            }
        }
    }

    class NFSession
    {
        public Guid Id { get; }

        Dictionary<int, NFStream> Streams { get; } = new Dictionary<int, NFStream>();

        public NFSession(Guid id)
        {
            this.Id = id;
        }
    }

    class NFStream : IMyStream
    {
        public MyStreamState State { get; private set; } = MyStreamState.Open;

        readonly MyQueue<BytesSegment> queue = new MyQueue<BytesSegment>();

        int readSeq = 0, writeSeq = 0;

        ReusableAwaiter<VoidType> notifyRead = new ReusableAwaiter<VoidType>();

        internal void PutFrag(Header header, BytesSegment buffer)
        {
            int relSeq;
            lock (queue)
            {
                relSeq = header.Sequence - readSeq;
                while (queue.Count <= relSeq)
                    queue.Enqueue(new BytesSegment());
                queue.SetAt(relSeq, buffer);
            }
            if (relSeq == 0)
                notifyRead.TrySetResult(0);
        }

        int TryReadNonblocking(BytesSegment bs)
        {
            var r = 0;
            while (true)
            {
                if (queue.Count == 0) return r == 0 ? -1 : r;
                var q = queue.Peek();
                if (q.Bytes == null) return r == 0 ? -1 : r;
                var len = Math.Min(q.Len, bs.Len);
                q.CopyTo(bs, len);
                q.SubSelf(len);
                if (q.Len == 0) queue.Dequeue();
                bs.SubSelf(len);
                r += len;
                if (bs.Len == 0) return r;
            }
        }

        public async Task<int> ReadAsync(BytesSegment bs)
        {
            lock (queue)
            {
                var r = TryReadNonblocking(bs);
                if (r >= 0) return r;
                notifyRead.Reset();
            }
            await notifyRead;
            var r2 = TryReadNonblocking(bs);
            if (r2 < 0) throw new Exception("TryReadNonblocking returns " + r2);
            return r2;
        }

        public Task WriteAsync(BytesSegment bs)
        {
            throw new NotImplementedException();
        }

        public Task FlushAsync() => AsyncHelper.CompletedTask;

        public Task Shutdown(SocketShutdown direction)
        {
            throw new NotImplementedException();
        }

        public Task Close()
        {
            throw new NotImplementedException();
        }
    }

    class NFBaseConnection
    {

    }
}

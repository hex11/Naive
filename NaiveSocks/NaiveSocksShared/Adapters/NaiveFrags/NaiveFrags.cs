using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.IO;
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

        Dictionary<Guid, NFServerSession> Sessions = new Dictionary<Guid, NFServerSession>();

        public NaiveFragsHub(NaiveFragsOptions options)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
        }

        NFServerSession GetSessionFromId(Guid id)
        {
            Sessions.TryGetValue(id, out var s);
            return s;
        }

        public async Task Handle(IMyStream stream)
        {
            await ReadHeader(stream, Options.RequestTemplate);
        }

        private async Task ReadHeader(IMyStream stream, byte[] template)
        {
            var buf = BufferPool.GlobalGetBs(template.Length);
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
    }

    class NFServerSession
    {
        public Guid Id { get; }

        public NFServerSession(Guid id)
        {
            this.Id = id;
        }
    }
}

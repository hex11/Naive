using System;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Collections.Generic;
using System.Linq;

/*

This is a message stream protocol with encrypted frame/msg headers.

Frame format:
    encrypt(
        [length_of_payload (3 bytes)]
        + [flags (1 byte)]
    ) + encrypt([payload])

TODO: authenticated encryption?

*/

namespace NaiveSocks
{
    public class NaiveMsgStream : IMsgStream
    {
        public IMyStream BaseStream { get; }

        public FilterBase Filters { get; } = new FilterBase();

        public MsgStreamStatus State { get; private set; }

        public NaiveMsgStream(IMyStream baseStream)
        {
            this.BaseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        }

        public Task SendMsg(Msg msg)
        {
            return SendImpl(msg, 0);
        }

        private async Task SendImpl(Msg msg, byte flags)
        {
            var header = BufferPool.GlobalGetBs(4);
            int hPos = 0;
            int len = msg.Data.len;
            if (len > 0x00ffffff) throw new ArgumentOutOfRangeException("msg", "msg is too long!");
            for (int i = 3 - 1; i >= 0; i--)
                header[hPos++] = (byte)((len >> (i * 8)) & (0xff));
            header[hPos++] = flags;

            var bv = new BytesView(header.Bytes, header.Offset, header.Len);

            Filters.OnWrite(bv);
            if (bv.len != hPos) throw new Exception("filters are not allowed to change data size");

            Filters.OnWrite(msg.Data);
            if (msg.Data.len != len) throw new Exception("filters are not allowed to change data size");

            bv.nextNode = msg.Data;

            await BaseStream.WriteMultipleAsyncR(bv);
        }

        public async Task<Msg> RecvMsg(BytesView buf)
        {
            var frame = await RecvImpl();
            return new Msg(frame.Payload);
        }

        private async Task<Frame> RecvImpl()
        {
            var header = BufferPool.GlobalGetBs(4).ToBytesView();
            await BaseStream.ReadFullAsyncR(header.Segment);
            Filters.OnRead(header);
            int hPos = 0;
            int len = 0;
            for (int i = 3 - 1; i >= 0; i--)
                len |= header[hPos++] << (i * 8);
            int flags = header[hPos++];
            var payload = BufferPool.GlobalGetBs(len).ToBytesView();
            await BaseStream.ReadFullAsyncR(payload.Segment);
            Filters.OnRead(payload);
            return new Frame
            {
                Flags = flags,
                Payload = payload
            };
        }

        public Task Close(CloseOpt closeOpt)
        {
            if (closeOpt.CloseType == CloseType.Close)
            {
                return BaseStream.Close();
            }
            else if (closeOpt.CloseType == CloseType.Shutdown)
            {
                return BaseStream.Shutdown(closeOpt.ShutdownType);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(closeOpt));
            }
        }

        private struct Frame
        {
            public int Flags;
            public BytesView Payload;
        }
    }
}
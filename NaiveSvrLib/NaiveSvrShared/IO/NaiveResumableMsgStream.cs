using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NaiveSocks
{
    // TODO
    public class NaiveResumableMsgStream : IMsgStream
    {
        public MsgStreamStatus State { get; private set; }

        public IMsgStream BaseStream { get; private set; }

        public int ReceivedPacketsCount { get; private set; }

        public int SentPacketsCount { get; private set; }

        public int AckedPacketsCount { get; private set; }

        private MyQueue<SeqMsg> unackedPackets;

        private int sendingWindow = 1024 * 1024;

        private AutoResetEvent sendingWindowEvent = new AutoResetEvent(false);

        private struct SeqMsg
        {
            public int Seq;
            public int Len;
            public Msg Msg;
        }

        enum Opcode : byte
        {
            Data = 0,
            AckOneMessage = 1,
            AckMessages = 2,
        }

        public Task Close(CloseOpt closeOpt)
        {
            throw new NotImplementedException();
        }

        public async Task<Msg> RecvMsg(BytesView buf)
        {
            while (true) {
                Msg msg = Msg.EOF;
                await WaitBaseStream();
                try {
                    msg = await BaseStream.RecvMsg(buf);
                } catch (Exception) {
                }
                if (msg.IsEOF) {
                    OnBaseStreamFailed();
                    continue;
                }
                var data = msg.Data;
                var opcode = (Opcode)data[0];
                if (opcode == Opcode.Data) {
                    ReceivedPacketsCount++;
                    SendAck();
                    return msg;
                } else if (opcode == Opcode.AckOneMessage) {
                    AckedPacketsCount++;
                    var acked = unackedPackets.Dequeue();
                    SendingWindowAdd(acked.Len);
                } else if (opcode == Opcode.AckMessages) {
                    int seq = BitConverter.ToInt32(data.bytes, data.offset + 1);
                    int totalLen = 0;
                    while (unackedPackets.Peek().Seq <= seq) {
                        AckedPacketsCount++;
                        var acked = unackedPackets.Dequeue();
                        totalLen += acked.Len;
                    }
                    SendingWindowAdd(totalLen);
                } else {
                    throw new Exception("unknown opcode: " + opcode);
                }
            }
        }

        private void SendingWindowAdd(int totalLen)
        {
            Interlocked.Add(ref sendingWindow, totalLen);
            sendingWindowEvent.Set();
        }

        void SendAck()
        {
            BaseStream.SendMsg(new Msg(new byte[] { 1 }));
        }

        public async Task SendMsg(Msg msg)
        {
            if (msg.Data == null)
                throw new ArgumentException();

            int len = msg.Data.tlen;

            if (Interlocked.Add(ref sendingWindow, -len) < 0) {
                do {
                    await sendingWindowEvent.AsTask();
                } while (sendingWindow < 0);
            }

            var seqMsg = new SeqMsg() { Msg = msg, Len = len, Seq = SentPacketsCount++ };
            unackedPackets.Enqueue(seqMsg);

            await WaitBaseStream();
            bool ok = false;
            try {
                await BaseStream.SendMsg(new BytesView(new byte[] { (byte)Opcode.Data }) { nextNode = msg.Data });
                ok = true;
            } catch (Exception) {
            }
        }

        public void Bind(IMsgStream baseStream)
        {
            if (baseStream == null)
                throw new ArgumentNullException(nameof(baseStream));
            BaseStream = baseStream;
        }

        public void Unbind()
        {
            if (BaseStream == null)
                throw new InvalidOperationException();
            BaseStream = null;
        }

        private Task WaitBaseStream()
        {
            if (BaseStream != null)
                return NaiveUtils.CompletedTask;
            throw new NotImplementedException();
        }

        private void OnBaseStreamFailed()
        {
            throw new NotImplementedException();
        }
    }
}

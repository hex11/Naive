using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Interlocked = System.Threading.Interlocked;

namespace NaiveSocks
{
    // https://en.wikipedia.org/wiki/Inverse_multiplexer
    public class InverseMuxStream : IMsgStream
    {
        private readonly IMsgStream[] allStreams, recvStreams, sendStreams;
        private Task<Msg>[] recvTasks;
        int lastSend = -1, nextRecv = 0;

        public bool PreReadEnabled { get; set; } = true;

        public InverseMuxStream(IEnumerable<IMsgStream> baseStreams)
        {
            if (baseStreams == null)
                throw new ArgumentNullException(nameof(baseStreams));
            this.allStreams = this.sendStreams = this.recvStreams = baseStreams.ToArray();
            foreach (var item in this.allStreams) {
                if (item == null)
                    throw new ArgumentException("null item in baseStreams");
            }
        }

        public InverseMuxStream(IEnumerable<IMsgStream> recvStreams, IEnumerable<IMsgStream> sendStreams)
        {
            if (recvStreams == null)
                throw new ArgumentNullException(nameof(recvStreams));
            if (sendStreams == null)
                throw new ArgumentNullException(nameof(sendStreams));
            this.recvStreams = recvStreams.ToArray();
            foreach (var item in this.recvStreams) {
                if (item == null)
                    throw new ArgumentException("null item in recvStreams");
            }
            this.sendStreams = sendStreams.ToArray();
            foreach (var item in this.sendStreams) {
                if (item == null)
                    throw new ArgumentException("null item in sendStreams");
            }
            this.allStreams = this.recvStreams.Union(this.sendStreams).ToArray();
        }

        public MsgStreamStatus State => allStreams.Min(x => x.State);

        public Task Close(CloseOpt closeOpt)
        {
            Task[] tasks = allStreams.Select(x => x.Close(closeOpt)).ToArray();
            return Task.WhenAll(tasks);
        }

        struct recvArgs
        {
            public Task prevTask;
            public IMsgStream curStream;
        }

        public Task<Msg> RecvMsg(BytesView buf)
        {
            if (PreReadEnabled && recvTasks == null) {
                recvTasks = new Task<Msg>[recvStreams.Length];
                for (int i = 0; i < recvStreams.Length; i++) {
                    var i2 = i;
                    recvTasks[i] = Task.Run(() => recvStreams[i2].RecvMsg(null));
                }
            }
            if (nextRecv == recvStreams.Length)
                nextRecv = 0;
            var curRecv2 = nextRecv++;
            if (recvTasks != null) {
                var task = recvTasks[curRecv2];
                recvTasks[curRecv2] = (!task.IsCompleted)
                    ? NaiveUtils.RunAsyncTask(async (s) => {
                        await s.prevTask;
                        return await s.curStream.RecvMsg(null);
                    }, new recvArgs { prevTask = task, curStream = recvStreams[curRecv2] })
                    //? task.ContinueWith((t, s) => s.CastTo<IMsgStream>().RecvMsg(null), recvStreams[curRecv2], TaskContinuationOptions.ExecuteSynchronously).Unwrap()
                    : recvStreams[curRecv2].RecvMsg(null);
                return task;
            } else {
                return recvStreams[curRecv2].RecvMsg(buf);
            }
        }

        public Task SendMsg(Msg msg)
        {
            int curSend, old;
            do {
                old = this.lastSend;
                curSend = (old + 1) % sendStreams.Length;
            } while (Interlocked.CompareExchange(ref this.lastSend, curSend, old) != old);
            return sendStreams[curSend].SendMsg(msg);
        }
    }
}

using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace NaiveSocks
{
    // https://en.wikipedia.org/wiki/Inverse_multiplexer
    public class InverseMuxStream : IMsgStream
    {
        private readonly IMsgStream[] allStreams, recvStreams, sendStreams;
        private Task<Msg>[] recvTasks;
        int curSend, curRecv;
        object syncSend = new object();

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

        public Task<Msg> RecvMsg(BytesView buf)
        {
            if (PreReadEnabled && recvTasks == null) {
                recvTasks = new Task<Msg>[recvStreams.Length];
                for (int i = 0; i < recvStreams.Length; i++) {
                    var i2 = i;
                    recvTasks[i] = Task.Run(() => recvStreams[i2].RecvMsg(null));
                }
            }
            if (curRecv == recvStreams.Length)
                curRecv = 0;
            var curRecv2 = curRecv++;
            if (PreReadEnabled) {
                var task = recvTasks[curRecv2];
                recvTasks[curRecv2] = NaiveUtils.RunAsyncTask(async () => {
                    await task;
                    return await recvStreams[curRecv2].RecvMsg(null);
                });
                return task;
            } else {
                return recvStreams[curRecv2].RecvMsg(buf);
            }
        }

        public Task SendMsg(Msg msg)
        {
            lock (syncSend) {
                if (curSend == sendStreams.Length)
                    curSend = 0;
                return sendStreams[curSend++].SendMsg(msg);
            }
        }
    }
}

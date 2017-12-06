using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace NaiveSocks
{
    class AllToOne : IMsgStream
    {
        private readonly IMsgStream[] baseStreams;
        private Task<Msg>[] recvTasks;
        int streamCount => baseStreams.Length;
        int curSend, curRecv;
        object syncSend = new object();

        public bool PreReadEnabled { get; set; } = true;

        public AllToOne(IEnumerable<IMsgStream> baseStreams)
        {
            if (baseStreams == null) {
                throw new ArgumentNullException(nameof(baseStreams));
            }
            this.baseStreams = baseStreams.ToArray();
            foreach (var item in this.baseStreams) {
                if (item == null)
                    throw new Exception("null item in baseStreams");
            }
        }

        public MsgStreamStatus State => baseStreams.Min(x => x.State);

        public Task Close(CloseOpt closeOpt)
        {
            Task[] tasks = baseStreams.Select(x => x.Close(closeOpt)).ToArray();
            return Task.WhenAll(tasks);
        }

        public Task<Msg> RecvMsg(BytesView buf)
        {
            if (PreReadEnabled && recvTasks == null) {
                recvTasks = new Task<Msg>[streamCount];
                for (int i = 0; i < streamCount; i++) {
                    var i2 = i;
                    recvTasks[i] = Task.Run(() => baseStreams[i2].RecvMsg(null));
                }
            }
            Debug.Assert(curRecv <= streamCount);
            if (curRecv == streamCount)
                curRecv = 0;
            var curRecv2 = curRecv++;
            if (PreReadEnabled) {
                var task = recvTasks[curRecv2];
                recvTasks[curRecv2] = NaiveUtils.RunAsyncTask(async () => {
                    await task;
                    return await baseStreams[curRecv2].RecvMsg(null);
                });
                return task;
            } else {
                return baseStreams[curRecv2].RecvMsg(buf);
            }
        }

        public Task SendMsg(Msg msg)
        {
            lock (syncSend) {
                Debug.Assert(curSend <= streamCount);
                if (curSend == streamCount)
                    curSend = 0;
                return baseStreams[curSend++].SendMsg(msg);
            }
        }
    }
}

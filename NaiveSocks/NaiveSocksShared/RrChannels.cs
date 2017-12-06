using System;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{
    /// <summary>
    /// Request-Reply Channels
    /// </summary>
    public class RrChannels<TRequest, TReply>
    {
        public NaiveMultiplexing BaseChannels;

        public Func<ReceivedRequest, Task> Requested;

        public Converter<TRequest, Msg> RequestMsgConverter;
        public Converter<Msg, TRequest> MsgRequestConverter;

        public Converter<TReply, Msg> ReplyMsgConverter;
        public Converter<Msg, TReply> MsgReplyConverter;

        public RrChannels(IMsgStream msgStream) : this(new NaiveMultiplexing(msgStream))
        {
        }

        public RrChannels(NaiveMultiplexing baseChannels)
        {
            BaseChannels = baseChannels;
        }

        public override string ToString()
        {
            return $"{{Rr on {base.ToString()}}}";
        }

        public async Task Start()
        {
            BaseChannels.NewRemoteChannel += Channels_NewRemoteChannel;
            await BaseChannels.Start();
        }

        private void Channels_NewRemoteChannel(Channel ch)
        {
            async Task tmp()
            {
                using (ch) {
                    var req = await ch.RecvMsg(null).CAF();
                    if (req.IsEOF) // WTF?
                        return;
                    var task = Requested?.Invoke(new ReceivedRequest(MsgRequestConverter(req), ch, ReplyMsgConverter));
                    if (task != null)
                        await task.CAF();
                }
            }
            tmp().Forget();
        }

        public async Task<RequestResult> Request(TRequest req)
        {
            var ch = await BaseChannels.CreateChannel();
            try {
                var msg = RequestMsgConverter(req);
                await ch.SendMsg(msg).CAF();
                return new RequestResult(ch, MsgReplyConverter);
            } catch {
                ch.Dispose();
                throw;
            }
        }

        public async Task<TReply> RequestAndGetReply(TRequest req)
        {
            var ch = await BaseChannels.CreateChannel();
            try {
                var msg = RequestMsgConverter(req);
                await ch.SendMsg(msg).CAF();
                var replyMsg = await ch.RecvMsg(null).ThrowIfEOF().CAF();
                return MsgReplyConverter(replyMsg);
            } catch {
                ch.Dispose();
                throw;
            }
        }

        public struct ReceivedRequest
        {
            public ReceivedRequest(TRequest request, Channel ch, Converter<TReply, Msg> converter)
            {
                Value = request;
                Channel = ch;
                Converter = converter;
            }

            public TRequest Value { get; }
            public Channel Channel { get; }
            Converter<TReply, Msg> Converter { get; }

            public async Task Reply(TReply reply)
            {
                await Channel.SendMsg(Converter(reply)).CAF();
            }

            public void Dispose()
            {
                Channel.Dispose();
            }
        }

        public struct RequestResult : IDisposable
        {
            public RequestResult(Channel channel, Converter<Msg, TReply> converter) : this()
            {
                Channel = channel;
                Converter = converter;
            }

            public Channel Channel { get; }
            Converter<Msg, TReply> Converter { get; }

            //public Task<TReply> GetReply() => GetReply(false);
            public async Task<TReply> GetReply(bool keepOpen)
            {
                var msg = await Channel.RecvMsg(null).ThrowIfEOF().CAF();
                if (!keepOpen)
                    Dispose();
                return Converter(msg);
            }

            public void Dispose()
            {
                Channel.Dispose();
            }
        }
    }

    //public struct RrMsg<TRecv,TSend> : IDisposable
    //{
    //    private bool _firstMsgRead;
    //    private TRecv _firstMsg;
    //    public TRecv FirstMsg
    //    {
    //        get {
    //            if (!_firstMsgRead)
    //                _firstMsg = GetFirstMsg().GetAwaiter().GetResult();
    //            return _firstMsg;
    //        }
    //    }

    //    public Channel Channel { get; }

    //    public RrMsg(TRecv firstMsg, Channel channel, bool responseRead)
    //    {
    //        _firstMsg = firstMsg;
    //        Channel = channel;
    //        _firstMsgRead = responseRead;
    //    }

    //    public async Task<TRecv> GetFirstMsg()
    //    {
    //        if (_firstMsgRead)
    //            return _firstMsg;
    //        _firstMsg = await Channel.RecvMsg(null);
    //        return _firstMsg;
    //    }

    //    public void Dispose()
    //    {
    //        Channel.Dispose();
    //    }
    //}
}
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

        public Action<Channel> OnLocalChannelCreated;
        public Action<Channel> OnRemoteChannelCreated;

        public Func<ReceivedRequest, Task> Requested;

        public static Converter<TRequest, Msg> RequestMsgConverter;
        public static Converter<Msg, TRequest> MsgRequestConverter;

        public static Converter<TReply, Msg> ReplyMsgConverter;
        public static Converter<Msg, TReply> MsgReplyConverter;

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
                    try {
                        OnRemoteChannelCreated(ch);
                        var req = await ch.RecvMsg(null).CAF();
                        if (req.IsEOF) // WTF?
                            return;
                        var task = Requested?.Invoke(new ReceivedRequest(MsgRequestConverter(req), ch, ReplyMsgConverter));
                        if (task != null)
                            await task.CAF();
                    } catch (Exception e) {
                        Logging.exception(e, Logging.Level.Error, "RrChannels handler");
                    }
                }
            }
            tmp().Forget();
        }

        public async Task<RequestResult> Request(TRequest req)
        {
            var ch = await BaseChannels.CreateChannel();
            try {
                OnLocalChannelCreated(ch);
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
                OnLocalChannelCreated(ch);
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
}
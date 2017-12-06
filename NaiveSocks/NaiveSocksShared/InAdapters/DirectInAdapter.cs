using System.Net;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public class DirectInAdapter : InAdapter
    {
        public IPEndPoint local { get; set; }
        public AddrPort dest { get; set; }

        private Listener _listener;

        public override void Start()
        {
            _listener = new Listener(local);
            _listener.Accepted = tcpClient => {
                var epPair = EPPair.FromSocket(tcpClient.Client);
                var dataStream = MyStream.FromSocket(tcpClient.Client);
                var dest = this.dest;
                Controller.HandleInConnection(InConnection.Create(this, dest, dataStream, epPair.ToString()));
            };
            _listener.Start();
        }

        public override void Stop()
        {
            _listener.Stop();
        }

        public override string ToString() => $"{{DirectIn local={local} dest={dest}}}";
    }
}

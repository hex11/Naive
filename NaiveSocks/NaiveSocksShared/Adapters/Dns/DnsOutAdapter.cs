using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using DNS;
using DNS.Client;
using DNS.Client.RequestResolver;
using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using Naive.HttpSvr;

namespace NaiveSocks
{
    class DnsOutAdapter : OutAdapter, IConnectionHandler2
    {
        public AddrPort server { get; set; }

        public string doh { get; set; }

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            if (doh != null) {
                ctx.AddField("DoH", doh);
            } else {
                ctx.AddField(nameof(server), server);
            }
        }

        DnsClient dnsClient;

        protected override void OnStart()
        {
            base.OnStart();
            if (doh != null) {
                if (doh == "cloudflare")
                    doh = "https://1.1.1.1/dns-query";
                dnsClient = new DnsClient(new HttpsRequestResolver() { Uri = doh });
            } else {
                server = server.WithDefaultPort(53);
                dnsClient = new DnsClient(IPAddress.Parse(server.Host), server.Port);
            }
        }

        public override Task HandleTcpConnection(InConnectionTcp connection)
        {
            return HandleConnection(connection as InConnection);
        }

        public Task HandleConnection(InConnection connection)
        {
            if (connection is InConnectionDns dns) {
                return ResolveName(dns);
            } else if (connection is InConnectionTcp) {
                throw new NotSupportedException("This is just a dns client and cannot handle regular connection.");
            } else {
                throw new NotSupportedException();
            }
        }

        private async Task ResolveName(InConnectionDns cxn)
        {
            var name = cxn.Dest.Host;
            var req = dnsClient.Create();
            var domain = new Domain(name);
            req.Questions.Add(new Question(domain, cxn.RequestType != DnsRequestType.AAAA ? RecordType.A : RecordType.AAAA));
            req.OperationCode = OperationCode.Query;
            req.RecursionDesired = true;
            IResponse r;
            try {
                r = await req.Resolve();
            } catch (ResponseException e) {
                var emptyResp = DnsResponse.Empty(this);
                emptyResp.Result = ConnectResultEnum.Failed;
                emptyResp.FailedReason = e.Message;
                await cxn.SetResult(emptyResp);
                return;
            }
            if (r.ResponseCode != ResponseCode.NoError)
                Logger.warning("resolving " + name + ": server returns " + r.ResponseCode);
            int count = 0;
            foreach (var item in r.AnswerRecords) {
                if (item.Type == RecordType.A || item.Type == RecordType.AAAA) {
                    count++;
                }
            }
            if (count == 0) {
                if (r.AnswerRecords.Count == 0) {
                    Logger.warning("resolving " + name + ": no answer records");
                } else {
                    Logger.warning("resolving " + name + ": answer records without A/AAAA records");
                }
            }
            var arr = new IPAddress[count];
            int? ttl = null;
            int cur = 0;
            foreach (var item in r.AnswerRecords) {
                if (item.Type == RecordType.A || item.Type == RecordType.AAAA) {
                    arr[cur++] = ((IPAddressResourceRecord)item).IPAddress;
                    int newTtl = (int)item.TimeToLive.TotalSeconds;
                    ttl = ttl.HasValue ? Math.Min(newTtl, ttl.Value) : newTtl;
                }
            }
            var resp = new DnsResponse(this, arr) { TTL = ttl };
            await cxn.SetResult(resp);
        }
    }
}

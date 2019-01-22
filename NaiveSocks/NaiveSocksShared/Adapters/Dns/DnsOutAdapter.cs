﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using DNS;
using DNS.Client;
using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using Naive.HttpSvr;

namespace NaiveSocks
{
    class DnsOutAdapter : OutAdapter, IDnsProvider
    {
        public AddrPort server { get; set; }

        DnsClient dnsClient;

        protected override void OnStart()
        {
            base.OnStart();
            server = server.WithDefaultPort(53);
            dnsClient = new DnsClient(IPAddress.Parse(server.Host), server.Port);
        }

        public override Task HandleConnection(InConnection connection)
        {
            throw new Exception("This is just a dns client and cannot handle regular connection.");
        }

        public async Task<IPAddress[]> ResolveName(string name)
        {
            var req = dnsClient.Create();
            var domain = new Domain(name);
            req.Questions.Add(new Question(domain, RecordType.A));
            //req.Questions.Add(new Question(domain, RecordType.AAAA));
            req.OperationCode = OperationCode.Query;
            req.RecursionDesired = true;
            var r = await req.Resolve();
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
            int cur = 0;
            foreach (var item in r.AnswerRecords) {
                if (item.Type == RecordType.A || item.Type == RecordType.AAAA) {
                    arr[cur++] = ((IPAddressResourceRecord)item).IPAddress;
                }
            }
            return arr;
        }
    }
}

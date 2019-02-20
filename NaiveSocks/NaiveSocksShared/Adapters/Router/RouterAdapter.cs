using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;

namespace NaiveSocks
{
    public class RouterAdapter : OutAdapter, IDnsProvider
    {
        public bool logging { get; set; }
        public bool log_uri { get; set; }

        public List<Rule> rules { get; set; }

        public AdapterRef @default { get; set; }

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("rulesets", rules?.Count ?? 0);
            ctx.AddField("default", @default);
        }

        CancellationTokenSource ctsOnStop = new CancellationTokenSource();

        public class Rule
        {
            public string abp { get; set; }
            public string abpuri { get; set; }
            public string abpfile { get; set; }
            public string abpuri_maxage { get; set; } = "1d";
            public bool base64 { get; set; }
            public Func<ConnectArgument, bool> _abp_current_filter;

            public string eq { get; set; }
            public string wildcard { get; set; }
            public string regex { get; set; }
            public int port { get; set; }

            public string ip { get; set; }
            public int[] _ip_addr;
            public int[] _ip_masklen;

            public bool is_dns { get; set; }

            public string new_host { get; set; }
            public AddrPort new_dest { get; set; }

            public AdapterRef to { get; set; }
        }

        Logger Logg => logging ? Logger : null;

        Rule[] parsedRules;

        protected override void OnInit()
        {
            base.OnInit();
            if (rules?.Count > 0) {
                parsedRules = new Rule[rules.Count];
                for (int i = 0; i < parsedRules.Length; i++) {
                    parsedRules[i] = ParseRule(rules[i]);
                }
            }
        }

        protected override void OnStop()
        {
            base.OnStop();
            ctsOnStop.Cancel();
        }

        bool Handle(ConnectArgument x, out AdapterRef redir)
        {
            redir = null;
            if (parsedRules == null)
                return false;
            foreach (var r in parsedRules) {
                var abpFilter = r._abp_current_filter;
                bool hit = false;
                if (abpFilter != null) {
                    hit = abpFilter(x);
                }
                Match regexMatch = null;
                if (!hit)
                    hit = HandleRule(x, r, out regexMatch);
                if (hit && onConnectionHit(r, x, regexMatch, out redir)) {
                    return true;
                }
            }
            return false;
        }

        private static bool HandleRule(ConnectArgument x, Rule r, out Match regexMatch)
        {
            regexMatch = null;
            if (r.is_dns && x is DnsFakeConnectArg)
                return true;
            if (x.DestOriginalName != null && HandleRule(r, ref regexMatch, x.DestOriginalName, x.Dest.Port))
                return true;
            if (HandleRule(r, ref regexMatch, x.Dest.Host, x.Dest.Port))
                return true;
            return false;
        }

        private static bool HandleRule(Rule r, ref Match regexMatch, string host, int port)
        {
            bool hit = false;
            if (r.eq != null) {
                hit |= r.eq == host;
            }
            if (r.regex != null) {
                var match = Regex.Match(host, r.regex);
                if (match.Success) {
                    regexMatch = match;
                    hit = true;
                }
            }
            if (r.wildcard != null) {
                hit |= Wildcard.IsMatch(host, r.wildcard);
            }
            if (r.port != 0) {
                hit |= r.port == port;
            }
            if (r.ip != null && !hit) {
                hit = HandleIp(host, r);
            }
            return hit;
        }

        private static bool HandleIp(string host, Rule r)
        {
            if (r._ip_addr == null) {
                var strsplits = r.ip.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s));
                var addr = new List<int>();
                var lens = new List<int>();
                foreach (var str in strsplits) {
                    var split = str.Split('/');
                    addr.Add((int)IPAddress.Parse(split[0]).Address);
                    lens.Add(int.Parse(split[1]));
                }
                r._ip_addr = addr.ToArray();
                r._ip_masklen = lens.ToArray();
            }
            if (IPAddress.TryParse(host, out var destip)) {
                for (int i = 0; i < r._ip_addr.Length; i++) {
                    var raddr = r._ip_addr[i];
                    var rlen = r._ip_masklen[i];
                    if (destip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) {
                        if ((int)ReverseBytes((uint)destip.Address) >> (32 - rlen) == (int)ReverseBytes((uint)raddr) >> (32 - rlen)) {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private Rule ParseRule(Rule rule)
        {
            if (rule.abpfile != null || rule.abp != null || rule.abpuri != null) {
                try {
                    void load(Rule rule2, string abpString)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        try {
                            abpString = abpString ?? rule2.abp;
                            if (abpString == null) {
                                var realPath = Controller.ProcessFilePath(rule2.abpfile);
                                if (!File.Exists(realPath)) {
                                    Logger.warning($"abp file '{realPath}' does not exist.");
                                    rule._abp_current_filter = null;
                                    return;
                                }
                                abpString = File.ReadAllText(realPath, Encoding.UTF8);
                            }
                            if (rule2.base64) {
                                abpString = Encoding.UTF8.GetString(Convert.FromBase64String(abpString));
                            }
                            var isreloading = rule._abp_current_filter != null;
                            var abpProcessor = new AbpProcessor();
                            abpProcessor.Parse(abpString);
                            rule._abp_current_filter = abpProcessor.Check;
                            Logg?.info($"{(isreloading ? "re" : null)}loaded abp filter" +
                                $" {(rule2.abpfile == null ? "(inline/network)" : rule2.abpfile.Quoted())}" +
                                $" {abpProcessor.RulesCount} rules in {sw.ElapsedMilliseconds} ms");
                        } catch (Exception e) {
                            Logger.exception(e, Logging.Level.Error, "loading abp filter");
                        }
                    }

                    if (rule.abpfile != null || rule.abp != null)
                        load(rule, null);
                    if (rule.abpuri != null) {
                        UpdateAbpFile(rule, load);
                    }
                } catch (Exception e) {
                    Logger.exception(e);
                }
            }
            return rule;
        }

        private void UpdateAbpFile(Rule rule, Action<Rule, string> load, bool ignoreMaxage = false)
        {
            var abpfile = rule.abpfile;
            var filepath = Controller.ProcessFilePath(abpfile); // returns null if abpfile is null
            var fi = filepath == null ? null : new FileInfo(filepath);
            NaiveUtils.TryParseDuration(rule.abpuri_maxage, out var maxage); // maxage is zero if failed
            var uristr = rule.abpuri;
            var uri = new Uri(uristr);
            var tag = abpfile ?? uristr;
            var haveOldFile = fi?.Exists ?? false;
            var lastWriteTime = haveOldFile ? fi.LastWriteTime : DateTime.MinValue;
            DateTime nextRun;
            if ((!ignoreMaxage || maxage != TimeSpan.Zero) && haveOldFile && DateTime.Now - lastWriteTime < maxage) {
                Logger.info($"'{abpfile}' no need to update (abpuri_maxage='{rule.abpuri_maxage}')");
                nextRun = lastWriteTime + maxage;
            } else {
                nextRun = DateTime.Now + maxage;
                AsyncHelper.Run(async () => {
                    if (haveOldFile)
                        Logger.info($"'{abpfile}' is being checking update...");
                    else if (abpfile != null)
                        Logger.info($"'{abpfile}' is being downloading...");
                    else
                        Logger.info($"loading abp file from network...");
                    try {
                        var webreq = WebRequest.CreateHttp(uri);
                        webreq.AllowAutoRedirect = true;
                        if (haveOldFile) {
                            webreq.IfModifiedSince = lastWriteTime;
                        }
                        HttpWebResponse resp;
                        try {
                            resp = await webreq.GetResponseAsync() as HttpWebResponse;
                        } catch (WebException e) when (e.Response is HttpWebResponse r) {
                            resp = r;
                        }
                        if (resp.StatusCode == HttpStatusCode.OK) {
                            string abpString = null;
                            if (filepath != null) {
                                await SaveResponseToFile(filepath, resp);
                            } else {
                                abpString = await resp.GetResponseStream().ReadAllTextAsync();
                            }
                            if (haveOldFile)
                                Logger.info($"'{tag}' is updated.");
                            else
                                Logger.info($"'{tag}' is downloaded.");
                            if (resp.GetResponseHeader("Last-Modified").IsNullOrEmpty() == false) {
                                fi.LastWriteTime = resp.LastModified;
                            }
                            load(rule, abpString);
                        } else if (resp.StatusCode == HttpStatusCode.NotModified) {
                            Logger.info($"'{tag}' remote file haven't been modified.");
                        } else {
                            Logger.warning($"'{tag}' response from server: {resp.StatusCode}.");
                        }
                    } catch (Exception e) {
                        Logger.error($"'{tag}' downloading: {e.Message}");
                    }
                });
            }
            Logg.info($"'{tag}' next checking at {nextRun}");
            AsyncHelper.Run(async () => {
                await Task.Delay(nextRun - DateTime.Now, ctsOnStop.Token);
                if (!IsRunning)
                    return;
                UpdateAbpFile(rule, load, true);
            }).Forget();
        }

        private static async Task SaveResponseToFile(string filepath, HttpWebResponse resp)
        {
            string tmpPath = filepath + ".downloading";
            using (var fs = File.Open(tmpPath, FileMode.Create, FileAccess.ReadWrite)) {
                await NaiveUtils.StreamCopyAsync(resp.GetResponseStream(), fs);
            }
            if (File.Exists(filepath)) {
                File.Replace(tmpPath, filepath, null, true);
            } else {
                File.Move(tmpPath, filepath);
            }
        }

        public static uint ReverseBytes(uint value)
        {
            return (value & 0x000000FF) << 24 | (value & 0x0000FF00) << 8 |
                (value & 0x00FF0000) >> 8 | (value & 0xFF000000) >> 24;
        }

        // returns true if redirected
        private static bool onConnectionHit(Rule rule, ConnectArgument connection, Match regexMatch, out AdapterRef redirect)
        {
            AddrPort dest = connection.Dest;
            redirect = null;
            var destChanged = false;
            if (!rule.new_dest.IsDefault) {
                dest = rule.new_dest;
                destChanged = true;
            }
            if (rule.new_host != null) {
                dest.Host = rule.new_host;
                destChanged = true;
            }
            if (destChanged) {
                if (regexMatch != null) {
                    for (int i = regexMatch.Groups.Count - 1; i >= 0; i--) {
                        dest.Host = dest.Host.Replace("$" + i, regexMatch.Groups[i].Value);
                    }
                }
                connection.Dest = dest;
                connection.DestOriginalName = null;
            }
            if (rule.to != null) {
                redirect = rule.to;
                return true;
            }
            return false;
        }

        public override Task HandleConnection(InConnection connection)
        {
            var sw = Stopwatch.StartNew();
            if (Handle(connection, out var redir)) {
                connection.RedirectTo(redir);
            } else {
                connection.RedirectTo(@default);
            }
            if (logging) {
                var ms = sw.ElapsedMilliseconds;
                var dest = connection.GetDestStringWithOriginalName();
                if (log_uri && connection.Url != null) {
                    dest = connection.Url;
                }
                Logger.info($"{connection.Redirected} <- {dest} - '{connection.InAdapter?.Name}' ({ms} ms)");
            }
            return AsyncHelper.CompletedTask;
        }

        class DnsFakeConnectArg : ConnectArgument
        {
            public DnsFakeConnectArg() : base(null)
            {
            }
        }

        public Task<DnsResponse> ResolveName(DnsRequest req)
        {
            var arg = new DnsFakeConnectArg() { Dest = new AddrPort(req.Name, 53) };
            AdapterRef adapterRef;
            if (Handle(arg, out var redir)) {
                adapterRef = redir;
            } else {
                adapterRef = @default;
            }
            if (adapterRef.Adapter is FailAdapter) {
                return Task.FromResult<DnsResponse>(null);
            }
            var dnsProvider = adapterRef.Adapter as IDnsProvider;
            if (dnsProvider == null) {
                Logger.error($"{adapterRef} is not a DNS resolver.");
                return Task.FromResult<DnsResponse>(null);
            }
            return req.RedirectTo(dnsProvider);
        }
    }
}

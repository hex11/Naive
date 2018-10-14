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
            public Func<ConnectArgument, bool> _abp_current_filter;

            public string eq { get; set; }
            public string wildcard { get; set; }
            public string regex { get; set; }
            public int port { get; set; }

            public string ip { get; set; }
            public int[] _ip_addr;
            public int[] _ip_masklen;

            public bool base64 { get; set; }

            public string new_host { get; set; }
            public AddrPort new_dest { get; set; }

            public AdapterRef to { get; set; }
        }

        Logger Logg => logging ? Logger : null;

        List<Rule> parsedRules;

        protected override void OnInit()
        {
            base.OnInit();
            if (rules?.Count > 0)
                parsedRules = rules.Select(ParseRule).ToList();
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
            bool hit = false;
            regexMatch = null;
            if (r.eq != null) {
                hit |= r.eq == x.Dest.Host;
            }
            if (r.regex != null) {
                var match = Regex.Match(x.Dest.Host, r.regex);
                if (match.Success) {
                    regexMatch = match;
                    hit = true;
                }
            }
            if (r.wildcard != null) {
                hit |= Wildcard.IsMatch(x.Dest.Host, r.wildcard);
            }
            if (r.port != 0) {
                hit |= r.port == x.Dest.Port;
            }
            if (r.ip != null && !hit) {
                hit = HandleIp(x.Dest.Host, r);
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

        public bool TryParseTime(string str, out TimeSpan timeSpan)
        {
            if (double.TryParse(str, out var seconds))
                goto OK;

            var sb = new StringBuilder(3);
            for (int i = 0; i < str.Length; i++) {
                if (char.IsDigit(str[i]) || (str[i] == '-' || str[i] == '+' || str[i] == '.')) {
                    sb.Append(str[i]);
                } else {
                    if (!double.TryParse(sb.ToString(), out var num))
                        goto FAIL;

                    sb.Clear();
                    switch (str[i]) {
                    case 's':
                        seconds += num;
                        break;
                    case 'm':
                        seconds += num * 60;
                        break;
                    case 'h':
                        seconds += num * 60 * 60;
                        break;
                    case 'd':
                        seconds += num * 60 * 60 * 24;
                        break;
                    default:
                        goto FAIL;
                    }
                }
            }

            OK:
            timeSpan = TimeSpan.FromSeconds(seconds);
            return true;

            FAIL:
            timeSpan = TimeSpan.Zero;
            return false;
        }

        private void UpdateAbpFile(Rule rule, Action<Rule, string> load, bool ignoreMaxage = false)
        {
            var abpfile = rule.abpfile;
            var filepath = Controller.ProcessFilePath(abpfile); // returns null if abpfile is null
            var fi = filepath == null ? null : new FileInfo(filepath);
            TryParseTime(rule.abpuri_maxage, out var maxage); // maxage is zero if failed
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
                var dest = connection.Dest.ToString();
                if (log_uri && connection.Url != null) {
                    dest = connection.Url;
                }
                Logger.info($"{connection.Redirected} <- {dest} - '{connection.InAdapter?.Name}' ({ms} ms)");
            }
            return AsyncHelper.CompletedTask;
        }

        public Task<IPAddress[]> ResolveName(string name)
        {
            var arg = new ConnectArgument(this) { Dest = new AddrPort(name, 53) };
            AdapterRef adapterRef;
            if (Handle(arg, out var redir)) {
                adapterRef = redir;
            } else {
                adapterRef = @default;
            }
            if (adapterRef.Adapter is FailAdapter) {
                return Task.FromResult<IPAddress[]>(null);
            }
            var dnsProvider = adapterRef.Adapter as IDnsProvider;
            if (dnsProvider == null) {
                Logger.error($"{adapterRef} is not a DNS resolver.");
                return Task.FromResult<IPAddress[]>(null);
            }
            return dnsProvider.ResolveName(name);
        }
    }

    class AbpProcessor
    {
        struct SubRange
        {
            public int offset, length;

            public SubRange(int offset, int length)
            {
                this.offset = offset;
                this.length = length;
            }
        }

        struct SubString
        {
            public string str;
            public int Offset, Length;

            public bool StartsWith(string pattern)
            {
                if (pattern.Length > Length)
                    return false;
                for (int i = 0; i < pattern.Length; i++) {
                    if (str[Offset + i] != pattern[i])
                        return false;
                }
                return true;
            }

            public void AppendTo(StringBuilder sb, int start)
                => AppendTo(sb, start, Length - start);

            public void AppendTo(StringBuilder sb, int start, int count)
                => sb.Append(str, Offset + start, count);

            public bool IsNullOrWhiteSpace()
            {
                for (int i = 0; i < Length; i++) {
                    if (!char.IsWhiteSpace(str[Offset + i]))
                        return false;
                }
                return true;
            }

            public void TrimSelf()
            {
                while (Length > 0 && char.IsWhiteSpace(this[0])) {
                    Offset++;
                    Length--;
                }
                while (Length > 0 && char.IsWhiteSpace(this[Length - 1])) {
                    Length--;
                }
            }

            public bool Contains(char ch, int offset)
                => AbpProcessor.Contains(str, new SubRange(Offset + offset, Length - offset), ch);

            public char CharOrZero(int index)
            {
                if (index < 0 | index > Length)
                    return '\0';
                return this[index];
            }

            public char this[int i] => str[Offset + i];

            public string Get() => str.Substring(Offset, Length);
            public override string ToString() => Get();
        }


        // Put strings into a big string, to avoid lots of short strings in the heap.
        private string bigString;

        private List<SubRange> blackDomainList;
        private List<Regex> blackRegexUrlList;
        private List<SubRange> blackWildcardUrlList;
        private List<SubRange> whiteDomainList;
        private List<Regex> whiteRegexUrlList;
        private List<SubRange> whiteWildcardUrlList;

        private Dictionary<string, bool> resultCache;
        private ReaderWriterLockSlim resultCacheLock;

        public Logger Logg { get; set; }

        public int RulesCount { get; set; }


        public void Parse(string ruleset)
        {
            int rlen = ruleset.Length;
            int lineCount = 0;
            for (int i = 0; i < ruleset.Length; i++) {
                if (ruleset[i] == '\n')
                    lineCount++;
            }
            var bssb = new StringBuilder(ruleset.Length);
            blackDomainList = new List<SubRange>(lineCount / 2);
            blackRegexUrlList = new List<Regex>();
            blackWildcardUrlList = new List<SubRange>(lineCount / 2);
            whiteDomainList = new List<SubRange>();
            whiteRegexUrlList = new List<Regex>();
            whiteWildcardUrlList = new List<SubRange>();
            resultCache = new Dictionary<string, bool>(32);
            resultCacheLock = new ReaderWriterLockSlim();
            RulesCount = 0;
            int cur = 0;
            int lineNum = 0;
            while (cur < rlen) {
                var begin = cur;
                while (cur < rlen && ruleset[cur++] != '\n') {
                }
                var line = new SubString() {
                    str = ruleset,
                    Offset = begin,
                    Length = cur - begin
                };
                lineNum++;
                line.TrimSelf();
                if (line.Length == 0)
                    continue;
                var firstCh = line[0];
                if (firstCh == '!' || firstCh == '#' || firstCh == '[')
                    continue;
                RulesCount++;
                var ch0 = line.CharOrZero(0);
                var ch1 = line.CharOrZero(1);
                var ch2 = line.CharOrZero(2);
                var ch3 = line.CharOrZero(3);
                if (ch0 == '|') {
                    if (ch1 == '|') { // ||
                        var offset = bssb.Length;
                        var len = line.Length - 2;
                        if (line.Contains('*', 2)) {
                            // add "*." + value
                            bssb.Append("*.");
                            blackDomainList.Add(new SubRange(offset, len + 2));
                            offset += 2;
                        }
                        line.AppendTo(bssb, 2);
                        blackDomainList.Add(new SubRange(offset, len));
                    } else { // |
                        var offset = bssb.Length;
                        var len = line.Length - 1 + 1;
                        //var wc = line.Substring(1) + "*";
                        line.AppendTo(bssb, 1); bssb.Append('*');
                        blackWildcardUrlList.Add(new SubRange(offset, len));
                    }
                } else if (ch0 == '@' & ch1 == '@') {
                    if (ch2 == '|') {
                        if (ch3 == '|') { // @@||
                            var offset = bssb.Length;
                            var len = line.Length - 4;
                            if (line.Contains('*', 4)) {
                                // add "*." + value
                                bssb.Append("*.");
                                whiteDomainList.Add(new SubRange(offset, len + 2));
                                offset += 2;
                            }
                            line.AppendTo(bssb, 4);
                            whiteDomainList.Add(new SubRange(offset, len));
                        } else { // @@|
                            var offset = bssb.Length;
                            var len = line.Length - 3 + 1;
                            //var wc = line.Substring(3) + "*";
                            line.AppendTo(bssb, 4); bssb.Append('*');
                            whiteWildcardUrlList.Add(new SubRange(offset, len));
                        }
                    } else { // @@
                        if (ch2.IsValidDomainCharacter() || ch2 == '*') {
                            var offset = bssb.Length;
                            var len = line.Length - 2 + 2;
                            //var wc = "*" + line.Substring(2) + "*";
                            bssb.Append('*'); line.AppendTo(bssb, 2); bssb.Append('*');
                            whiteWildcardUrlList.Add(new SubRange(offset, len));
                        } else if (ch2 == '/') {
                            var regex = Regex.Match(line.Get(), "/(.+)/");
                            if (regex.Success) {
                                whiteRegexUrlList.Add(new Regex(regex.Groups[1].Value, RegexOptions.ECMAScript));
                            }
                        }
                    }
                } else if (ch0.IsValidDomainCharacter() || ch0 == '*') {
                    //var wc = "*" + line + "*";
                    var offset = bssb.Length;
                    var len = line.Length + 2;
                    bssb.Append('*').Append(line).Append('*');
                    blackWildcardUrlList.Add(new SubRange(offset, len));
                } else if (ch0 == '/') {
                    var regex = Regex.Match(line.Get(), "/(.+)/");
                    if (regex.Success) {
                        blackRegexUrlList.Add(new Regex(regex.Groups[1].Value, RegexOptions.ECMAScript));
                    } else {
                        goto WRONG;
                    }
                } else {
                    goto WRONG;
                }
                continue;
                WRONG:
                RulesCount--;
                Logg?.warning($"unsupported/wrong ABP filter at line {lineNum + 1}: {line.Get().Quoted()}");
            }
            bigString = bssb.ToString();
            blackDomainList.TrimExcess();
            blackRegexUrlList.TrimExcess();
            blackWildcardUrlList.TrimExcess();
            whiteDomainList.TrimExcess();
            whiteRegexUrlList.TrimExcess();
            whiteWildcardUrlList.TrimExcess();
        }

        public bool Check(ConnectArgument conn)
        {
            return Check(conn.Dest, conn.Url);
        }

        public bool Check(AddrPort dest, string url)
        {
            if (url == null) { // no cache for connections with a URL
                return CheckUncached(dest, url);
            }

            var host = dest.Host;
            if (host == null)
                throw new NullReferenceException("dest.Host == null");

            resultCacheLock.EnterReadLock();
            bool isCacheHit = resultCache.TryGetValue(host, out var cachedHit);
            resultCacheLock.ExitReadLock();

            if (isCacheHit) {
                return cachedHit;
            }

            lock (resultCache) { // to avoid multiple threads compute the same thing
                if (resultCache.TryGetValue(host, out cachedHit))
                    return cachedHit;
                var hit = CheckUncached(dest, url);
                resultCacheLock.EnterWriteLock();
                try {
                    resultCache.Add(host, hit);
                } finally {
                    resultCacheLock.ExitWriteLock();
                }
                return hit;
            }
        }

        private bool CheckUncached(AddrPort dest, string url)
        {
            var host = dest.Host;
            var u = url ?? (dest.Port == 443 ? $"https://{host}/" : $"http://{host}/");
            foreach (var item in blackDomainList) {
                if (MatchDomain(host, bigString, item))
                    goto IF_HIT;
            }
            foreach (var item in blackRegexUrlList) {
                if (MatchRegex(u, item))
                    goto IF_HIT;
            }
            foreach (var item in blackWildcardUrlList) {
                if (MatchWildcard(u, bigString, item))
                    goto IF_HIT;
            }
            return false;

            IF_HIT:
            foreach (var item in whiteDomainList) {
                if (MatchDomain(host, bigString, item))
                    return false;
            }
            foreach (var item in whiteRegexUrlList) {
                if (MatchRegex(u, item))
                    return false;
            }
            foreach (var item in whiteWildcardUrlList) {
                if (MatchWildcard(u, bigString, item))
                    return false;
            }
            return true;
        }

        static bool MatchWildcard(string input, string pattern)
        {
            return Wildcard.IsMatch(input, pattern);
        }

        static bool MatchWildcard(string input, string pattern, SubRange sub)
        {
            return Wildcard.IsMatch(input, pattern, sub.offset, sub.length);
        }

        static bool MatchRegex(string input, Regex regex)
        {
            return regex.IsMatch(input);
        }

        static bool MatchDomain(string input, string pattern, SubRange sub)
        {
            if (Contains(pattern, sub, '*')) {
                return Wildcard.IsMatch(input, pattern, sub.offset, sub.length);
            } else {
                var i = IndexOf(input, pattern, sub.offset, sub.length);
                if (i >= 0) {
                    if ((i == 0 || input[i - 1] == '.') && i + sub.length == input.Length)
                        return true;
                }
            }
            return false;
        }

        static bool Contains(string input, SubRange inputSub, char ch)
        {
            for (int i = 0; i < inputSub.length; i++) {
                if (input[inputSub.offset + i] == ch)
                    return true;
            }
            return false;
        }

        static int IndexOf(string input, string pattern, int patOffset, int patLen)
        {
            for (int i = 0; i <= input.Length - patLen; i++) {
                for (int j = 0; j < patLen; j++) {
                    if (input[i + j] != pattern[patOffset + j])
                        goto WRONG;
                }
                return i;
                WRONG:
                ;
            }
            return -1;
        }
    }
}

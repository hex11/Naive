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
    public class RouterAdapter : OutAdapter
    {
        public bool logging { get; set; }

        public List<Rule> rules { get; set; }

        public AdapterRef @default { get; set; }

        public override string ToString() => $"{{Router rulesets={rules?.Count ?? 0} default={@default}}}";

        CancellationTokenSource ctsOnStop = new CancellationTokenSource();

        public class Rule
        {
            public string abp { get; set; }
            public string abpuri { get; set; }
            public string abpfile { get; set; }
            public string abpuri_maxage { get; set; } = "1d";

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

        Action<InConnection> _handler;

        protected override void Init()
        {
            base.Init();
            _handler = ConnectionHandlerFromRules(rules);
        }

        public override void Stop()
        {
            base.Stop();
            ctsOnStop.Cancel();
        }

        Action<InConnection> ConnectionHandlerFromRules(List<Rule> rules)
        {
            if (rules == null || rules.Count == 0)
                return (x) => { };
            var parsedRules = rules.Select(ParseRule).ToList();
            return (x) => {
                foreach (var item in parsedRules) {
                    if (item is Rule r) {
                        var hit = false;
                        Match regexMatch = null;
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
                            hit = HandleIp(x, r);
                        }
                        if (hit && onConnectionHit(r, x, regexMatch)) {
                            break;
                        }
                    } else if (item is Func<InConnection, bool> a) {
                        if (a(x))
                            break;
                    } else {
                        // WTF
                        throw new Exception($"unexpected object in parsedRules {item}");
                    }
                }
            };
        }

        private static bool HandleIp(InConnection x, Rule r)
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
            if (IPAddress.TryParse(x.Dest.Host, out var destip)) {
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

        // returns Rule or Func<InConnection, bool>
        private object ParseRule(Rule rule)
        {
            if (rule.abpfile == null && rule.abp == null && rule.abpuri == null) {
                return (object)rule;
            } else {
                try {
                    Func<InConnection, bool> currentAbpFilter = null;
                    void load()
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        try {
                            var abpString = rule.abp;
                            if (abpString == null) {
                                var realPath = Controller.ProcessFilePath(rule.abpfile);
                                if (!File.Exists(realPath)) {
                                    Logger.warning($"abp file '{realPath}' does not exist.");
                                    currentAbpFilter = null;
                                }
                                abpString = File.ReadAllText(realPath, Encoding.UTF8);
                            }
                            if (rule.base64) {
                                abpString = Encoding.UTF8.GetString(Convert.FromBase64String(abpString));
                            }
                            var isreloading = currentAbpFilter != null;
                            var abpProcessor = new AbpProcessor();
                            abpProcessor.Parse(abpString);
                            currentAbpFilter = abpProcessor.Check;
                            Logg?.info($"{(isreloading ? "re" : null)}loaded abp filter" +
                                $" {(rule.abpfile == null ? "(inline/network)" : rule.abpfile.Quoted())}" +
                                $" {abpProcessor.RulesCount} rules in {sw.ElapsedMilliseconds} ms");
                        } catch (Exception e) {
                            Logger.exception(e, Logging.Level.Error, "loading abp filter");
                        }
                    }
                    if (rule.abpfile != null || rule.abp != null)
                        load();
                    if (rule.abpuri != null) {
                        UpdateAbpFile(rule, load);
                    }
                    return new Func<InConnection, bool>((x) => {
                        return (currentAbpFilter?.Invoke(x) ?? false) && onConnectionHit(rule, x);
                    });
                } catch (Exception e) {
                    Logger.exception(e);
                    return new Func<InConnection, bool>((x) => false);
                }
            }
        }

        public bool TryParseTime(string str, out decimal seconds)
        {
            seconds = 0;
            if (decimal.TryParse(str, out seconds))
                return true;

            var sb = new StringBuilder(3);
            for (int i = 0; i < str.Length; i++) {
                if (char.IsDigit(str[i]) || (str[i] == '-' || str[i] == '+' || str[i] == '.'))
                    sb.Append(str[i]);
                else {
                    decimal num;
                    if (!decimal.TryParse(sb.ToString(), out num))
                        return false;

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
                        return false;
                    }
                }
            }
            return true;
        }

        private void UpdateAbpFile(Rule rule, Action load, bool ignoreMaxage = false)
        {
            var abpfile = rule.abpfile;
            var filepath = rule.abpfile == null ? null : Controller.ProcessFilePath(abpfile);
            var maxage = TimeSpan.Zero;
            if (TryParseTime(rule.abpuri_maxage, out var sec))
                maxage = TimeSpan.FromSeconds((double)sec);
            var uristr = rule.abpuri;
            var uri = new Uri(uristr);
            var tag = abpfile ?? uristr;
            var haveOldFile = filepath != null && File.Exists(filepath);
            var fi = new FileInfo(filepath);
            var lastWriteTime = haveOldFile ? fi.LastWriteTime : DateTime.MinValue;
            DateTime nextRun;
            if (!ignoreMaxage && haveOldFile && DateTime.Now - lastWriteTime < maxage) {
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
                            if (filepath != null) {
                                await SaveResponseToFile(filepath, resp);
                            } else {
                                rule.abp = await resp.GetResponseStream().ReadAllTextAsync();
                            }
                            if (haveOldFile)
                                Logger.info($"'{tag}' is updated.");
                            else
                                Logger.info($"'{tag}' is downloaded.");
                            if (resp.GetResponseHeader("Last-Modified").IsNullOrEmpty() == false) {
                                fi.LastWriteTime = resp.LastModified;
                            }
                            load();
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
        private static bool onConnectionHit(Rule rule, InConnection connection, Match regexMatch = null)
        {
            AddrPort dest = connection.Dest;
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
                connection.RedirectTo(rule.to);
                return true;
            }
            return false;
        }

        public override Task HandleConnection(InConnection connection)
        {
            var sw = Stopwatch.StartNew();
            _handler(connection);
            if (connection.IsRedirected == false) {
                connection.RedirectTo(@default);
            }
            if (logging) {
                var ms = sw.ElapsedMilliseconds;
                Logger.info($"{connection.Redirected} <- {connection.Url ?? connection.Dest.ToString()} ({ms} ms)");
            }
            return AsyncHelper.CompletedTask;
        }
    }

    class AbpProcessor
    {
        struct Range
        {
            public int offset, length;

            public Range(int offset, int length)
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
                => AbpProcessor.Contains(str, new Range(Offset + offset, Length - offset), ch);

            public char this[int i] => str[Offset + i];

            public string Get() => str.Substring(Offset, Length);
            public override string ToString() => Get();
        }


        // Put strings into a big string, to avoid lots of short strings in the heap.
        private string bigString;

        private List<Range> blackDomainList;
        private List<Regex> blackRegexUrlList;
        private List<Range> blackWildcardUrlList;
        private List<Range> whiteDomainList;
        private List<Regex> whiteRegexUrlList;
        private List<Range> whiteWildcardUrlList;

        private Dictionary<string, bool> resultCache;

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
            blackDomainList = new List<Range>(lineCount / 2);
            blackRegexUrlList = new List<Regex>();
            blackWildcardUrlList = new List<Range>(lineCount / 2);
            whiteDomainList = new List<Range>();
            whiteRegexUrlList = new List<Regex>();
            whiteWildcardUrlList = new List<Range>();
            resultCache = new Dictionary<string, bool>(32);
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
                if (line.StartsWith("||")) {
                    var offset = bssb.Length;
                    var len = line.Length - 2;
                    if (line.Contains('*', 2)) {
                        // add "*." + value
                        bssb.Append("*.");
                        blackDomainList.Add(new Range(offset, len + 2));
                        offset += 2;
                    }
                    line.AppendTo(bssb, 2);
                    blackDomainList.Add(new Range(offset, len));
                } else if (line.StartsWith("|")) {
                    var offset = bssb.Length;
                    var len = line.Length - 1 + 1;
                    //var wc = line.Substring(1) + "*";
                    line.AppendTo(bssb, 1); bssb.Append('*');
                    blackWildcardUrlList.Add(new Range(offset, len));
                } else if (line.StartsWith("@@||")) {
                    var offset = bssb.Length;
                    var len = line.Length - 4;
                    if (line.Contains('*', 4)) {
                        // add "*." + value
                        bssb.Append("*.");
                        whiteDomainList.Add(new Range(offset, len + 2));
                        offset += 2;
                    }
                    line.AppendTo(bssb, 4);
                    whiteDomainList.Add(new Range(offset, len));
                } else if (line.StartsWith("@@|")) {
                    var offset = bssb.Length;
                    var len = line.Length - 3 + 1;
                    //var wc = line.Substring(3) + "*";
                    line.AppendTo(bssb, 4); bssb.Append('*');
                    whiteWildcardUrlList.Add(new Range(offset, len));
                } else if (line.StartsWith("@@")) {
                    if (line.Length > 2 && (line[2].IsValidDomainCharacter() || line[2] == '*')) {
                        var offset = bssb.Length;
                        var len = line.Length - 2 + 2;
                        //var wc = "*" + line.Substring(2) + "*";
                        bssb.Append('*'); line.AppendTo(bssb, 2); bssb.Append('*');
                        whiteWildcardUrlList.Add(new Range(offset, len));
                    } else if (line.Length > 2 && line[2] == '/') {
                        var regex = Regex.Match(line.Get(), "/(.+)/");
                        if (regex.Success) {
                            whiteRegexUrlList.Add(new Regex(regex.Groups[1].Value, RegexOptions.ECMAScript));
                        }
                    }
                } else if (line[0].IsValidDomainCharacter() || line[0] == '*') {
                    //var wc = "*" + line + "*";
                    var offset = bssb.Length;
                    var len = line.Length + 2;
                    bssb.Append('*').Append(line).Append('*');
                    blackWildcardUrlList.Add(new Range(offset, len));
                } else if (line[0] == '/') {
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
            bssb = null;
            blackDomainList.TrimExcess();
            blackRegexUrlList.TrimExcess();
            blackWildcardUrlList.TrimExcess();
            whiteDomainList.TrimExcess();
            whiteRegexUrlList.TrimExcess();
            whiteWildcardUrlList.TrimExcess();
        }

        public bool Check(InConnection conn)
        {
            return Check(conn.Dest, conn.Url);
        }

        public bool Check(AddrPort dest, string url)
        {
            var host = dest.Host;
            lock (resultCache) {
                bool hit = false;
                if (url != null // no cache for connections with a Url
                    || !resultCache.TryGetValue(host, out hit)) {
                    var u = url ?? (dest.Port == 443 ? $"https://{host}/" : $"http://{host}/");
                    foreach (var item in blackDomainList) {
                        hit = MatchDomain(host, bigString, item);
                        if (hit) goto IF_HIT;
                    }
                    foreach (var item in blackRegexUrlList) {
                        hit = MatchRegex(u, item);
                        if (hit) goto IF_HIT;
                    }
                    foreach (var item in blackWildcardUrlList) {
                        hit = MatchWildcard(u, bigString, item);
                        if (hit) goto IF_HIT;
                    }
                    goto IF_NOT_HIT;
                    IF_HIT:
                    foreach (var item in whiteDomainList) {
                        if (MatchDomain(host, bigString, item)) {
                            hit = false;
                            goto IF_NOT_HIT;
                        }
                    }
                    foreach (var item in whiteRegexUrlList) {
                        if (MatchRegex(u, item)) {
                            hit = false;
                            goto IF_NOT_HIT;
                        }
                    }
                    foreach (var item in whiteWildcardUrlList) {
                        if (MatchWildcard(u, bigString, item)) {
                            hit = false;
                            goto IF_NOT_HIT;
                        }
                    }
                    IF_NOT_HIT:
                    if (url == null)
                        resultCache.Add(host, hit);
                }
                return hit;
            }
        }

        static bool MatchWildcard(string input, string pattern)
        {
            return Wildcard.IsMatch(input, pattern);
        }

        static bool MatchWildcard(string input, string pattern, Range sub)
        {
            return Wildcard.IsMatch(input, pattern, sub.offset, sub.length);
        }

        static bool MatchRegex(string input, Regex regex)
        {
            return regex.IsMatch(input);
        }

        static bool MatchDomain(string input, string pattern, Range sub)
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

        static bool Contains(string input, Range inputSub, char ch)
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

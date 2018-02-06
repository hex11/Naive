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

namespace NaiveSocks
{
    public class RouterAdapter : OutAdapter
    {
        public bool logging { get; set; }

        public List<Rule> rules { get; set; }

        public AdapterRef @default { get; set; }

        public class Rule
        {
            public string abp { get; set; }
            public string abpfile { get; set; }

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

        Action<InConnection> _handler;

        protected override void Init()
        {
            base.Init();
            _handler = ConnectionHandlerFromRules(rules);
        }

        Action<InConnection> ConnectionHandlerFromRules(List<Rule> rules)
        {
            if (rules == null || rules.Count == 0)
                return (x) => { };
            var parsedRules = rules.Select((rule) => {
                // return Rule or Func<InConnection, bool>
                if (rule.abpfile == null && rule.abp == null) {
                    return (object)rule;
                } else {
                    try {
                        Stopwatch sw = Stopwatch.StartNew();
                        var abpString = rule.abp;
                        if (abpString == null) {
                            var realPath = Controller.ProcessFilePath(rule.abpfile);
                            if (!File.Exists(realPath)) {
                                Logging.warning($"{this}: abp file {realPath.Quoted()} does not exist.");
                                return new Func<InConnection, bool>((x) => false);
                            }
                            abpString = File.ReadAllText(realPath, Encoding.UTF8);
                        }
                        if (rule.base64) {
                            abpString = Encoding.UTF8.GetString(Convert.FromBase64String(abpString));
                        }
                        var parsedAbpFilter = ParseABPFilter(abpString);
                        if (logging)
                            Logging.info($"{this}: loaded abp filter {(rule.abpfile == null ? "(inline)" : rule.abpfile.Quoted())} in {sw.ElapsedMilliseconds} ms");
                        return new Func<InConnection, bool>((x) => parsedAbpFilter(x) && onConnectionHit(rule, x));
                    } catch (Exception e) {
                        Logging.exception(e);
                        return new Func<InConnection, bool>((x) => false);
                    }
                }
            }).ToList();
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
                        if (r.ip != null) {
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
                                            hit = true;
                                            break;
                                        }
                                    }
                                }
                            }
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

        Func<InConnection, bool> ParseABPFilter(string ruleset)
        {
            var ruleLines = ruleset.Split('\r', '\n');
            var blackDomainList = new List<string>();
            var blackRegexUrlList = new List<Regex>();
            var blackWildcardUrlList = new List<string>();
            var whiteDomainList = new List<string>();
            var whiteRegexUrlList = new List<Regex>();
            var whiteWildcardUrlList = new List<string>();
            var resultCache = new Dictionary<string, bool>();
            for (int i = 0; i < ruleLines.Length; i++) {
                var line = ruleLines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var firstCh = line[0];
                if (firstCh == '!' || firstCh == '#' || firstCh == '[')
                    continue;
                if (line.StartsWith("||")) {
                    blackDomainList.Add(line.Substring(2));
                } else if (line.StartsWith("|")) {
                    var wc = line.Substring(1) + "*";
                    blackWildcardUrlList.Add(wc);
                } else if (line.StartsWith("@@||")) {
                    whiteDomainList.Add(line.Substring(4));
                } else if (line.StartsWith("@@|")) {
                    var wc = line.Substring(3) + "*";
                    whiteWildcardUrlList.Add(wc);
                } else if (line.StartsWith("@@")) {
                    if (line.Length > 2 && (line[2].IsValidDomainCharacter() || line[2] == '*')) {
                        var wc = "*" + line.Substring(2) + "*";
                        whiteWildcardUrlList.Add(wc);
                    } else if (line.Length > 2 && line[2] == '/') {
                        var regex = Regex.Match(line, "/(.+)/");
                        if (regex.Success) {
                            whiteRegexUrlList.Add(new Regex(regex.Groups[1].Value, RegexOptions.ECMAScript));
                        }
                    }
                } else if (line[0].IsValidDomainCharacter() || line[0] == '*') {
                    var wc = "*" + line + "*";
                    blackWildcardUrlList.Add(wc);
                } else if (line[0] == '/') {
                    var regex = Regex.Match(line, "/(.+)/");
                    if (regex.Success) {
                        blackRegexUrlList.Add(new Regex(regex.Groups[1].Value, RegexOptions.ECMAScript));
                    }
                } else {
                    if (logging)
                        Logging.warning($"{this}: unsupported/wrong ABP filter at line {i + 1}: {line.Quoted()}");
                }
            }
            return (x) => {
                var host = x.Dest.Host;
                lock (resultCache) {
                    bool hit = false;
                    if (x.Url != null // no cache for connections with a Url
                        || !resultCache.TryGetValue(host, out hit)) {
                        var url = x.Url ?? (x.Dest.Port == 443 ? $"https://{host}/" : $"http://{host}/");
                        foreach (var item in blackDomainList) {
                            hit = MatchDomain(host, item);
                            if (hit) goto IF_HIT;
                        }
                        foreach (var item in blackRegexUrlList) {
                            hit = MatchRegex(url, item);
                            if (hit) goto IF_HIT;
                        }
                        foreach (var item in blackWildcardUrlList) {
                            hit = MatchWildcard(url, item);
                            if (hit) goto IF_HIT;
                        }
                        IF_HIT:
                        if (hit) {
                            foreach (var item in whiteDomainList) {
                                if (MatchDomain(host, item)) {
                                    hit = false;
                                    goto IF_NOT_HIT;
                                }
                            }
                            foreach (var item in whiteRegexUrlList) {
                                if (MatchRegex(url, item)) {
                                    hit = false;
                                    goto IF_NOT_HIT;
                                }
                            }
                            foreach (var item in whiteWildcardUrlList) {
                                if (MatchWildcard(url, item)) {
                                    hit = false;
                                    goto IF_NOT_HIT;
                                }
                            }
                        }
                        IF_NOT_HIT:
                        if (x.Url == null)
                            resultCache.Add(host, hit);
                    }
                    return hit;
                }
            };
        }
        // But it works!

        static bool MatchWildcard(string input, string pattern)
        {
            return Wildcard.IsMatch(input, pattern);
        }

        static bool MatchRegex(string input, Regex regex)
        {
            return regex.IsMatch(input);
        }

        static bool MatchDomain(string input, string pattern)
        {
            if (pattern.Contains("*")) {
                return Wildcard.IsMatch(input, pattern);
            } else {
                var i = input.IndexOf(pattern);
                if (i >= 0) {
                    if (input[0] == '.' || (i == 0 || input[i - 1] == '.') && i + pattern.Length == input.Length)
                        return true;
                }
            }
            return false;
        }

        public override async Task HandleConnection(InConnection connection)
        {
            var sw = Stopwatch.StartNew();
            _handler(connection);
            if (connection.IsRedirected == false) {
                connection.RedirectTo(@default);
            }
            if (logging) {
                var ms = sw.ElapsedMilliseconds;
                Logging.info($"{this}: {connection.Redirected} <- {connection.Url ?? connection.Dest.ToString()} ({ms} ms)");
            }
        }
    }
}

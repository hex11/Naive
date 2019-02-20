using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NaiveSocks
{
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
        private List<SubRange> blackWildcardDomainList;
        private List<Regex> blackRegexUrlList;
        private List<SubRange> blackWildcardUrlList;
        private List<SubRange> whiteDomainList;
        private List<SubRange> whiteWildcardDomainList;
        private List<Regex> whiteRegexUrlList;
        private List<SubRange> whiteWildcardUrlList;

        private Dictionary<string, bool> resultCache;
        private ReaderWriterLockSlim resultCacheLock;

        private Dictionary<string, ManualResetEvent> computingLocks;

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

            blackDomainList = new List<SubRange>(lineCount / 2);
            blackWildcardDomainList = new List<SubRange>();
            blackRegexUrlList = new List<Regex>();
            blackWildcardUrlList = new List<SubRange>(lineCount / 2);
            whiteDomainList = new List<SubRange>();
            whiteWildcardDomainList = new List<SubRange>();
            whiteRegexUrlList = new List<Regex>();
            whiteWildcardUrlList = new List<SubRange>();
            if (rlen > 1024) {
                resultCache = new Dictionary<string, bool>(32);
                resultCacheLock = new ReaderWriterLockSlim();
                computingLocks = new Dictionary<string, ManualResetEvent>();
            }

            var bssb = new StringBuilder(ruleset.Length);
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
                        const int lineOffset = 2;
                        var offset = bssb.Length;
                        var len = line.Length - 2;
                        if (line.Contains('*', 2)) {
                            // add "*." + value
                            bssb.Append("*.");
                            line.AppendTo(bssb, lineOffset);
                            blackWildcardDomainList.Add(new SubRange(offset, len + 2));
                            // and value without "*."
                            blackWildcardDomainList.Add(new SubRange(offset + 2, len));
                        } else {
                            line.AppendTo(bssb, lineOffset);
                            blackDomainList.Add(new SubRange(offset, len));
                        }
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
                            const int lineOffset = 4;
                            var offset = bssb.Length;
                            var len = line.Length - 4;
                            if (line.Contains('*', 4)) {
                                // add "*." + value
                                bssb.Append("*.");
                                line.AppendTo(bssb, lineOffset);
                                whiteWildcardDomainList.Add(new SubRange(offset, len + 2));
                                // and value without "*."
                                whiteWildcardDomainList.Add(new SubRange(offset + 2, len));
                            } else {
                                line.AppendTo(bssb, lineOffset);
                                whiteDomainList.Add(new SubRange(offset, len));
                            }
                        } else { // @@|
                            var offset = bssb.Length;
                            var len = line.Length - 3 + 1;
                            //var wc = line.Substring(3) + "*";
                            line.AppendTo(bssb, 3); bssb.Append('*');
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
                Logg?.warning($"unsupported/wrong ABP filter at line {lineNum}: {line.Get().Quoted()}");
            }
            bigString = bssb.ToString();
            blackDomainList.TrimExcess();
            blackWildcardDomainList.TrimExcess();
            blackRegexUrlList.TrimExcess();
            blackWildcardUrlList.TrimExcess();
            whiteDomainList.TrimExcess();
            whiteWildcardDomainList.TrimExcess();
            whiteRegexUrlList.TrimExcess();
            whiteWildcardUrlList.TrimExcess();
        }

        public bool Check(ConnectArgument conn)
        {
            return Check(conn.TryGetDestWithOriginalName(), conn.Url);
        }

        public bool Check(AddrPort dest, string url)
        {
            if (url != null || resultCache == null) { // no cache for connections with a URL
                return CheckUncached(dest, url);
            }

            var host = dest.Host;
            if (host == null)
                throw new ArgumentNullException("dest.Host");

            if (ReadCache(host, out var cachedHit)) {
                return cachedHit;
            }

            bool computing;
            ManualResetEvent compLock;
            lock (computingLocks) {
                computing = computingLocks.TryGetValue(host, out compLock);
                if (!computing) {
                    if (ReadCache(host, out cachedHit)) {
                        return cachedHit;
                    }
                    compLock = new ManualResetEvent(false);
                    computingLocks[host] = compLock;
                }
            }

            if (computing) {
                compLock.WaitOne();
                if (ReadCache(host, out cachedHit)) {
                    return cachedHit;
                } else {
                    throw new Exception("failed to get cached result");
                }
            }

            try {
                var hit = CheckUncached(dest, url);
                resultCacheLock.EnterWriteLock();
                try {
                    resultCache.Add(host, hit);
                } finally {
                    resultCacheLock.ExitWriteLock();
                }
                return hit;
            } finally {
                compLock.Set();
                lock (computingLocks) {
                    computingLocks.Remove(host);
                }
            }
        }

        private bool ReadCache(string host, out bool cachedHit)
        {
            resultCacheLock.EnterReadLock();
            var cached = resultCache.TryGetValue(host, out cachedHit);
            resultCacheLock.ExitReadLock();
            return cached;
        }

        private bool CheckUncached(AddrPort dest, string url)
        {
            var host = dest.Host;
            url = url ?? (dest.Port == 443 ? $"https://{host}/" : $"http://{host}/");
            foreach (var item in blackDomainList) {
                if (MatchDomain(host, bigString, item))
                    goto IF_HIT;
            }
            foreach (var item in blackWildcardDomainList) {
                if (MatchWildcard(host, bigString, item))
                    goto IF_HIT;
            }
            foreach (var item in blackRegexUrlList) {
                if (MatchRegex(url, item))
                    goto IF_HIT;
            }
            foreach (var item in blackWildcardUrlList) {
                if (MatchWildcard(url, bigString, item))
                    goto IF_HIT;
            }
            return false;

            IF_HIT:
            foreach (var item in whiteDomainList) {
                if (MatchDomain(host, bigString, item))
                    return false;
            }
            foreach (var item in whiteWildcardDomainList) {
                if (MatchWildcard(host, bigString, item))
                    return false;
            }
            foreach (var item in whiteRegexUrlList) {
                if (MatchRegex(url, item))
                    return false;
            }
            foreach (var item in whiteWildcardUrlList) {
                if (MatchWildcard(url, bigString, item))
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
            var i = IndexOf(input, pattern, sub.offset, sub.length);
            if (i >= 0) {
                if ((i == 0 || input[i - 1] == '.') && i + sub.length == input.Length)
                    return true;
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

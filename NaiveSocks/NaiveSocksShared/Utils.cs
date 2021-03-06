﻿using Naive.HttpSvr;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks
{
    public static class Utils
    {
        public static void StreamCopy(Stream from, Stream to, long size = -1, int bs = 16 * 1024)
        {
            ReadStream(from, (buf, len) => to.Write(buf, 0, len), size, bs);
        }

        public static Task StreamCopyAsync(Stream from, Stream to, long size = -1, int bs = 16 * 1024)
        {
            return ReadStreamAsync(from, (buf, len) => to.WriteAsync(buf, 0, len), size, bs);
        }

        public static void ReadStream(Stream from, Action<byte[], int> action, long size = -1, int bs = 16 * 1024)
        {
            if (size == 0)
                return;
            if (size < -1)
                throw new ArgumentOutOfRangeException(nameof(size));
            int bufferSize = (int)(size == -1 ? bs :
                size < bs ? size : bs);
            byte[] buffer = new byte[bufferSize];
            if (size == -1) {
                while (true) {
                    int read = @from.Read(buffer, 0, bufferSize);
                    if (read == 0)
                        break;
                    action(buffer, read);
                }
            } else {
                while (true) {
                    int read = @from.Read(buffer, 0, (int)(size > bufferSize ? bufferSize : size));
                    if (read == 0)
                        throw new EndOfStreamException();
                    action(buffer, read);
                    size -= read;
                    if (size <= 0)
                        return;
                }
            }
        }

        public static async Task ReadStreamAsync(Stream from, Func<byte[], int, Task> action, long size = -1, int bs = 16 * 1024)
        {
            if (size == 0)
                return;
            if (size < -1)
                throw new ArgumentOutOfRangeException(nameof(size));
            int bufferSize = (int)(size == -1 ? bs :
                size < bs ? size : bs);
            byte[] buffer = new byte[bufferSize];
            if (size == -1) {
                while (true) {
                    int read = await @from.ReadAsync(buffer, 0, bufferSize);
                    if (read == 0)
                        break;
                    await action(buffer, read);
                }
            } else {
                while (true) {
                    int read = await @from.ReadAsync(buffer, 0, (int)(size > bufferSize ? bufferSize : size));
                    if (read == 0)
                        throw new EndOfStreamException();
                    await action(buffer, read);
                    size -= read;
                    if (size <= 0)
                        return;
                }
            }
        }

        public static IPEndPoint CreateIPEndPoint(string endPoint)
        {
            return NaiveUtils.ParseIPEndPoint(endPoint);
        }

        public static void SymmetricalAction<T>(T left, T right, Action<T, T> action)
        {
            action(left, right);
            action(right, left);
        }

        public static async Task SymmetricalAction<T>(T left, T right, Func<T, T, Task> asyncAction)
        {
            await asyncAction(left, right);
            await asyncAction(right, left);
        }

        public static bool IsValidDomainCharacter(this char ch)
        {
            // match a-z, A-Z, 0-9, '.', '-'
            return (ch >= 'a' & ch <= 'z') || (ch >= 'A' & ch <= 'Z') || (ch >= '0' & ch <= '9')
                   || (ch == '.') || (ch == '-');
        }

        public static string Quoted(this string str)
        {
            return str.Quoted("'");
        }

        public static string Quoted(this string str, string quote)
        {
            return quote + str + quote;
        }

        public static string Quoted(this string str, string quoteLeft, string quoteRight)
        {
            return quoteLeft + str + quoteRight;
        }
    }

    // C# version of https://leetcode.com/problems/wildcard-matching/discuss/
    public static class Wildcard
    {
        public static bool IsMatch(string input, string pattern)
        {
            int star = -1, ss = 0, s = 0, p = 0;
            while (s < input.Length) {
                if (p < pattern.Length) {
                    if ((pattern[p] == '?') || (pattern[p] == input[s])) {
                        //advancing both pointers when (both characters match) or ('?' found in pattern)
                        //note that pattern[p] will not advance beyond its length 
                        s++;
                        p++;
                        continue;
                    } else if (pattern[p] == '*') {
                        // * found in pattern, track index of *, only advancing pattern pointer 
                        star = p++;
                        ss = s;
                        continue;
                    }
                }

                if (star != -1) {
                    //current characters didn't match, last pattern pointer was *, current pattern pointer is not *
                    //only advancing pattern pointer
                    p = star + 1;
                    s = ++ss;
                } else {
                    //current pattern pointer is not star, last patter pointer was not *
                    //characters do not match
                    return false;
                }
            }

            //check for remaining characters in pattern
            while (p < pattern.Length && pattern[p] == '*') {
                p++;
            }

            return p == pattern.Length;
        }

        public static bool IsMatch(string input, string pattern, int patOffset, int patLen)
        {
            int star = -1, ss = 0, s = 0, p = 0;
            while (s < input.Length) {
                if (p < patLen) {
                    if ((pattern[patOffset + p] == '?') || (pattern[patOffset + p] == input[s])) {
                        //advancing both pointers when (both characters match) or ('?' found in pattern)
                        //note that pattern[p] will not advance beyond its length 
                        s++;
                        p++;
                        continue;
                    } else if (pattern[patOffset + p] == '*') {
                        // * found in pattern, track index of *, only advancing pattern pointer 
                        star = p++;
                        ss = s;
                        continue;
                    }
                }

                if (star != -1) {
                    //current characters didn't match, last pattern pointer was *, current pattern pointer is not *
                    //only advancing pattern pointer
                    p = star + 1;
                    s = ++ss;
                } else {
                    //current pattern pointer is not star, last patter pointer was not *
                    //characters do not match
                    return false;
                }
            }

            //check for remaining characters in pattern
            while (p < patLen && pattern[patOffset + p] == '*') {
                p++;
            }

            return p == patLen;
        }
    }

    public static class TomlExt
    {
        public static bool TryGetValue<T>(this Nett.TomlTable table, string key, out T value)
        {
            if (table.TryGetValue(key, out var obj)) {
                value = obj.Get<T>();
                return true;
            } else {
                value = default(T);
                return false;
            }
        }

        public static T TryGetValue<T>(this Nett.TomlTable table, string key, T @default)
        {
            if (table.TryGetValue(key, out var obj)) {
                return obj.Get<T>();
            } else {
                return @default;
            }
        }
    }

    public static class Pack
    {
        // String pack format:
        //  [length (2 bytes, 0x0000..0xFFFE)][bytes]
        //  or
        //  [0xFFFF (2 bytes)][length (4 bytes)][bytes]

        public static int BytesLength(this string str, Encoding encoding)
        {
            if (str == null)
                return 2;
            var len = encoding.GetByteCount(str);
            if (len > 0xFFFE) {
                return 2 + 4 + len;
            }
            return 2 + len;
        }

        public static void ToBytes(this string str, Encoding encoding, byte[] buf, ref int cur)
        {
            var len = str != null ? encoding.GetByteCount(str) : 0;
            if (len <= 0xFFFE) {
                buf[cur++] = (byte)(len >> 8);
                buf[cur++] = (byte)len;
            } else {
                buf[cur++] = 0xff;
                buf[cur++] = 0xff;
                for (int i = 4 - 1; i >= 0; i--)
                    buf[cur++] = (byte)(len >> (i * 8));
            }
            if (str != null)
                encoding.GetBytes(str, 0, str.Length, buf, cur);
            cur += len;
        }

        public static string ParseString(Encoding encoding, byte[] buf, ref int cur)
        {
            var len = (buf[cur++] << 8) | buf[cur++];
            if (len == 0xFFFF) {
                len = 0;
                for (int i = 4 - 1; i >= 0; i--)
                    len |= buf[cur++] << (i * 8);
            }
            var str = encoding.GetString(buf, cur, len);
            cur += len;
            return str;
        }
    }

    public class Listener
    {
        public Listener(IAdapter adapter, IPEndPoint localEP) : this(adapter, new TcpListener(localEP))
        {
        }

        public Listener(IAdapter adapter, TcpListener baseListener)
        {
            Adapter = adapter;
            BaseListener = baseListener;
            if (adapter != null)
                Logger.ParentLogger = adapter.GetAdapter().Logger;
        }

        bool stopping = false;

        public IAdapter Adapter { get; }
        public TcpListener BaseListener { get; }
        public Action<TcpClient> Accepted;

        public bool LogInfo { get; set; } = false;

        public Logger Logger { get; set; } = new Logger("listener");

        public Task Run() => Run(10);

        public async Task Run(int backlog)
        {
            stopping = false;
            EndPoint ep;
            try {
                BaseListener.Server.NoDelay = true;
                BaseListener.Start(backlog);
                ep = BaseListener.LocalEndpoint;
            } catch (Exception e) {
                Logger.error($"starting on {BaseListener.LocalEndpoint}: {e.Message}");
                return;
            }
            if (LogInfo)
                Logger.info($"listening on {ep}");
            while (true) {
                TcpClient cli = null;
                try {
                    try {
                        cli = await BaseListener.AcceptTcpClientAsync().CAF();
                        NaiveUtils.ConfigureSocket(cli.Client);
                    } catch (Exception) when (stopping) {
                        if (LogInfo)
                            Logger.info($"stopped on {ep}");
                        return;
                    }
                    Accepted?.Invoke(cli);
                } catch (Exception e) {
                    Logger.exception(e, Logging.Level.Error, ep?.ToString());
                }
            }
        }

        public void Stop()
        {
            //Logging.info($"stopping listening on {BaseListener.LocalEndpoint}");
            stopping = true;
            BaseListener.Stop();
        }
    }
}

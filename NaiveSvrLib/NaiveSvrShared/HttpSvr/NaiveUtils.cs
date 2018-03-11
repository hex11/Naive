using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Net.Security;

namespace Naive.HttpSvr
{
    public static class NaiveUtils
    {
        public static readonly byte[] ZeroBytes = new byte[0];
        public static readonly UTF8Encoding UTF8Encoding = new UTF8Encoding(false);
        public static readonly Task CompletedTask = AsyncHelper.CompletedTask;

        public static readonly Random Random = new Random();

        public static byte[] GetUTF8Bytes(string str) => UTF8Encoding.GetBytes(str);

        public static readonly byte[] DoubleCRLFBytes = new byte[] { 13, 10, 13, 10 };

        public static readonly byte[] CRLFBytes = new byte[] { 13, 10 };

        public static void SplitUrl(string url, out string path, out string qstr)
        {
            var splits = url.Split(new char[] { '?' }, 2);
            path = splits[0];
            qstr = splits.Length >= 2 ? splits[1] : "";
        }

        public static NameValueCollection ParseUrlQstr(this HttpConnection p)
        {
            return HttpUtil.ParseQueryString(p.Url_qstr);
        }

        public static NameValueCollection ParsePostData(this HttpConnection p)
        {
            var data = p.ReadAllInputData();
            return HttpUtil.ParseQueryString(data);
        }

        public static string ReadAllInputData(this HttpConnection p) => p.ReadAllInputData(Encoding.UTF8);

        public static string ReadAllInputData(this HttpConnection p, Encoding encoding)
        {
            return new StreamReader(p.inputDataStream, encoding).ReadToEnd();
        }

        public static string GetHttpHeader(this HttpConnection p)
        {
            //StringBuilder sb = new StringBuilder(128);
            //sb.AppendLine($"{p.Method} {p.Url} {p.HttpVersion}");
            //foreach (var item in p.RequestHeaders) {
            //    sb.AppendLine($"{item.Key}: {item.Value}");
            //}
            //return sb.ToString();
            return p.RawRequest;
        }

        public static bool HandleIfNotModified(this HttpConnection p, string lastModified = null, string etag = null)
        {
            if (lastModified != null) {
                p.setHeader("Last-Modified", lastModified);
            }
            if (etag != null) {
                p.setHeader("Etag", etag);
            }
            p.setHeader("Cache-Control", "public,max-age=3600");
            var notmod =
                (etag != null && p.GetReqHeader("If-None-Match") == etag)
                || (lastModified != null && p.GetReqHeader("If-Modified-Since") == lastModified);
            if (notmod)
                p.ResponseStatusCode = "304 Not Modified";
            return notmod;
        }

        public static Task HandleDirectoryAsync(HttpConnection p, string dirpath, bool allowListDir = true)
        {
            return HandleDirectoryAsync(p, dirpath, HandleFileAsync, allowListDir);
        }

        public static async Task HandleDirectoryAsync(HttpConnection p, string dirpath, Func<HttpConnection, string, Task> hitFile = null, bool allowListDir = true)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));
            if (dirpath == null)
                throw new ArgumentNullException(nameof(dirpath));

            var path = Path.Combine(dirpath, p.Url_path.TrimStart('/'));
            if (p.Url_path.Contains(".."))
                return;
            if (File.Exists(path)) {
                p.Handled = true;
                p.ResponseStatusCode = "200 OK";
                await hitFile(p, path);
            } else if (Directory.Exists(path) && allowListDir) {
                p.Handled = true;
                p.ResponseStatusCode = "200 OK";
                if (p.Url_path != "/") {
                    await p.writeLineAsync("<a href=\"../\">../</a><br/>");
                }
                foreach (var item in Directory.EnumerateDirectories(path)) {
                    var name = new DirectoryInfo(item).Name;
                    await p.writeLineAsync($"<a href=\"{HttpUtil.UrlEncode(name)}/\">{HttpUtil.HtmlAttributeEncode(name)}/</a><br/>");
                }
                foreach (var item in Directory.EnumerateFiles(path)) {
                    var name = new FileInfo(item).Name;
                    await p.writeLineAsync($"<a href=\"{HttpUtil.UrlEncode(name)}\">{HttpUtil.HtmlAttributeEncode(name)}</a><br/>");
                }
            } else {
                //p.ResponseStatusCode = "404 Not Found";
                //p.writeLine("file not found");
            }
        }

        public static async Task HandleFileAsync(HttpConnection p, string path)
        {
            FileInfo fi = new FileInfo(path);
            using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                var type = GetHttpContentTypeFromPath(path);
                if (type != null) {
                    p.setHeader(HttpHeaders.KEY_Content_Type, type);
                } else {
                    p.setHeader(HttpHeaders.KEY_Content_Type, "application/octet-stream");
                }
                if (p.HandleIfNotModified(fi.LastWriteTimeUtc.ToString("R")))
                    return;
                await NaiveUtils.HandleFileStreamAsync(p, fs, (type == null ? fi.Name : null), false);
            }
        }

        struct KeyValueStringPair
        {
            public KeyValueStringPair(string key, string value)
            {
                Key = key;
                Value = value;
            }

            public string Key;
            public string Value;
        }

        static KeyValueStringPair[] HttpContentTypes = new[] {
            new KeyValueStringPair(".html", "text/html; charset=UTF-8"),
            new KeyValueStringPair(".htm", "text/html; charset=UTF-8"),
            new KeyValueStringPair(".css", "text/css"),
            new KeyValueStringPair(".js", "text/javascript"),
            new KeyValueStringPair(".gif", "image/gif"),
            new KeyValueStringPair(".png", "image/png"),
            new KeyValueStringPair(".jpeg", "image/jpeg"),
            new KeyValueStringPair(".jpg", "image/jpeg"),
            new KeyValueStringPair(".ico", "image/x-icon"),
            new KeyValueStringPair(".txt", "text/plain"),
            new KeyValueStringPair(".json", "application/json"),
            new KeyValueStringPair(".mp4", "video/mp4"),
        };

        public static string GetHttpContentTypeFromPath(string path)
        {
            foreach (var item in HttpContentTypes) {
                if (path.EndsWith(item.Key))
                    return item.Value;
            }
            return null;
        }

        public static IPEndPoint ParseIPEndPoint(string endPoint)
        {
            var addrPort = AddrPort.Parse(endPoint);
            string[] ep = endPoint.Split(':');
            if (ep.Length < 2) throw new FormatException("Invalid endpoint format");
            if (!IPAddress.TryParse(addrPort.Host, out var ip)) {
                throw new FormatException("Invalid IP address");
            }
            return new IPEndPoint(ip, addrPort.Port);
        }

        public static Task HandleFileDownloadAsync(HttpConnection p, string path)
            => HandleFileDownloadAsync(p, path, new FileInfo(path));
        public static Task HandleFileDownloadAsync(HttpConnection p, string path, FileInfo fileInfo)
            => HandleFileDownloadAsync(p, path, fileInfo.Name);
        public static async Task HandleFileDownloadAsync(HttpConnection p, string path, string fileName)
        {
            p.Handled = true;
            p.outputStream.CurrentCompressionType = CompressionType.None;
            if (File.Exists(path) == false) {
                p.ResponseStatusCode = "404 Not Found";
                return;
            }
            using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                p.ResponseStatusCode = "200 OK";
                p.ResponseHeaders[HttpHeaders.KEY_Content_Type] = "application/octet-stream";
                p.ResponseHeaders["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
                await HandleSeekableStreamAsync(p, fs);
            }
        }

        public static async Task HandleFileStreamAsync(this HttpConnection p, Stream fs, string fileName, bool setContentType)
        {
            if (setContentType)
                p.ResponseHeaders[HttpHeaders.KEY_Content_Type] = "application/octet-stream";
            if (fileName != null)
                p.ResponseHeaders["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
            await HandleSeekableStreamAsync(p, fs);
        }

        public static void HandleSeekableStream(this HttpConnection p, Stream fs)
            => HandleSeekableStreamAsync(p, fs).RunSync();

        private static readonly Regex regexBytes = new Regex(@"bytes=(\d+)-(\d+)?");
        public static async Task HandleSeekableStreamAsync(this HttpConnection p, Stream stream)
        {
            var fileLength = stream.Length;
            long beginpos = 0;
            long endpos = fileLength - 1;
            p.ResponseHeaders["Accept-Ranges"] = "bytes";
            if (p.RequestHeaders["Range"] is string ranges) {
                var match = regexBytes.Match(ranges);
                beginpos = Convert.ToInt64(match.Groups[1].Value);
                if (match.Groups[2].Success) {
                    endpos = Convert.ToInt64(match.Groups[2].Value);
                }
                p.ResponseStatusCode = "206 Partial Content";
                p.ResponseHeaders["Content-Range"] = $"bytes {beginpos}-{endpos}/{fileLength}";
            }
            long realLength = endpos - beginpos + 1;
            if (p.outputStream.CurrentCompressionType == CompressionType.None) {
                await p.outputStream.SwitchToKnownLengthModeAsync(realLength);
            } else {
                await p.outputStream.SwitchToChunkedModeAsync();
            }
            stream.Position = beginpos;
            await StreamCopyAsync(from: stream, to: p.outputStream, size: realLength, bs: 64 * 1024);
        }

        public static void StreamToStream(Stream from, Stream to, long size = -1, int bs = 16 * 1024)
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

        public static async Task<byte[]> ReadBytesUntil(Stream stream, byte[] pattern, byte[] buffer = null, int maxLength = 32 * 1024, bool withPattern = true)
        {
            // Notice: this implementation may not work properly when the pattern is not "\r\n" or "\r\n\r\n"
            // TODO: use KMP search algorithm
            var ms = new MemoryStream(128);
            int pos = 0;
            int read = 0;
            int curMatchingPos = 0;
            int patternLength = pattern.Length;
            if (buffer == null)
                buffer = new byte[patternLength];
            else if (buffer.Length < patternLength)
                throw new ArgumentException("buffer.Length < pattern.Length");
            while (true) {
                Debug.Assert(pos <= read);
                if (pos >= read) {
                    pos = 0;
                    read = await stream.ReadAsync(buffer, 0, patternLength - curMatchingPos).CAF();
                    if (read == 0) {
                        throw new DisconnectedException("unexpected EOF");
                    }
                }
                var ch = buffer[pos++];
                ms.WriteByte(ch);
                if (ch == pattern[curMatchingPos]) {
                    curMatchingPos++;
                    if (curMatchingPos == patternLength) {
                        break;
                    }
                } else {
                    curMatchingPos = 0;
                }
                if (ms.Length >= maxLength) {
                    throw new Exception($"input line is too long (>{maxLength}).");
                }
            }
            if (!withPattern) {
                ms.SetLength(ms.Length - patternLength);
            }
            return ms.ToArray();
        }

        public static async Task<string> ReadStringUntil(Stream stream, byte[] pattern, byte[] buffer = null, int maxLength = 32 * 1024, bool withPattern = true)
        {
            var bytes = await ReadBytesUntil(stream, pattern, buffer, maxLength, withPattern);
            return UTF8Encoding.GetString(bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void ThrowIfLessThanZero(int value, string argName)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(argName, argName + " can not less than zero");
        }

        public static void CopyBytes(byte[] src, int srcOffset, byte[] dst, int dstOffset, int count)
        {
            if (count > 4096) {
                Buffer.BlockCopy(src, srcOffset, dst, dstOffset, count);
                return;
            } else if (count > 128) {
                Array.Copy(src, srcOffset, dst, dstOffset, count);
            } else {
                for (int i = 0; i < count; i++) {
                    dst[dstOffset++] = src[srcOffset++];
                }
            }
        }

        public static unsafe void XorBytes(byte[] src, int srcOffset, byte[] dst, int dstOffset, int count)
        {
            if ((srcOffset | dstOffset | count) < 0) {
                ThrowIfLessThanZero(srcOffset, nameof(srcOffset));
                ThrowIfLessThanZero(dstOffset, nameof(dstOffset));
                ThrowIfLessThanZero(count, nameof(count));
            }
            if (src == null)
                throw new ArgumentNullException(nameof(src));
            if (src.Length < srcOffset + count)
                throw new ArgumentException($"src.Length < srcOffset + count ({src.Length} < {srcOffset} + {count})");
            if (dst == null)
                throw new ArgumentNullException(nameof(dst));
            if (dst.Length < dstOffset + count)
                throw new ArgumentException($"dst.Length < dstOffset + count ({dst.Length} < {dstOffset} + {count})");

            fixed (byte* pSrc = src)
            fixed (byte* pDst = dst) {
                XorBytesUnsafe(pSrc + srcOffset, pDst + dstOffset, count);
            }
        }

        public static unsafe void XorBytesUnsafe(byte* src, byte* dst, int count)
        {
            if (IntPtr.Size == 4) {
                XorBytes32Unsafe(src, dst, count);
                return;
            }
            const int BLOCK_SIZE = 8;
            int blockCount = count / BLOCK_SIZE;
            if (blockCount > 0) {
                var pBlockSrc = (long*)src;
                var pBlockDst = (long*)dst;
                int i = 0;
                do {
                    pBlockDst[i] ^= pBlockSrc[i];
                } while (++i < blockCount);
                int xored = blockCount * BLOCK_SIZE;
                src += xored;
                dst += xored;
                count -= xored;
            }
            for (int i = 0; i < count; i++) {
                dst[i] ^= src[i];
            }
        }

        public static unsafe void XorBytes32Unsafe(byte* src, byte* dst, int count)
        {
            const int BLOCK_SIZE = 4;
            int blockCount = count / BLOCK_SIZE;
            if (blockCount > 0) {
                var pBlockSrc = (int*)src;
                var pBlockDst = (int*)dst;
                int i = 0;
                do {
                    pBlockDst[i] ^= pBlockSrc[i];
                } while (++i < blockCount);
                int xored = blockCount * BLOCK_SIZE;
                src += xored;
                dst += xored;
                count -= xored;
            }
            for (int i = 0; i < count; i++) {
                dst[i] ^= src[i];
            }
        }

        enum ConnectingState
        {
            Resolving,
            Connecting,
            Handshake,
            ConnectingError,
            HandshakeError,
            TimedOut
        }

        public static Task<Socket> ConnectTcpAsync(AddrPort dest, int timeout)
        {
            return ConnectTcpAsync(dest, timeout, async x => x);
        }

        public static async Task<T> ConnectTcpAsync<T>(AddrPort dest, int timeout, Func<Socket, Task<T>> handshake)
        {
            var state = (int)ConnectingState.Resolving;
            TcpClient destTcp = null;
            var connectTask = NaiveUtils.RunAsyncTask(async () => {
                var addrs = await Dns.GetHostAddressesAsync(dest.Host);
                if (state != (int)ConnectingState.Resolving)
                    return default(T);
                if (addrs.Length == 0)
                    throw new Exception($"no address resolved for '{dest.Host}'");
                var addr = addrs[0];
                destTcp = new TcpClient(addr.AddressFamily);
                if (Interlocked.Exchange(ref state, (int)ConnectingState.Connecting) != (int)ConnectingState.Resolving) {
                    destTcp.Close();
                    return default(T);
                }
                try {
                    ConfigureSocket(destTcp.Client);
                    await destTcp.ConnectAsync(addr, dest.Port);
                } catch (Exception) {
                    if (Interlocked.Exchange(ref state, (int)ConnectingState.ConnectingError) == (int)ConnectingState.Connecting) {
                        destTcp.Close();
                        throw;
                    }
                    return default(T);
                }
                if (Interlocked.Exchange(ref state, (int)ConnectingState.Handshake) != (int)ConnectingState.Connecting)
                    return default(T);
                try {
                    return await handshake(destTcp.Client);
                } catch (Exception) {
                    if (Interlocked.Exchange(ref state, (int)ConnectingState.HandshakeError) == (int)ConnectingState.Handshake) {
                        destTcp.Close();
                    }
                    throw;
                }
            });
            if (timeout > 0) {
                if (await connectTask.WithTimeout(timeout)) { // if timed out
                    var originalState = Interlocked.Exchange(ref state, (int)ConnectingState.TimedOut);
                    switch (originalState) {
                    case (int)ConnectingState.Connecting:
                    case (int)ConnectingState.Handshake:
                        destTcp.Close();
                        break;
                    case (int)ConnectingState.ConnectingError:
                    case (int)ConnectingState.HandshakeError:
                        await connectTask; // to throw the exception
                        break;
                    }
                    throw new DisconnectedException($"connecting timed out (timeout={timeout}, state={(ConnectingState)originalState})");
                }
            }
            return await connectTask;
        }

        public static readonly System.Security.Authentication.SslProtocols TlsProtocols
            = System.Security.Authentication.SslProtocols.Tls12
                | System.Security.Authentication.SslProtocols.Tls11
                | System.Security.Authentication.SslProtocols.Tls;

        public static Task<Stream> ConnectTlsAsync(AddrPort dest, int timeout)
        {
            return ConnectTlsAsync(dest, timeout, TlsProtocols);
        }

        public static Task<Stream> ConnectTlsAsync(AddrPort dest, int timeout,
            System.Security.Authentication.SslProtocols protocols)
        {
            return ConnectTcpAsync(dest, timeout, async socket => {
                var tls = new NaiveSocks.TlsStream(NaiveSocks.MyStream.FromSocket(socket));
                await tls.AuthAsClient(dest.Host, protocols);
                return tls.ToStream();
            });
        }

        public static void ConfigureSocket(Socket socket)
        {
            socket.SendBufferSize = 128 * 1024;
            socket.ReceiveBufferSize = 128 * 1024;
            socket.NoDelay = true;
        }

        public class DeserializedArray : List<string>
        {
            public string Input { get; internal set; }
            public string GetOrNull(int index)
            {
                if (Count > index) {
                    return this[index];
                } else {
                    return null;
                }
            }
        }

        public static DeserializedArray TryDeserialize(string input)
        {
            try {
                return DeserializeArray(input);
            } catch (Exception) {
                return null;
            }
        }

        public static bool TryDeserializeArray(string input, out DeserializedArray output)
        {
            try {
                output = DeserializeArray(input);
                return true;
            } catch (Exception) {
                output = null;
                return false;
            }
        }

        public static DeserializedArray DeserializeArray(string input)
        {
            var list = new DeserializedArray();
            list.Input = input;
            var begin = 0;
            for (int i = 0; i < input.Length; i++) {
                var ch = input[i];
                if (ch == '|') {
                    var count = int.Parse(input.Substring(begin, i - begin));
                    list.Add(input.Substring(i + 1, count));
                    i += count + 1;
                    begin = i + 1;
                }
            }
            return list;
        }

        public static string SerializeArray(params string[] strs) => SerializeArray((IEnumerable<string>)strs);

        public static string SerializeArray(IEnumerable<string> strs)
        {
            StringBuilder sb = new StringBuilder(32);
            foreach (var item in strs) {
                sb.Append(item?.Length ?? 0);
                sb.Append('|');
                sb.Append(item);
                sb.Append('|');
            }
            if (sb.Length > 0)
                sb.Remove(sb.Length - 1, 1);
            return sb.ToString();
        }

        public static byte[] GetSha256FromString(string str)
        {
            using (var hash = SHA256.Create())
                return hash.ComputeHash(NaiveUtils.UTF8Encoding.GetBytes(str));
        }

        public static Task RunAsyncTask(Func<Task> asyncTask)
        {
            return asyncTask();
        }

        public static Task<T> RunAsyncTask<T>(Func<Task<T>> asyncTask)
        {
            return asyncTask();
        }

        public static Task SetTimeout(int timeout, Func<Task> asyncTask) => AsyncHelper.SetTimeout(timeout, asyncTask);
        public static void SetTimeout(int timeout, Action action) => AsyncHelper.SetTimeout(timeout, action);

        #region AsyncHelper

        public static void Forget(Task task)
        {
            // nothing to do
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ConfiguredTaskAwaitable CAF(Task task)
        {
            return task.ConfigureAwait(false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ConfiguredTaskAwaitable<T> CAF<T>(Task<T> task)
        {
            return task.ConfigureAwait(false);
        }

        public static void RunSync(Task task) => AsyncHelper.RunSync(task);
        public static T RunSync<T>(Task<T> task) => AsyncHelper.RunSync<T>(task);

        #endregion

        public static void Write(this Stream stream, byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        public static Task WriteAsync(this Stream stream, byte[] bytes)
        {
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }

        public static Task WriteAsync(this Stream stream, byte[] bytes, CancellationToken ct)
        {
            return stream.WriteAsync(bytes, 0, bytes.Length, ct);
        }

        public static bool IsConnectionException(this Exception e)
        {
            return e is DisconnectedException
                || (e is IOException ioe && ioe.InnerException is SocketException)
                || e is SocketException;
        }

        public static bool IsNullOrEmpty(this string str)
        {
            return string.IsNullOrEmpty(str);
        }

        public static string JoinWithoutEmptyValue(string separator, params string[] strs)
        {
            return string.Join(separator, strs.Where(x => !string.IsNullOrEmpty(x)));
        }

        public static T CastTo<T>(this object obj)
        {
            return (T)obj;
        }

        public static int ToInt(this string str)
        {
            return int.Parse(str);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Get<T>(this T[] array, int index)
        {
            return (index < 0) ? array[array.Length + index] : array[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Get<T>(this IList<T> array, int index)
        {
            return (index < 0) ? array[array.Count + index] : array[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Set<T>(this T[] array, int index, T value)
        {
            if (index < 0) {
                array[array.Length + index] = value;
            } else {
                array[index] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Set<T>(this IList<T> array, int index, T value)
        {
            if (index < 0) {
                array[array.Count + index] = value;
            } else {
                array[index] = value;
            }
        }

        public static string SafeToStr<T>(this T obj)
        {
            try {
                return obj.ToString();
            } catch (Exception e) {
                try {
                    return "!!ToString exception!! " + e;
                } catch (Exception) {
                    return "!!!ToString exception!!!";
                }
            }
        }

        public static string ReadAllText(this Stream stream)
        {
            return ReadAllText(stream, UTF8Encoding);
        }

        public static string ReadAllText(this Stream stream, Encoding encoding)
        {
            using (var sr = new StreamReader(stream, encoding)) {
                return sr.ReadToEnd();
            }
        }

        public static Task<string> ReadAllTextAsync(this Stream stream)
        {
            return ReadAllTextAsync(stream, UTF8Encoding);
        }

        public static async Task<string> ReadAllTextAsync(this Stream stream, Encoding encoding)
        {
            using (var sr = new StreamReader(stream, encoding)) {
                return await sr.ReadToEndAsync();
            }
        }

        public static byte[] ConcatBytes(byte[] bytes1, byte[] bytes2)
        {
            if (bytes1 == null)
                throw new ArgumentNullException(nameof(bytes1));
            if (bytes2 == null)
                throw new ArgumentNullException(nameof(bytes2));
            var newBytes = new byte[bytes1.Length + bytes2.Length];
            Buffer.BlockCopy(bytes1, 0, newBytes, 0, bytes1.Length);
            Buffer.BlockCopy(bytes2, 0, newBytes, bytes1.Length, bytes2.Length);
            return newBytes;
        }

        public static byte[] ConcatBytes(params byte[][] arrayOfBytes)
        {
            if (arrayOfBytes == null)
                throw new ArgumentNullException(nameof(arrayOfBytes));
            var newLength = 0;
            foreach (var item in arrayOfBytes) {
                newLength += item?.Length ?? throw new ArgumentException("null in arrayOfBytes");
            }
            var newBytes = new byte[newLength];
            var cur = 0;
            foreach (var item in arrayOfBytes) {
                Buffer.BlockCopy(item, 0, newBytes, cur, item.Length);
                cur += item.Length;
            }
            return newBytes;
        }

        static Action<int, // generation
            GCCollectionMode, // mode
            bool, // blocking
            bool // compacting
            > dotNet46_GC_Collect;

        static bool dotNet46_GC_Collect_inited;

        public static void GCCollect(Action<string> onLog)
        {
            if (!dotNet46_GC_Collect_inited) {
                dotNet46_GC_Collect_inited = true;
                Type delegateType = typeof(Action<int, GCCollectionMode, bool, bool>);
                dotNet46_GC_Collect = typeof(GC)
                    .GetMethod("Collect", delegateType.GetGenericArguments())
                    ?.CreateDelegate(delegateType) as Action<int, GCCollectionMode, bool, bool>;
                if (dotNet46_GC_Collect == null)
                    onLog("(cannot get delegate of new GC.Collect in .NET 4.6)");
            }
            string getMemStat() => $"Total Memory: {GC.GetTotalMemory(false).ToString("N0")}. ";
            onLog($"GC...  {getMemStat()}");
            try {
                if (dotNet46_GC_Collect != null)
                    dotNet46_GC_Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                else
                    GC.Collect();
                GC.WaitForFullGCComplete();
            } catch (Exception) {
            }
            onLog($"GC OK. {getMemStat()}");
        }

        public static Task<int> GetCachedTaskInt(int value)
        {
            if (value >= TaskCache.CachedIntMin && value <= TaskCache.CachedIntMax) {
                return TaskCache.cachedTaskInt[value - TaskCache.CachedIntMin];
            }
            return Task.FromResult(value);
        }

        static class TaskCache
        {
            static TaskCache()
            {
                cachedTaskInt = new Task<int>[CachedIntMax + 1 - CachedIntMin];
                for (int i = 0; i < cachedTaskInt.Length; i++) {
                    cachedTaskInt[i] = Task.FromResult(CachedIntMin + i);
                }
            }

            public const int CachedIntMin = -1;
            public const int CachedIntMax = 64;
            public static Task<int>[] cachedTaskInt;
        }
    }

    public struct EPPair
    {
        public IPEndPoint LocalEP;
        public IPEndPoint RemoteEP;

        public EPPair(IPEndPoint localEP, IPEndPoint remoteEP)
        {
            LocalEP = localEP;
            RemoteEP = remoteEP;
        }

        public static EPPair FromSocket(Socket socket)
        {
            return new EPPair() {
                LocalEP = socket.LocalEndPoint as IPEndPoint,
                RemoteEP = socket.RemoteEndPoint as IPEndPoint
            };
        }

        public override string ToString()
        {
            return $"tcp={LocalEP}<->{RemoteEP}";
        }
    }

    public class AsyncEvent : List<Func<Task>>
    {
        public async Task InvokeAsync()
        {
            foreach (var item in this) {
                await item();
            }
        }
    }

    public class AsyncEvent<T> : List<Func<T, Task>>
    {
        public async Task InvokeAsync(T arg)
        {
            foreach (var item in this) {
                await item(arg).CAF();
            }
        }
    }

    public class AsyncEvent<T1, T2> : List<Func<T1, T2, Task>>
    {
        public async Task InvokeAsync(T1 arg1, T2 arg2)
        {
            foreach (var item in this) {
                await item(arg1, arg2);
            }
        }
    }

    public static class HttpHeaders
    {
        public const string
            KEY_Connection = "Connection",
            VALUE_Connection_Keepalive = "keep-alive",
            VALUE_Connection_close = "close",
            KEY_Content_Length = "Content-Length",
            KEY_Content_Type = "Content-Type",
            KEY_Server = "Server",
            KEY_Authorization = "Authorization",
            KEY_WWW_Authenticate = "WWW-Authenticate",
            KEY_Transfer_Encoding = "Transfer-Encoding",
            VALUE_Transfer_Encoding_chunked = "chunked",
            KEY_Content_Encoding = "Content-Encoding",
            VALUE_Content_Encoding_gzip = "gzip",
            VALUE_Content_Encoding_deflate = "deflate";
    }

    public class LambdaHttpRequestHandler : IHttpRequestHandler
    {
        private HttpRequestHandler handler;

        public LambdaHttpRequestHandler(HttpRequestHandler handler)
        {
            this.handler = handler;
        }

        public void HandleRequest(HttpConnection p)
        {
            handler(p);
        }
    }

    public class LambdaHttpRequestAsyncHandler : IHttpRequestHandler, IHttpRequestAsyncHandler
    {
        private readonly HttpRequestAsyncHandler handler;

        public LambdaHttpRequestAsyncHandler(HttpRequestAsyncHandler handler)
        {
            this.handler = handler;
        }

        public void HandleRequest(HttpConnection p)
        {
            handler(p).RunSync();
        }

        public Task HandleRequestAsync(HttpConnection p)
            => handler(p);
    }

    public static class WaitHandleExtensions
    {
        public static Task AsTask(this WaitHandle handle)
        {
            return AsTask(handle, Timeout.InfiniteTimeSpan);
        }

        public static Task AsTask(this WaitHandle handle, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<VoidType>();
            var registration = ThreadPool.RegisterWaitForSingleObject(handle, (state, timedOut) => {
                var localTcs = (TaskCompletionSource<VoidType>)state;
                if (timedOut)
                    localTcs.TrySetCanceled();
                else
                    localTcs.TrySetResult(0);
            }, tcs, timeout, executeOnlyOnce: true);
            tcs.Task.ContinueWith((_, state) => ((RegisteredWaitHandle)state).Unregister(null), registration, TaskScheduler.Default);
            return tcs.Task;
        }
    }

    public class HttpClient
    {
        public Stream BaseStream { get; }

        public HttpClient(Stream baseStream)
        {
            BaseStream = baseStream;
        }

        public async Task<HttpResponse> Request(HttpRequest request)
        {
            await WriteRequest(request);
            return await ReadResponse();
        }

        public async Task<HttpResponse> ReadResponse()
        {
            var responseStr = await NaiveUtils.ReadStringUntil(BaseStream, NaiveUtils.DoubleCRLFBytes);
            return ReadHttpResponseHeader(new StringReader(responseStr));
        }

        public async Task WriteRequest(HttpRequest request)
        {
            var tw = new StringWriter();
            WriteHttpRequestHeader(tw, request);
            await BaseStream.WriteAsync(NaiveUtils.GetUTF8Bytes(tw.ToString()));
        }

        public static void WriteHttpRequestHeader(TextWriter output, HttpRequest request)
                        => WriteHttpRequestHeader(output, request.Method, request.Path, request.Headers);
        public static void WriteHttpRequestHeader(
            TextWriter output,
            string method,
            string uri,
            IDictionary<string, string> headers)
        {
            if (uri.Contains(' ')) {
                throw new Exception("spaces in uri are not allowed");
            }
            output.Write($"{method} {uri} HTTP/1.1\r\n");
            if (headers.TryGetValue("Host", out var host)) { // 'Host' first
                output.Write($"Host: {host}\r\n");
            }
            foreach (var item in headers) {
                if (item.Key == "Host")
                    continue;
                output.Write($"{item.Key}: {item.Value}\r\n");
            }
            output.Write("\r\n");
        }

        public static Task WriteHttpRequestHeaderAsync(TextWriter output, HttpRequest request)
            => WriteHttpRequestHeaderAsync(output, request.Method, request.Path, request.Headers);
        public static async Task WriteHttpRequestHeaderAsync(
            TextWriter output,
            string method,
            string uri,
            IDictionary<string, string> headers)
        {
            var tw = new StringWriter();
            WriteHttpRequestHeader(tw, method, uri, headers);
            await output.WriteAsync(tw.ToString());
        }

        public static HttpResponse ReadHttpResponseHeader(TextReader input)
        {
            var response = new HttpResponse();
            var line = input.ReadLine();
            if (line == null)
                throw new Exception("unexpected EOF");
            var splits = line.Split(new[] { ' ' }, 3);
            response.StatusCode = splits[1];
            response.ReasonPhrase = splits[2];
            var headers = new Dictionary<string, string>();
            while (!string.IsNullOrEmpty(line = input.ReadLine())) {
                splits = line.Split(new[] { ':' }, 2);
                headers.Add(splits[0], splits[1].TrimStart(' '));
            }
            if (line == null) {
                throw new Exception("unexpected EOF");
            }
            response.Headers = headers;
            return response;
        }

        public static async Task<HttpResponse> ReadHttpResponseHeaderAsync(TextReader input)
        {
            var response = new HttpResponse();
            var line = await input.ReadLineAsync();
            if (line == null)
                throw new Exception("unexpected EOF");
            var splits = line.Split(new[] { ' ' }, 3);
            response.StatusCode = splits[1];
            response.ReasonPhrase = splits[2];
            var headers = new Dictionary<string, string>();
            while (!string.IsNullOrEmpty(line = await input.ReadLineAsync())) {
                splits = line.Split(new[] { ':' }, 2);
                headers.Add(splits[0], splits[1].TrimStart(' '));
            }
            if (line == null)
                throw new Exception("unexpected EOF");
            response.Headers = headers;
            return response;
        }
    }

    public class HttpRequest
    {
        public string Method;
        public string Path;
        public IDictionary<string, string> Headers;
    }

    public class HttpResponse
    {
        public string StatusCode;
        public string ReasonPhrase;
        public IDictionary<string, string> Headers;

        public bool TestHeader(string key, string value)
        {
            return Headers.TryGetValue(key, out var val) && string.Equals(val, value, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    public struct AddrPort
    {
        public string Host;
        public int Port;

        public bool IsDefault => Host == default(string) && Port == default(int);

        public bool IsIPv6
        {
            get {
                if (Host == null)
                    return false;
                foreach (var item in Host) {
                    if (item == ':')
                        return true;
                }
                return false;
            }
        }

        public bool IsIP
        {
            get {
                if (Host == null)
                    return false;
                return IPAddress.TryParse(Host, out _);
            }
        }

        public static AddrPort Empty => new AddrPort();

        public AddrPort(string host, int port)
        {
            Host = host;
            Port = port;
        }

        public override string ToString()
        {
            if (Host?.Contains(":") == true && Host[0] != '[') {
                return $"[{Host}]:{Port}";
            }
            return $"{Host}:{Port}";
        }

        public AddrPort WithDefaultPort(int defaultPort)
        {
            return new AddrPort {
                Host = Host,
                Port = (Port > 0) ? Port : defaultPort
            };
        }

        public byte[] ToSocks5Bytes()
        {
            if (IPAddress.TryParse(Host, out var ip)) {
                var ipBytes = ip.GetAddressBytes();
                var buf = new byte[1 + ipBytes.Length + 2];
                var cur = 0;
                buf[cur++] = (byte)((ipBytes.Length == 4) ? 0x01 : 0x04);
                for (int i = 0; i < ipBytes.Length; i++) {
                    buf[cur++] = ipBytes[i];
                }
                buf[cur++] = (byte)(Port >> 8);
                buf[cur++] = (byte)Port;
                return buf;
            } else {
                var buf = new byte[1 + BytesLength];
                var cur = 0;
                buf[cur++] = 0x03; // domain name
                ToBytes(buf, ref cur);
                return buf;
            }
        }

        public static unsafe AddrPort FromSocks5Bytes(BytesView bytes)
        {
            AddrPort dest = new AddrPort();
            var cur = 0;
            var addrType = bytes[cur++];
            switch (addrType) {
            case 0x01:
            case 0x04:
                var addrLen = (addrType == 0x01) ? 4 : 16;
                var ip = new IPAddress(bytes.GetBytes(cur, addrLen));
                cur += addrLen;
                dest.Host = ip.ToString();
                break;
            case 0x03:
                var nameLen = bytes[cur++];
                if (nameLen == 0)
                    throw new Exception("length of domain name cannot be zero");
                var p = stackalloc sbyte[nameLen];
                for (int i = 0; i < nameLen; i++) {
                    var b = bytes[cur++];
                    if (b > 0x7f) {
                        throw new Exception("domain name is not an ASCII string");
                    }
                    p[i] = (sbyte)b;
                }
                dest.Host = new String(p, 0, nameLen);
                break;
            default:
                throw new Exception($"unknown addr type ({addrType})");
            }
            dest.Port = bytes[cur++] << 8;
            dest.Port |= bytes[cur++];
            return dest;
        }

        // +---------+-------------------+-------------------+
        // | a byte  |  addrlen byte(s)  |      2 bytes      |
        // +---------+-------------------+-------------------+
        // | addrlen | host addr (ascii) | port (big endian) |
        // +---------+-------------------+-------------------+

        public static AddrPort Parse(string str)
        {
            var s = str.LastIndexOf(':');
            if (s == -1) {
                //throw new FormatException("Invalid format (':' not found)");
                return new AddrPort(str, -1);
            }
            if (!int.TryParse(str.Substring(s + 1), out var port)) {
                throw new FormatException("Invalid number after ':'");
            }
            return new AddrPort {
                Host = str.Substring(0, s),
                Port = port
            };
        }

        public static AddrPort Parse(byte[] buf)
        {
            var offset = 0;
            return Parse(buf, ref offset);
        }

        public static AddrPort Parse(byte[] buf, ref int offset)
        {
            AddrPort result;
            var strlen = buf[offset++];
            char[] strbuf = new char[strlen];
            for (int i = 0; i < strlen; i++) {
                strbuf[i] = (char)buf[offset++];
            }
            result.Host = new string(strbuf);
            result.Port = buf[offset++] << 8
                          | buf[offset++];
            return result;
        }

        public byte[] ToBytes()
        {
            var dest = this;
            var buf = new byte[dest.BytesLength];
            int i = 0;
            return ToBytes(buf, ref i);
        }

        public int BytesLength => (Host?.Length ?? 0) + 3;

        public byte[] ToBytes(byte[] buf, ref int offset)
        {
            var dest = this;
            var hostlen = (byte)(dest.Host?.Length ?? 0);
            if (hostlen > 255)
                throw new Exception("host length > 255");
            buf[offset++] = hostlen;
            for (var ich = 0; ich < hostlen; ich++) {
                char ch = dest.Host[ich];
                if (ch > 127)
                    throw new Exception("illegal character in host string");
                buf[offset++] = (byte)ch;
            }
            buf[offset++] = (byte)(dest.Port >> 8);
            buf[offset++] = (byte)dest.Port;
            return buf;
        }

        public override bool Equals(object obj)
        {
            if (obj is AddrPort ap)
                return this == ap;
            return false;
        }

        public override int GetHashCode()
        {
            return Host.GetHashCode() * 23 + Port.GetHashCode();
        }

        public static bool operator ==(AddrPort a1, AddrPort a2)
        {
            return a1.Port == a2.Port && a1.Host == a2.Host;
        }

        public static bool operator !=(AddrPort a1, AddrPort a2)
        {
            return !(a1 == a2);
        }
    }

    public class ObjectPool<T> where T : class
    {
        private readonly Action<T> _resetFunc;
        private readonly Func<T> _createFunc;
        private ConcurrentStack<T> _baseContainer = new ConcurrentStack<T>();
        public int MaxCount { get; set; } = 5;

        public ObjectPool(Func<T> createFunc)
        {
            _createFunc = createFunc;
        }

        public ObjectPool(Func<T> createFunc, Action<T> resetFunc) : this(createFunc)
        {
            _resetFunc = resetFunc;
        }

        public Handle Get()
        {
            return new Handle(this, GetValue());
        }

        public T GetValue()
        {
            if (!_baseContainer.TryPop(out T obj)) {
                obj = _createFunc();
            }
            return obj;
        }

        private bool Put(T value)
        {
            if (_baseContainer.Count < MaxCount) {
                _resetFunc?.Invoke(value);
                _baseContainer.Push(value);
                return true;
            }
            return false;
        }

        public bool PutValue(T value) => Put(value);

        public class Handle : IDisposable
        {
            private readonly ObjectPool<T> _pool;
            public T Value { get; private set; }

            public Handle(ObjectPool<T> pool, T obj)
            {
                _pool = pool;
                Value = obj;
            }

            private bool _disposed;
            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _pool.Put(Value);
                Value = null;
            }
        }
    }

    public class WeakObjectPool<T> where T : class
    {
        private ObjectPool<WeakReference<T>> _basePool;
        public int MaxCount
        {
            get => _basePool.MaxCount;
            set => _basePool.MaxCount = value;
        }

        public WeakObjectPool(Func<T> createFunc)
        {
            _basePool = new ObjectPool<WeakReference<T>>(
                () => new WeakReference<T>(createFunc())
            );
        }

        public WeakObjectPool(Func<T> createFunc, Action<T> resetFunc)
        {
            _basePool = new ObjectPool<WeakReference<T>>(
                () => new WeakReference<T>(createFunc()),
                (weakRef) => {
                    if (weakRef.TryGetTarget(out var v)) {
                        resetFunc(v);
                    }
                }
            );
        }

        public Handle Get()
        {
            while (true) {
                var weakRefHandle = _basePool.Get();
                if (weakRefHandle.Value.TryGetTarget(out var obj)) {
                    return new Handle(weakRefHandle, obj);
                }
            }
        }

        public class Handle : IDisposable
        {
            private readonly ObjectPool<WeakReference<T>>.Handle _baseHandle;
            public T Value { get; private set; }

            public Handle(ObjectPool<WeakReference<T>>.Handle baseHandle, T obj)
            {
                _baseHandle = baseHandle;
                Value = obj;
            }

            public void Dispose()
            {
                _baseHandle.Dispose();
                Value = null;
            }
        }
    }

    public struct VoidType
    {
        public static readonly VoidType Void;

        public static implicit operator VoidType(int i)
        {
            return new VoidType();
        }
    }
}

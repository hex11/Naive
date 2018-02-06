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

namespace Naive.HttpSvr
{
    public class MultipartFormDataReader : ReadOnlyStream
    {
        private long _position;

        public HttpConnection p { get; }
        BackableStream bstream;
        public Stream BaseStream => bstream;

        public byte[] Boundary { get; private set; }
        public string CurrentPartName { get; private set; }
        public string CurrentPartFileName { get; private set; }
        public string CurrentPartRawHeader { get; private set; }
        public Dictionary<string, string> CurrentPartHeaders { get; } = new Dictionary<string, string>();

        public bool IsReadingPart { get; private set; }
        bool firstBoundaryRead;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get { return _position; }
            set {
                throw new NotSupportedException();
            }
        }

        public MultipartFormDataReader(HttpConnection p)
        {
            bstream = new BackableStream(p.inputDataStream);
            this.p = p;
            Boundary = GetBoundary(p);
        }

        private static byte[] GetBoundary(HttpConnection p)
        {
            const string boundary = "boundary=";
            var contentType = p.GetReqHeader(HttpHeaders.KEY_Content_Type);
            if (contentType == null)
                throw new Exception("no Content-Type");
            if (!contentType.StartsWith("multipart/form-data;"))
                throw new Exception("!Content-Type.StartWith(\"multipart/form-data\")");
            var boundrayIndex = contentType.IndexOf(boundary);
            if (boundrayIndex == -1)
                throw new Exception("'boundary=' not Found");
            return NaiveUtils.UTF8Encoding.GetBytes("--" + contentType.Substring(boundrayIndex + boundary.Length));
        }

        static Regex re_Content_Disposition = new Regex("form-data; ?name=\"(.*?)\"(?:; ?filename=\"(.*)\")?");

        static string[] separator = new[] { "\r\n" };

        public async Task<bool> ReadNextPartHeader()
        {
            if (IsReadingPart)
                throw new InvalidOperationException("cannot read next part while IsReadingPart == true");
            if (!firstBoundaryRead) {
                await NaiveUtils.ReadStringUntil(BaseStream, Boundary, withPattern: false);
                Boundary = NaiveUtils.ConcatBytes(NaiveUtils.CRLFBytes, Boundary);
                firstBoundaryRead = true;
            }
            var buf = new byte[2];
            var cur = 0;
            do {
                cur += await BaseStream.ReadAsync(buf, cur, 2 - cur);
            } while (cur < 2);
            if (buf[0] == '-' && buf[1] == '-')
                return false;
            if (buf[0] != '\r' || buf[1] != '\n')
                throw new Exception($"unexcepted data: {buf[0]}({(char)buf[0]}), {buf[1]}({(char)buf[1]})");
            CurrentPartRawHeader = (await NaiveUtils.ReadStringUntil(BaseStream, NaiveUtils.DoubleCRLFBytes, withPattern: true));
            var headers = CurrentPartRawHeader.Split(separator, StringSplitOptions.None);
            var i = 0;
            while (HttpConnection.ParseHeader(headers[i++], out var key, out var value)) {
                CurrentPartHeaders[key] = value;
            }
            var content_disposition = CurrentPartHeaders["Content-Disposition"];
            var regexResult = re_Content_Disposition.Match(content_disposition);
            if (regexResult.Success == false)
                throw new Exception("bad Content-Disposition");
            CurrentPartName = regexResult.Groups[1].Value;
            CurrentPartFileName = regexResult.Groups[2].Value;
            state = 0;
            _position = 0;
            IsReadingPart = true;
            return true;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count).RunSync();
        }

        int state;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (state == 2) {
                throw new EndOfStreamException();
            }
            int matchingPos = 0;
            int boundaryLength = Boundary.Length;
            if (state == 1) {
                matchingPos = 2;
                return 0;
            }
            var buf = new BytesSegment(buffer, offset, count);
            var bufRead = await BaseStream.ReadAsync(buffer, offset, count, cancellationToken);
            var i = offset;
            SEARCH:
            for (; i < offset + bufRead; i++) { // TODO: use KMP search algorithm
                if (buffer[i] == Boundary[matchingPos]) {
                    if (++matchingPos == boundaryLength) {
                        IsReadingPart = false;
                        state = 1;
                        bstream.Push(new BytesSegment(buffer, i + 1, bufRead - (i + 1)));
                        bufRead = i + 1 - boundaryLength;
                        goto ANOTHERBREAK;
                    }
                } else {
                    if (matchingPos > 0) {
                        i -= matchingPos - 1;
                        matchingPos = 0;
                    }
                }
            }
            if (matchingPos > 0) {
                if (bufRead > matchingPos) { // if got data before matching beginning
                    bstream.Push(new BytesSegment(buffer, offset + bufRead - matchingPos, matchingPos));
                    bufRead -= matchingPos;
                } else {
                    if (buf.Len > boundaryLength) {
                        var readuntil = boundaryLength;
                        do {
                            bufRead += await BaseStream.ReadAsync(buffer, offset, readuntil - bufRead, cancellationToken);
                        } while (bufRead < readuntil);
                        goto SEARCH;
                    } else {
                        // TODO
                        throw new NotImplementedException();
                    }
                }
            }
            ANOTHERBREAK:
            var ret = bufRead;
            _position += ret;
            return ret;
        }
    }

    public struct StackBuffer
    {
        byte[] bytes;
        int curPos;
        public int Length { get; private set; }

        public void Push(BytesSegment bs)
        {
            if (bytes == null) {
                bytes = bs.GetBytes(true);
                Length = bytes.Length;
                return;
            }
            if (bytes.Length < bs.Len) {
                var newBytes = new byte[Length + bs.Len];
                CopyTo(newBytes, 0, bs.Len, Length);
                bytes = newBytes;
                curPos = bs.Len;
            }
            curPos -= bs.Len;
            Length += bs.Len;
            Buffer.BlockCopy(bs.Bytes, bs.Offset, bytes, curPos, bs.Len);
        }

        public void Peek(BytesSegment bs, int count)
        {
            CopyTo(bs.Bytes, 0, bs.Offset, count);
        }

        public void Pop(BytesSegment bs, int count)
        {
            Peek(bs, count);
            Sub(count);
        }

        public void Sub(int count)
        {
            curPos += count;
            Length -= count;
        }

        private void CopyTo(byte[] buffer, int srcOffset, int dstOffset, int count)
        {
            Buffer.BlockCopy(bytes, curPos + srcOffset, buffer, dstOffset, count);
        }
    }

    public class BackableStream : ReadOnlyStream
    {
        public BackableStream(Stream baseStream)
        {
            BaseStream = baseStream;
        }

        public Stream BaseStream;

        StackBuffer sbuffer;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Push(BytesSegment bs)
        {
            sbuffer.Push(bs);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (sbuffer.Length > 0) {
                count = Math.Min(sbuffer.Length, count);
                sbuffer.Pop(new BytesSegment(buffer, offset, count), count);
                return count;
            }
            return BaseStream.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (sbuffer.Length > 0) {
                count = Math.Min(sbuffer.Length, count);
                sbuffer.Pop(new BytesSegment(buffer, offset, count), count);
                return Task.FromResult(count);
            }
            return BaseStream.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }
}

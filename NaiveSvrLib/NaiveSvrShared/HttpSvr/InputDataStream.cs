using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static Naive.HttpSvr.HttpHeaders;

namespace Naive.HttpSvr
{
    /// <summary>
    /// HTTP request content stream.
    /// </summary>
    public class InputDataStream : ReadOnlyStream
    {
        internal InputDataStream(HttpConnection p, long length)
        {
            this.p = p;
            this.length = length;
        }

        internal InputDataStream(HttpConnection p)
        {
            this.p = p;
            this.length = -1;
        }

        private HttpConnection p;
        private long length;
        private long readedLength = 0;

        public bool IsChunkedEncoding => length == -1;

        /// <summary>
        /// = <see cref="Length"/> - <see cref="Position"/>
        /// </summary>
        public long RemainingLength => length - readedLength;

        /// <summary>
        /// Request content length.
        /// </summary>
        public override long Length => length;

        public override long Position
        {
            get {
                return readedLength;
            }
            set {
                throw new NotSupportedException();
            }
        }

        public bool IsEOF => IsChunkedEncoding ? (remainingChunkSize == -1) : (RemainingLength == 0);

        long remainingChunkSize = 0;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (IsChunkedEncoding) {
                return _readAsync(buffer, offset, count, CancellationToken.None).RunSync();
            } else {
                int bytesToRead = (int)(count < RemainingLength ? count : RemainingLength);
                if (bytesToRead > 0) {
                    var ret = p.realInputStream.Read(buffer, offset, bytesToRead);
                    if (ret == 0)
                        throw new DisconnectedException("unexpected EOF while reading http request content.");
                    readedLength += ret;
                    return ret;
                } else {
                    return 0;
                }
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _readAsync(buffer, offset, count, cancellationToken);

        public async Task<int> _readAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (IsChunkedEncoding) {
                if (remainingChunkSize == -1) {
                    throw new EndOfStreamException();
                }
                if (remainingChunkSize == 0) {
                    var str = await NaiveUtils.ReadStringUntil(p.realInputStream, NaiveUtils.CRLFBytes, maxLength: 32, withPattern: false);
                    remainingChunkSize = Convert.ToInt64(str, 16);
                    if (remainingChunkSize == 0) {
                        remainingChunkSize = -1; // EOF
                        return 0;
                    }
                }
                int bytesToRead = (int)Math.Min(count, remainingChunkSize);
                var ret = await p.realInputStream.ReadAsync(buffer, offset, bytesToRead, cancellationToken).CAF();
                if (ret == 0)
                    throw new DisconnectedException("unexpected EOF while reading chunked http request content.");
                readedLength += ret;
                remainingChunkSize -= ret;
                if (remainingChunkSize == 0) {
                    var read = 0;
                    do { // read CRLF
                        read += await p.realInputStream.ReadAsync(new byte[2], read, 2 - read);
                    } while (read < 2);
                }
                return ret;
            } else {
                int bytesToRead = (int)(count < RemainingLength ? count : RemainingLength);
                if (bytesToRead > 0) {
                    var ret = await p.realInputStream.ReadAsync(buffer, offset, bytesToRead, cancellationToken).CAF();
                    if (ret == 0)
                        throw new DisconnectedException("unexpected EOF while reading http request content.");
                    readedLength += ret;
                    return ret;
                } else {
                    return 0;
                }
            }
        }
    }


    public abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override void Flush()
        {
            throw new NotSupportedException();
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}

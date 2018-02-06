using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    public enum CompressionType
    {
        None,
        GZip,
        Deflate
    }

    public class CompressedOutputStream : OutputStream
    {
        private Stream compressionStream;

        private CompressionType cType = CompressionType.None;

        internal CompressedOutputStream(HttpConnection p, Stream baseStream) : base(p, baseStream)
        {
        }

        public void AutoCompressionType()
        {
            if (p.GetReqHeaderSplits("Accept-Encoding")?.Contains("gzip") == true) {
                SetCompressionType(CompressionType.GZip);
            }
        }

        /// <summary>
        /// Get/Set current compression type, and automatically set HTTP header
        /// </summary>
        /// <exception cref="InvalidOperationException">HTTP headers have been sent -OR- Current position is not zero.</exception>
        public CompressionType CurrentCompressionType
        {
            get {
                return cType;
            }
            set {
                SetCompressionType(value, true);
            }
        }
        /// <summary>
        /// Set current compression type.
        /// </summary>
        /// <param name="setHeader">Set HTTP header automatically.</param>
        /// <exception cref="InvalidOperationException">HTTP headers have been sent -OR- Current position is not zero.</exception>
        public void SetCompressionType(CompressionType value, bool setHeader = true)
        {
            if (value == cType)
                return;
            if (p.ConnectionState != HttpConnection.States.Processing && setHeader)
                throw new InvalidOperationException("cannot change CompressionType: headers have been sent.");
            if (Position != 0)
                throw new InvalidOperationException("cannot change CompressionType: current position is non-zero.");
            if (value > CompressionType.None) {
                compressionStream = null;
            }
            if (setHeader) {
                setCompressionHeader(value);
            }
            cType = value;
        }

        MemoryStream ms;
        basestreamProxy proxy;

        private void createCompressedStream(CompressionType ct)
        {
            if (ms == null) {
                ms = new MemoryStream(128);
                proxy = new basestreamProxy(this);
            }
            switch (ct) {
            case CompressionType.GZip:
                compressionStream = new GZipStream(ms, CompressionMode.Compress, true);
                break;
            case CompressionType.Deflate:
                compressionStream = new DeflateStream(ms, CompressionMode.Compress, true);
                break;
            }
        }

        private void setCompressionHeader(CompressionType value)
        {
            switch (value) {
            case CompressionType.None:
                p.ResponseHeaders.Remove(HttpHeaders.KEY_Content_Encoding);
                break;
            case CompressionType.GZip:
                p.ResponseHeaders[HttpHeaders.KEY_Content_Encoding] = HttpHeaders.VALUE_Content_Encoding_gzip;
                break;
            case CompressionType.Deflate:
                p.ResponseHeaders[HttpHeaders.KEY_Content_Encoding] = HttpHeaders.VALUE_Content_Encoding_deflate;
                break;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (cType > CompressionType.None) {
                if (compressionStream == null) {
                    createCompressedStream(cType);
                }
                compressionStream.Write(buffer, offset, count);
                flushCompressedBuffer();
            } else {
                base.Write(buffer, offset, count);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (cType > CompressionType.None) {
                if (compressionStream == null) {
                    createCompressedStream(cType);
                }
                compressionStream.Write(buffer, offset, count);
                return flushCompressedBufferAsync(ct);
            } else {
                return base.WriteAsync(buffer, offset, count, ct);
            }
        }

        public override void Flush()
        {
            if (cType > CompressionType.None)
                compressionStream?.Flush();
            flushCompressedBuffer();
            base.Flush();
        }

        public override async Task FlushAsync(CancellationToken ct)
        {
            if (cType > CompressionType.None) {
                var flushAsync = compressionStream?.FlushAsync(ct);
                if (flushAsync != null) await flushAsync;
            }
            if (ms?.Length > 0) {
                await base.WriteAsync(ms.GetBuffer(), 0, (int)ms.Length, ct);
                ms.SetLength(0);
            }
            await base.FlushAsync(ct);
        }

        void flushCompressedBuffer()
        {
            if (ms?.Length > 0) {
                ms.WriteTo(proxy);
                ms.SetLength(0);
            }
        }

        async Task flushCompressedBufferAsync(CancellationToken ct)
        {
            if (ms?.Length > 0) {
                await base.WriteAsync(ms.GetBuffer(), 0, (int)ms.Length, ct);
                ms.SetLength(0);
            }
        }

        /// <summary>
        /// Close compression stream and base stream.
        /// </summary>
        public override void Close()
        {
            if (cType > CompressionType.None) {
                compressionStream?.Flush();
                compressionStream?.Close();
            }
            flushCompressedBuffer();
            base.Close();
        }

        public override async Task CloseAsync()
        {
            if (cType > CompressionType.None) {
                compressionStream?.Flush();
                compressionStream?.Close();
            }
            await flushCompressedBufferAsync(CancellationToken.None);
            await base.CloseAsync();
        }

        private void baseWrite(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
        }

        private Task baseWriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            return base.WriteAsync(buffer, offset, count, ct);
        }

        private class basestreamProxy : WriteOnlyStream
        {
            private CompressedOutputStream baseStream;

            public basestreamProxy(CompressedOutputStream baseStream)
            {
                this.baseStream = baseStream;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                baseStream.baseWrite(buffer, offset, count);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return baseStream.baseWriteAsync(buffer, offset, count, cancellationToken);
            }
        }
    }

    public abstract class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length
        {
            get {
                throw new NotSupportedException();
            }
        }

        public override long Position
        {
            get {
                throw new NotSupportedException();
            }

            set {
                throw new NotSupportedException();
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
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
    }
}

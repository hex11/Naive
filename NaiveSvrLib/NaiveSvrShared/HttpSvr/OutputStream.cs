using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    /// <summary>
    /// HTTP response content stream
    /// </summary>
    public class OutputStream : Stream
    {
        private static readonly WeakObjectPool<MemoryStream> bufferPool = new WeakObjectPool<MemoryStream>(
            () => new MemoryStream(BufferInitSize),
            buffer => {
                buffer.Position = 0;
                buffer.SetLength(0);
            });


        internal OutputStream(HttpConnection p, Stream baseStream)
        {
            this.p = p;
            this.baseStream = baseStream;
        }

        protected HttpConnection p;
        private Stream baseStream;

        private MemoryStream buffer => bufferHandle?.Value;
        private WeakObjectPool<MemoryStream>.Handle bufferHandle;

        public event Action<OutputStream> Actived;

        public Mode CurrentMode { get; private set; } = Mode.Buffering;
        public enum Mode
        {
            /// <summary>
            /// Writing content to buffer. HTTP headers haven't sent.
            /// </summary>
            Buffering,
            /// <summary>
            /// Writing content by HTTP chunked encoding.
            /// </summary>
            Chunked,
            /// <summary>
            /// HTTP headers including Connect-Length have sent.
            /// </summary>
            KnownLength,
            /// <summary>
            /// No HTTP Keep-Alive. Base stream will be closed when response ends.
            /// </summary>
            NoKeepAlive
        }

        private bool needKeepAlive => p.keepAlive;
        /// <summary>
        /// init buffer size
        /// </summary>
        public static int BufferInitSize = 16 * 1024;
        /// <summary>
        /// buffer size to automagictly switch mode to Chunked or NoKeepAlive.
        /// </summary>
        public static int BufferSize = 16 * 1024;
        private long haveWrote = 0;
        private long lengthToWrite = 0;
        /// <summary>
        /// Implements Stream.Flush
        /// </summary>
        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken ct)
        {
            return NaiveUtils.CompletedTask;
        }
        /// <summary>
        /// Implements Stream.Write
        /// </summary>
        /// <exception cref="DisconnectedException">catched a SocketException</exception>
        /// <exception cref="OutputStreamException">current mode is KnownLength, and position is out of range</exception>
        public override void Write(byte[] buffer, int offset, int count)
        {
            try {
                _write(buffer, offset, count);
            } catch (IOException e) when (e.InnerException is SocketException) {
                throw new DisconnectedException(e.InnerException.Message);
            }
            Actived?.Invoke(this);
        }

        /// <summary>
        /// Implements Stream.WriteAsync
        /// </summary>
        /// <exception cref="DisconnectedException">catched a SocketException</exception>
        /// <exception cref="OutputStreamException">current mode is KnownLength, and position is out of range</exception>
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try {
                if (count == 0)
                    return;
                haveWrote += count;
                WRITE:
                if (CurrentMode == Mode.Buffering) {
                    if (haveWrote <= BufferSize) {
                        if (this.bufferHandle == null)
                            this.bufferHandle = bufferPool.Get();
                        this.buffer.Write(buffer, offset, count);
                    } else {
                        if (needKeepAlive) {
                            await SwitchToChunkedModeAsync().CAF();
                        } else {
                            await SwitchToNoKeepAliveModeAsync().CAF();
                        }
                        goto WRITE;
                    }
                } else if (CurrentMode == Mode.Chunked) {
                    await writeChunkSizeAsync(baseStream, count).CAF();
                    await baseStream.WriteAsync(buffer, offset, count, cancellationToken).CAF();
                    await writeBytesToStreamAsync(baseStream, CRLF).CAF();
                } else if (CurrentMode == Mode.KnownLength) {
                    if (haveWrote > lengthToWrite)
                        throw new OutputStreamException("position > content-length");
                    await baseStream.WriteAsync(buffer, offset, count, cancellationToken).CAF();
                } else if (CurrentMode == Mode.NoKeepAlive) {
                    await baseStream.WriteAsync(buffer, offset, count, cancellationToken).CAF();
                }
            } catch (IOException e) when (e.InnerException is SocketException) {
                throw new DisconnectedException(e.InnerException.Message);
            } catch (SocketException se) {
                throw new DisconnectedException(se.Message);
            }
            Actived?.Invoke(this);
        }

        private void _write(byte[] buffer, int offset, int count)
        {
            if (count == 0)
                return;
            haveWrote += count;
            WRITE:
            if (CurrentMode == Mode.Buffering) {
                if (haveWrote <= BufferSize) {
                    if (this.bufferHandle == null)
                        this.bufferHandle = bufferPool.Get();
                    this.buffer.Write(buffer, offset, count);
                } else {
                    if (needKeepAlive) {
                        SwitchToChunkedMode();
                    } else {
                        SwitchToNoKeepAliveMode();
                    }
                    goto WRITE;
                }
            } else if (CurrentMode == Mode.Chunked) {
                writeChunkSize(baseStream, count);
                baseStream.Write(buffer, offset, count);
                writeBytesToStream(baseStream, CRLF);
            } else if (CurrentMode == Mode.KnownLength) {
                if (haveWrote > lengthToWrite)
                    throw new OutputStreamException("position > content-length");
                baseStream.Write(buffer, offset, count);
            } else if (CurrentMode == Mode.NoKeepAlive) {
                baseStream.Write(buffer, offset, count);
            }
        }

        /// <summary>
        /// Switch mode from Buffering to KnownLength.
        /// </summary>
        /// <param name="lengthToWrite">Expected length</param>
        /// <exception cref="OutputStreamException">Current mode is not Buffering</exception>
        /// <exception cref="ArgumentOutOfRangeException">lengthToWrite is less than current buffered length</exception>
        public void SwitchToKnownLengthMode(long lengthToWrite) => SwitchToKnownLengthModeAsync(lengthToWrite).RunSync();

        public async Task SwitchToKnownLengthModeAsync(long lengthToWrite)
        {
            if (CurrentMode != Mode.Buffering) {
                throw new OutputStreamException("current mode is not buffering.");
            }
            if (this.buffer != null && this.lengthToWrite < this.buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(lengthToWrite), "length cannot be less than buffer");
            CurrentMode = Mode.KnownLength;
            this.lengthToWrite = lengthToWrite;
            p.ResponseHeaders[HttpHeaders.KEY_Content_Length] = lengthToWrite.ToString();
            await writeHeadersToBaseStreamAsync().CAF();
            await writeBufferToBaseStreamAndClearBufferAsync().CAF();
        }

        /// <summary>
        /// Switch mode from Buffering to NoKeepAlive.
        /// </summary>
        /// <exception cref="OutputStreamException">Current mode is not Buffering</exception>
        public void SwitchToNoKeepAliveMode() => SwitchToNoKeepAliveModeAsync().RunSync();

        public async Task SwitchToNoKeepAliveModeAsync()
        {
            if (CurrentMode != Mode.Buffering)
                throw new OutputStreamException("current mode is not buffering");
            CurrentMode = Mode.NoKeepAlive;
            p.keepAlive = false;
            p.ResponseHeaders[HttpHeaders.KEY_Connection] = HttpHeaders.VALUE_Connection_close;
            await writeHeadersToBaseStreamAsync().CAF();
            await writeBufferToBaseStreamAndClearBufferAsync().CAF();
        }

        /// <summary>
        /// Switch mode from Buffering to Chunked.
        /// </summary>
        /// <exception cref="OutputStreamException">Current mode is not Buffering</exception>
        public void SwitchToChunkedMode() => SwitchToChunkedModeAsync().RunSync();
        /// <summary>
        /// Switch mode from Buffering to Chunked.
        /// </summary>
        /// <exception cref="OutputStreamException">Current mode is not Buffering</exception>
        public async Task SwitchToChunkedModeAsync()
        {
            if (CurrentMode != Mode.Buffering)
                throw new OutputStreamException("current mode is not buffering");
            CurrentMode = Mode.Chunked;
            p.ResponseHeaders[HttpHeaders.KEY_Transfer_Encoding] =
                                    HttpHeaders.VALUE_Transfer_Encoding_chunked;
            await writeHeadersToBaseStreamAsync().CAF();
            if (this.buffer != null && this.buffer.Length > 0) {
                await writeChunkSizeAsync(baseStream, (int)this.buffer.Length).CAF();
                await writeBufferToBaseStreamAndClearBufferAsync().CAF();
                await writeBytesToStreamAsync(baseStream, CRLF).CAF();
            }
        }
        /// <summary>
        /// Clear buffer.
        /// </summary>
        /// <exception cref="OutputStreamException">Current mode is not Buffering</exception>
        public void ClearBuffer()
        {
            if (CurrentMode != Mode.Buffering) {
                throw new OutputStreamException("current mode is not buffering");
            }
            if (bufferHandle != null) {
                bufferHandle.Dispose();
                bufferHandle = null;
            }
            haveWrote = 0;
        }

        private async Task writeBufferToBaseStreamAndClearBufferAsync()
        {
            if (buffer != null) {
                await baseStream.WriteAsync(buffer.GetBuffer(), 0, (int)buffer.Length).CAF();
                bufferHandle.Dispose();
                bufferHandle = null;
            }
        }

        /// <summary>
        /// Close output stream.
        /// </summary>
        /// <exception cref="OutputStreamException">Current mode is KnownLength, and wrote length is too small</exception>
        public override void Close()
        {
            CloseAsync().RunSync();
        }

        /// <summary>
        /// Close output stream.
        /// </summary>
        /// <exception cref="OutputStreamException">Current mode is KnownLength, and wrote length is too small</exception>
        public virtual async Task CloseAsync()
        {
            try {
                if (CurrentMode == Mode.Chunked) {
                    await writeChunkSizeAsync(baseStream, 0).CAF();
                    await writeBytesToStreamAsync(baseStream, CRLF).CAF();
                } else if (CurrentMode == Mode.Buffering) {
                    p.ResponseHeaders[HttpHeaders.KEY_Content_Length] = haveWrote.ToString();
                    await writeHeadersToBaseStreamAsync().CAF();
                    await writeBufferToBaseStreamAndClearBufferAsync().CAF();
                } else if (CurrentMode == Mode.KnownLength) {
                    if (lengthToWrite != haveWrote) {
                        throw new OutputStreamException("wrote length != content-length");
                    }
                }
                await baseStream.FlushAsync();
                base.Close();
            } catch (IOException e) when (e.InnerException is SocketException) {
                throw new DisconnectedException(e.InnerException.Message);
            }
        }

        private static byte[] CRLF = new byte[] { (byte)'\r', (byte)'\n' };

        private async Task writeHeadersToBaseStreamAsync()
        {
            if (p.ConnectionState > HttpConnection.States.Processing) {
                throw new OutputStreamException("headers have already been sent");
            }
#if DEBUG
            p.ResponseHeaders["X-NaiveSvr-Mode"] = CurrentMode.ToString();
#endif
            using (MemoryStream ms = new MemoryStream(512))
            using (var sw = new StreamWriter(ms, NaiveUtils.UTF8Encoding, 256)) {
                p.writeResponseTo(sw);
                sw.Flush();
                await baseStream.WriteAsync(ms.GetBuffer(), 0, (int)ms.Length);
                await baseStream.FlushAsync();
            }
            p.ConnectionState = HttpConnection.States.HeadersEnded;
        }

        private static Task writeChunkSizeAsync(Stream stream, int size)
        {
            byte[] bytesOfChunkSize = getChunkSizeBytes(size);
            return writeBytesToStreamAsync(stream, bytesOfChunkSize);
        }

        private static Task writeBytesToStreamAsync(Stream stream, byte[] bytes)
        {
            return stream.WriteAsync(bytes);
        }

        private static void writeChunkSize(Stream stream, int size)
        {
            byte[] bytesOfChunkSize = getChunkSizeBytes(size);
            writeBytesToStream(stream, bytesOfChunkSize);
        }

        private static void writeBytesToStream(Stream stream, byte[] bytes)
        {
            stream.Write(bytes);
        }

        private static byte[] getChunkSizeBytes(int size)
        {
            string chunkSize = Convert.ToString(size, 16) + "\r\n";
            return Encoding.UTF8.GetBytes(chunkSize);
        }

        /// <summary>
        /// Returns false
        /// </summary>
        public override bool CanRead => false;
        /// <summary>
        /// Returns false
        /// </summary>
        public override bool CanSeek => false;
        /// <summary>
        /// Returns true
        /// </summary>
        public override bool CanWrite => true;
        /// <summary>
        /// Wrote/buffered size. (= <see cref="Position"/>)
        /// </summary>
        public override long Length
        {
            get {
                return haveWrote;
            }
        }
        /// <summary>
        /// Wrote/buffered size. (= <see cref="Length"/>)
        /// </summary>
        public override long Position
        {
            get {
                return haveWrote;
            }

            set {
                throw new NotSupportedException();
            }
        }
        /// <summary>
        /// Throws <see cref="NotSupportedException"/>
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
        /// <summary>
        /// Throws <see cref="NotSupportedException"/>
        /// </summary>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }
        /// <summary>
        /// Throws <see cref="NotSupportedException"/>
        /// </summary>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) {
            }
            if (bufferHandle != null) {
                bufferHandle.Dispose();
                bufferHandle = null;
            }
            base.Dispose(disposing);
        }
    }

    internal class OutputStreamException : Exception
    {
        public OutputStreamException()
        {
        }

        public OutputStreamException(string str) : base(str)
        {
        }
    }
}

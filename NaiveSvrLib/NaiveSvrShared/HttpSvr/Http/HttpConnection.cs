using NaiveSocks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static Naive.HttpSvr.HttpHeaders;

namespace Naive.HttpSvr
{
    public class HttpConnection : ObjectWithTags
    {
        public HttpConnection(Stream stream, EPPair epPair, NaiveHttpServer server)
        {
            this.baseStream = stream;
            this.myStream = stream.ToMyStream();
            this.server = server;
            this.epPair = epPair;
        }

        public HttpConnection(IMyStream myStream, EPPair epPair, NaiveHttpServer server)
        {
            this.baseStream = myStream.ToStream();
            this.myStream = myStream;
            this.server = server;
            this.epPair = epPair;
        }

        public Socket socket;

        public NaiveHttpServer server;

        public Stream baseStream;
        public IMyStream myStream;

        private Stream realOutputStream => baseStream;

        private TextWriter realOutputWriter;

        public CompressedOutputStream outputStream;
        public TextWriter outputWriter;

        public InputDataStream inputDataStream;

        public EPPair epPair;
        public IPEndPoint remoteEP => epPair.RemoteEP;
        public IPEndPoint localEP => epPair.LocalEP;

        public HttpHeaderCollection RequestHeaders;

        public string GetReqHeader(string key) => RequestHeaders[key];

        static readonly char[] headerValueSeparator = { ',' };

        public List<string> GetReqHeaderSplits(string key)
        {
            var val = GetReqHeader(key);
            if (val == null) return null;
            return val.Split(headerValueSeparator).Select(x => x.Trim()).ToList();
        }

        // "GET /show+result?id=233 HTTP/1.1" for example

        public string Method;       // "GET"
        public string Url;          // "/show+result?id=233"
        public string Url_path;     // "/show result"
        public string Url_qstr;     // "id=233"
        public string HttpVersion;  // "HTTP/1.1"

        public string RealUrl { get; private set; }
        public string RealPath { get; private set; }
        public string RealPathEscaped { get; private set; }

        private NameValueCollection _parsedQstr;
        public NameValueCollection ParsedQstr => _parsedQstr ?? (_parsedQstr = HttpUtil.ParseQueryString(Url_qstr));

        public bool IsHttp1_1 { get; private set; }

        public string RawRequest { get; private set; }
        public int RawRequestBytesLength { get; private set; }

        public string Host;

        public bool Handled = false;

        public HttpHeaderCollection ResponseHeaders;

        public const string defaultResponseCode = "404 Not Found";

        public const string defaultContentType = "text/html; charset=utf-8";

        public string ResponseStatusCode;

        public bool EnableKeepAlive = true;

        public bool keepAlive = true;

        public int requestCount = 0;
        public int RequestCount => requestCount;

        public string ServerHeader; // = "NaiveServer";

        public event Action<HttpConnection> ConnectionBegin;
        public event Action<HttpConnection> ConnectionStateChanged;
        public event HttpRequestHandler Requested;
        public event Action<HttpConnection> ConnectionEnd;

        private States _connectionState = States.Receiving;

        public States ConnectionState
        {
            get {
                return _connectionState;
            }
            set {
                _connectionState = value;
                ConnectionStateChanged?.Invoke(this);
            }
        }

        public enum States
        {
            /// <summary>
            /// Receiving/parsing HTTP request (without body).
            /// </summary>
            Receiving = 0,
            /// <summary>
            /// Processing request. HTTP response headers haven't sent.
            /// </summary>
            Processing = 1,
            /// <summary>
            /// Processing request. HTTP response headers have sent. Sending response content.
            /// </summary>
            HeadersEnded = 2,

            ResponseEnded = 3,

            /// <summary>
            /// Switched/Upgraded to another protocol (e.g., WebSocket).
            /// </summary>
            SwitchedProtocol = 4,
        }

        public bool IsClientSupportKeepAlive
        {
            get {
                var connection = GetReqHeaderSplits(KEY_Connection);
                return connection?.Contains(VALUE_Connection_Keepalive) == true
                || (IsHttp1_1 && (connection?.Contains(VALUE_Connection_close) != true));
            }
        }

        public bool disconnecting = false;

        public void Disconnect()
        {
            disconnecting = true;
            try {
                baseStream.Close();
            } catch (Exception) {
                log("failed to disconnect", Logging.Level.Warning);
            }
        }

        public async Task Process()
        {
            try {
                realOutputWriter = new StreamWriter(realOutputStream, NaiveUtils.UTF8Encoding);
                realOutputWriter.NewLine = "\r\n";
                ConnectionBegin?.Invoke(this);
                await _requestingLoop().CAF();
            } catch (DisconnectedException) {

            } finally {
                try {
                    baseStream.Close();
                } catch (Exception) { }
                ConnectionEnd?.Invoke(this);
            }
        }

        private async Task _requestingLoop()
        {
            do {
                if (!await _ReceiveNextRequest().CAF())
                    break;
                ConnectionState = States.Processing;
                try {
                    await processRequest().CAF();
                } catch (DisconnectedException) {
                    return;
                } catch (Exception e) {
                    if (disconnecting || myStream.State.IsClosed)
                        break;
                    server.logException(e, Logging.Level.Warning, $"[{server.mark}#{Id}({requestCount}) {remoteEP}] handling");
                    if (ConnectionState == States.SwitchedProtocol)
                        break;
                    if (ConnectionState == States.Processing & outputStream.Position == 0) {
                        await server.errorPage(this, e);
                    } else if (outputStream.CurrentMode == OutputStream.Mode.Buffering) {
                        outputStream.ClearBuffer();
                        await server.errorPage(this, e);
                    } else if (outputStream.CurrentMode == OutputStream.Mode.Chunked) {

                    } else {
                        break;
                    }
                }

                if (ConnectionState != States.ResponseEnded && ConnectionState != States.SwitchedProtocol)
                    await EndResponseAsync();
            } while (keepAlive);
            if (ConnectionState != States.SwitchedProtocol) {
                await realOutputWriter.FlushAsync();
                realOutputStream.Close();
            }
        }

        public virtual void EndResponse()
        {
            EndResponseAsync().RunSync();
        }

        public Task EndResponseAsync()
        {
            return EndResponseAsync(null);
        }

        public virtual async Task EndResponseAsync(string writecontent)
        {
            if (ConnectionState == States.ResponseEnded)
                throw new Exception("endResponse(): connectionStatus is already ResponseEnded");
            this.Handled = true;
            checkInputData();
            if (writecontent != null)
                await outputWriter.WriteAsync(writecontent).CAF();
            await outputWriter.FlushAsync().CAF(); // it will also flush its base stream
            await outputStream.CloseAsync().CAF();
            ConnectionState = States.ResponseEnded;
        }

        private bool checkInputData()
        {
            if (inputDataStream?.IsEOF == false) {
                log($"unread input data is found. (length={inputDataStream.Length}, read={inputDataStream.Position})", Logging.Level.Warning);
                if (ConnectionState == States.Processing)
                    ResponseHeaders[KEY_Connection] = VALUE_Connection_close;
                keepAlive = false;
                return false;
            }
            return true;
        }

        private void initOutputStream()
        {
            outputStream = new CompressedOutputStream(this, realOutputStream);
            outputWriter = new StreamWriter(outputStream, NaiveUtils.UTF8Encoding);
            outputWriter.NewLine = "\r\n";
        }

        public async Task<bool> _ReceiveNextRequest()
        {
            if (!checkInputData())
                throw new InvalidOperationException("cannot receive next request because unread input data exists.");
            ConnectionState = States.Receiving;
            clearStatus(); // to make them be collected faster
            rawRequestPos = 0;
            try {
                await readRequest().CAF();
            } catch (Exception e) when (e.IsConnectionException()) {
                return false;
            }
            initStatus();
            parseRequestAndHeaders();
            initOutputStream();
            initInputDataStream();
            return true;
        }

        private void parseRequestAndHeaders()
        {
            parseRequest(readLineFromRawRequest());
            while (parseHeader(readLineFromRawRequest()))
                ;
            keepAlive = EnableKeepAlive && IsClientSupportKeepAlive;
            ResponseHeaders[KEY_Connection] = keepAlive ? VALUE_Connection_Keepalive : VALUE_Connection_close;
            Host = GetReqHeader("Host");
            requestCount++;
        }

        private void parseRequest(string requestLine)
        {
            var splits = requestLine.Split(' ');
            if (splits.Length != 3) {
                throw new Exception("Bad Request: " + requestLine);
            }
            foreach (var item in splits) {
                if (item.Length == 0)
                    throw new Exception("Bad Request: " + requestLine);
            }
            Method = splits[0];
            RealUrl = Url = splits[1];
            HttpVersion = splits[2];
            IsHttp1_1 = HttpVersion == "HTTP/1.1";
            NaiveUtils.SplitUrl(Url, out var path, out Url_qstr);
            RealPathEscaped = path;
            RealPath = Url_path = HttpUtil.UrlDecode(path);
        }

        private static readonly char[] headerSeparator = new[] { ':' };
        private static readonly char[] headerValueTrimStart = new[] { ' ' };
        private bool parseHeader(string headerLine)
        {
            if (headerLine?.Length == 0)
                return false;
            var splits = headerLine.Split(headerSeparator, 2);
            if (splits.Length != 2) {
                throw new Exception($"Bad header line: \"{headerLine}\"");
            }
            var key = splits[0];
            var value = splits[1].TrimStart(headerValueTrimStart);
            RequestHeaders.Add(new HttpHeader(key, value)); // allow duplicated keys
            return true;
        }

        public static bool ParseHeader(string headerLine, out string key, out string value)
        {
            if (headerLine?.Length == 0) {
                key = null;
                value = null;
                return false;
            }
            var splits = headerLine.Split(headerSeparator, 2);
            if (splits.Length != 2) {
                throw new Exception($"Bad header line: \"{headerLine}\"");
            }
            key = splits[0];
            value = splits[1].TrimStart(' ');
            return true;
        }

        public void ClearCaches()
        {
            _parsedQstr = null;
        }

        private void clearStatus()
        {
            ClearCaches();
            outputStream = null; outputWriter = null; inputDataStream = null;
            RawRequest = Host = Method = Url = Url_path = Url_qstr = HttpVersion = RealUrl = RealPath = RealPathEscaped = null;
            RequestHeaders?.Clear();
            ResponseHeaders?.Clear();
            ResponseStatusCode = defaultResponseCode;
        }

        private void initStatus()
        {
            RequestHeaders = RequestHeaders ?? new HttpHeaderCollection(16);

            ResponseHeaders = ResponseHeaders ?? new HttpHeaderCollection(16);
            ResponseHeaders[KEY_Content_Type] = defaultContentType;
            if (ServerHeader != null) {
                ResponseHeaders[KEY_Server] = ServerHeader;
            }
        }

        private Task processRequest()
        {
            Handled = false;
            return ProcessRequest();
        }

        protected virtual Task ProcessRequest()
        {
            Requested?.Invoke(this);
            if (Handled)
                return AsyncHelper.CompletedTask;
            if (server is IHttpRequestAsyncHandler asyncServer)
                return asyncServer.HandleRequestAsync(this);
            else
                return Task.Run(() => server.HandleRequest(this));
        }

        private void initInputDataStream()
        {
            inputDataStream = null;
            if (RequestHeaders[KEY_Content_Length] is string lengthHeader) {
                long length = 0;
                if (long.TryParse(lengthHeader, out length) && length >= 0) {
                    inputDataStream = new InputDataStream(this, length);
                } else {
                    throw new Exception("Bad Content-Length.");
                }
            } else if (RequestHeaders[KEY_Transfer_Encoding] as string == VALUE_Transfer_Encoding_chunked) {
                inputDataStream = new InputDataStream(this);
            }
        }

        private const int MaxRequestLength = 64 * 1024;

        int rawRequestPos = 0;
        private string readLineFromRawRequest()
        {
            if (rawRequestPos >= RawRequest.Length)
                throw new Exception("RawRequest EOF.");
            var endOfLinePos = RawRequest.IndexOf('\n', rawRequestPos);
            if (endOfLinePos == -1)
                throw new Exception("EOL not found.");
            var startPos = rawRequestPos;
            rawRequestPos = endOfLinePos + 1;
            if (endOfLinePos - 1 >= 0 && RawRequest[endOfLinePos - 1] == '\r')
                endOfLinePos -= 1;
            return RawRequest.Substring(startPos, endOfLinePos - startPos);
        }

        private async Task readRequest()
        {
            var buf = BufferPool.GlobalGet(MaxRequestLength);
            var cur = 0;
            var gotLF = 0;

            while (true) {
                if (cur + 2 >= MaxRequestLength)
                    throw new Exception("request length > MaxRequestLength");
                var read = await myStream.ReadAsyncR(new BytesSegment(buf, cur, 2 - gotLF)).CAF();
                if (read == 0)
                    throw new DisconnectedException("unexpected EOF");
                var parsingCur = cur;
                cur += read;
                while (parsingCur < cur) {
                    var ch = buf[parsingCur++];
                    if (ch == '\n') {
                        if (gotLF++ == 1)
                            goto DONE;
                    } else if (ch == '\r') {
                        continue;
                    } else {
                        gotLF = 0;
                    }
                }
            }
            DONE:

            var len = cur;
            RawRequestBytesLength = len;
            RawRequest = NaiveUtils.UTF8Encoding.GetString(buf, 0, len);
            BufferPool.GlobalPut(buf);
        }

        private void ThrowIfHeadersEnded(string methodName)
        {
            if (ConnectionState >= States.HeadersEnded)
                throw new Exception($"{methodName}(): but headers have already been sent");
        }

        public void setHeader(string key, string value)
        {
            ThrowIfHeadersEnded(nameof(setHeader));
            ResponseHeaders[key] = value;
        }

        public void setContentTypeTextPlain()
        {
            this.setHeader(HttpHeaders.KEY_Content_Type, "text/plain; charset=utf-8");
        }

        public void setStatusCode(string statusCode)
        {
            ThrowIfHeadersEnded(nameof(setStatusCode));
            this.ResponseStatusCode = statusCode;
        }

        public HttpConnection write(string str)
        {
            outputWriter.Write(str);
            return this;
        }

        public void writeEncoded(string str)
        {
            outputWriter.Write(HttpUtil.HtmlEncode(str));
        }

        public Task writeAsync(string str)
            => outputWriter.WriteAsync(str);

        public HttpConnection writeLine(string str)
        {
            outputWriter.WriteLine(str);
            return this;
        }

        public Task writeLineAsync(string str)
             => outputWriter.WriteLineAsync(str);

        string GetResponseHttpVersion() => IsHttp1_1 ? "HTTP/1.1 " : "HTTP/1.0 ";

        internal async Task writeResponseToAsync(TextWriter writer)
        {
            await writer.WriteAsync(GetResponseHttpVersion() + ResponseStatusCode + "\r\n");
            foreach (var kv in ResponseHeaders) {
                await writer.WriteAsync(kv.Key + ": " + ResponseHeaders[kv.Value] + "\r\n");
            }
            await writer.WriteAsync("\r\n");
        }

        internal void writeResponseTo(TextWriter writer)
        {
            writer.Write(GetResponseHttpVersion());
            writer.Write(ResponseStatusCode);
            writer.Write("\r\n");
            foreach (var kv in ResponseHeaders) {
                writer.Write(kv.Key);
                writer.Write(": ");
                writer.Write(kv.Value);
                writer.Write("\r\n");
            }
            writer.Write("\r\n");
        }

        public int Id { get; } = idGen.Get();

        private static IncrNumberGenerator idGen = new IncrNumberGenerator();

        public void info(string text) => log(text, Logging.Level.Info);

        public virtual void log(string text, Logging.Level level)
        {
            server.log($"(#{Id}|{remoteEP}({RequestCount})) {text}", level);
        }

        public Stream SwitchProtocol()
        {
            ConnectionState = States.SwitchedProtocol;
            keepAlive = false;
            return baseStream;
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public virtual string ToString(bool verbose)
        {
            if (verbose)
                return $"{{HttpConn #{Id}|{remoteEP}({RequestCount})}}";
            else
                return $"{{HttpConn #{Id}({RequestCount})}}";
        }
    }

    public interface IHaveTags
    {
        object this[object key] { get; set; }
    }

    public class ObjectWithTags : IHaveTags
    {
        private Hashtable tags;
        public object GetTag(object key)
        {
            if (tags == null)
                return null;
            return tags[key];
        }

        public void SetTag(object key, object value)
        {
            if (tags == null) {
                tags = new Hashtable();
            }
            tags[key] = value;
        }

        public object this[object key]
        {
            get {
                return GetTag(key);
            }
            set {
                SetTag(key, value);
            }
        }
    }
}

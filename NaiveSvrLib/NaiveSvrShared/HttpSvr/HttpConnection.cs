using System;
using System.Collections;
using System.Collections.Generic;
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
    public class HttpConnection : ObjectWithTags, IDisposable
    {
        //public TcpClient tcpClient;
        public Stream baseStream;
        public NaiveHttpServer server;

        public Socket socket;

        internal Stream realInputStream;
        private Stream realOutputStream;
        private TextWriter realOutputWriter;

        public CompressedOutputStream outputStream;
        public TextWriter outputWriter;

        public InputDataStream inputDataStream;
        public IPEndPoint remoteEP;
        public IPEndPoint localEP;

        public Hashtable RequestHeaders;

        public string GetReqHeader(string key) => RequestHeaders[key] as string;

        public List<string> GetReqHeaderSplits(string key)
        {
            var val = GetReqHeader(key);
            if (val == null) return null;
            return (from x in val.Split(',') select x.Trim()).ToList();
        }

        // "GET /query?id=233 HTTP/1.1" for example

        public string Method; // "GET"
        public string Url; // "/query?id=233"
        public string Url_path; // "/query"
        public string Url_qstr; // "id=233"
        public string HttpVersion; // "HTTP/1.1"

        public string RawRequest { get; private set; }

        public string Host;

        public bool Handled = false;

        public Hashtable ResponseHeaders;

        public const string defaultResponseCode = "404 Not Found";

        public const string defaultContentType = "text/html; charset=utf-8";

        public string ResponseStatusCode;

        public bool EnableKeepAlive = true;

        public bool keepAlive = true;

        public int requestCount = 0;
        public int RequestCount => requestCount;

        public string ServerHeader = "NaiveServer";

        public event Action<HttpConnection> ConnectionBegin;
        public event Action<HttpConnection> ConnectionStatusChanged;
        public event HttpRequestHandler Requested;
        public event Action<HttpConnection> ConnectionEnd;

        private States _connectionStatus = States.Receiving;

        public States ConnectionState
        {
            get {
                return _connectionStatus;
            }
            set {
                _connectionStatus = value;
                ConnectionStatusChanged?.Invoke(this);
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
                || (HttpVersion == "HTTP/1.1" && connection?.Contains(VALUE_Connection_close) != true);
            }
        }

        public HttpConnection(Stream stream, EPPair ePPair, NaiveHttpServer server)
        {
            this.baseStream = stream;
            this.server = server;
            remoteEP = ePPair.RemoteEP;
            localEP = ePPair.LocalEP;
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
            realOutputStream = realInputStream = baseStream;
            realOutputWriter = new StreamWriter(realOutputStream, NaiveUtils.UTF8Encoding);
            realOutputWriter.NewLine = "\r\n";
            ConnectionBegin?.Invoke(this);
            try {
                await _requestingLoop().CAF();
            } catch (DisconnectedException) {
                return;
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
                resetResponseStatus();
                ConnectionState = States.Receiving;
                await _ReceiveNextRequest().CAF();
                ConnectionState = States.Processing;
                try {
                    await processRequest().CAF();
                } catch (DisconnectedException) {
                    return;
                } catch (Exception e) {
                    if (disconnecting)
                        break;
                    server.logException(e, Logging.Level.Warning, $"[{server.mark}#{id}({requestCount}) {remoteEP}] handling");
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

        public virtual async Task EndResponseAsync()
        {
            if (ConnectionState == States.ResponseEnded)
                throw new Exception("endResponse(): connectionStatus is already ResponseEnded");
            this.Handled = true;
            checkInputData();
            await outputWriter.FlushAsync().CAF();
            await outputStream.FlushAsync().CAF();
            await outputStream.CloseAsync().CAF();
            ConnectionState = States.ResponseEnded;
        }

        private void checkInputData()
        {
            if (inputDataStream?.IsEOF == false) {
                log("unread input data is found.", Logging.Level.Warning);
                if (ConnectionState == States.Processing)
                    ResponseHeaders[KEY_Connection] = VALUE_Connection_close;
                keepAlive = false;
            }
        }

        private void initOutputStream()
        {
            outputStream = new CompressedOutputStream(this, realOutputStream);
            outputWriter = new StreamWriter(outputStream, NaiveUtils.UTF8Encoding);
            outputWriter.NewLine = "\r\n";
        }

        public async Task _ReceiveNextRequest()
        {
            checkInputData();
            await parseRequestAndHeaders();
            initOutputStream();
            initInputDataStream();
        }

        private async Task parseRequestAndHeaders()
        {
            rawRequestPos = 0;
            RawRequest = await readRequest().CAF();
            RequestHeaders = new Hashtable(16);
            parseRequest(readLineFromRawRequest());
            keepAlive = EnableKeepAlive && IsClientSupportKeepAlive;
            ResponseHeaders[KEY_Connection] = keepAlive ? VALUE_Connection_Keepalive : VALUE_Connection_close;
            while (parseHeader(readLineFromRawRequest()))
                ;
            Host = GetReqHeader("Host");
            requestCount++;
        }

        private void parseRequest(string requestLine)
        {
            var splits = requestLine.Split(' ');
            if (splits.Length != 3) {
                throw new Exception("Bad Request: " + requestLine);
            }
            Method = splits[0];
            Url = splits[1];
            HttpVersion = splits[2];
            NaiveUtils.SplitUrl(Url, out Url_path, out Url_qstr);
            Url_path = HttpUtil.UrlDecode(Url_path);
        }

        private static readonly char[] headerSeparator = new[] { ':' };
        private bool parseHeader(string headerLine)
        {
            if (headerLine == "")
                return false;
            var splits = headerLine.Split(headerSeparator, 2);
            if (splits.Length != 2) {
                throw new Exception($"Bad header line: \"{headerLine}\"");
            }
            var key = splits[0];
            var value = splits[1].TrimStart(' ');
            RequestHeaders[key] = value;
            return true;
        }

        public static bool ParseHeader(string headerLine, out string key, out string value)
        {
            if (headerLine == "") {
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

        private void resetResponseStatus()
        {
            ResponseStatusCode = defaultResponseCode;
            ResponseHeaders = new Hashtable(16);
            ResponseHeaders[KEY_Content_Type] = defaultContentType;
            if (ServerHeader != null) {
                ResponseHeaders[KEY_Server] = ServerHeader;
            }
        }

        private async Task processRequest()
        {
            Handled = false;
            await ProcessRequest();
        }

        protected virtual async Task ProcessRequest()
        {
            Requested?.Invoke(this);
            if (Handled)
                return;
            if (server is IHttpRequestAsyncHandler asyncServer)
                await asyncServer.HandleRequestAsync(this).CAF();
            else
                await Task.Run(() => server.HandleRequest(this)).CAF();
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

        private const int MaxInputLineLength = 8 * 1024;
        private readonly byte[] readlineBytes = new byte[4];

        int rawRequestPos = 0;
        private string readLineFromRawRequest()
        {
            if (rawRequestPos >= RawRequest.Length)
                throw new Exception("RawRequest EOF.");
            var endOfLinePos = RawRequest.IndexOf("\r\n", rawRequestPos);
            if (endOfLinePos == -1)
                throw new Exception("CRLF not found.");
            var startPos = rawRequestPos;
            rawRequestPos = endOfLinePos + 2;
            return RawRequest.Substring(startPos, endOfLinePos - startPos);
        }

        private Task<string> readRequest()
        {
            return NaiveUtils.ReadStringUntil(realInputStream, NaiveUtils.DoubleCRLFBytes, readlineBytes, MaxInputLineLength);
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

        public void setStatusCode(string statusCode)
        {
            ThrowIfHeadersEnded(nameof(setStatusCode));
            this.ResponseStatusCode = statusCode;
        }

        public void write(string str)
        {
            outputWriter.Write(str);
        }

        public void writeEncoded(string str)
        {
            outputWriter.Write(HttpUtil.HtmlEncode(str));
        }

        public Task writeAsync(string str)
            => outputWriter.WriteAsync(str);

        public void writeLine(string str)
        {
            outputWriter.WriteLine(str);
        }

        public Task writeLineAsync(string str)
             => outputWriter.WriteLineAsync(str);

        internal async Task writeHeadersToAsync(TextWriter writer)
        {
            await writer.WriteAsync("HTTP/1.1 " + ResponseStatusCode + "\r\n");
            foreach (var key in ResponseHeaders.Keys) {
                await writer.WriteAsync(key + ": " + ResponseHeaders[key] + "\r\n");
            }
            await writer.WriteAsync("\r\n");
        }

        internal void writeHeadersTo(TextWriter writer)
        {
            writeHeadersToAsync(writer).RunSync();
        }
        public int Id => id;
        private int id = id_counter++;
        private static int id_counter = 0;

        public void info(string text) => log(text, Logging.Level.Info);

        public virtual void log(string text, Logging.Level level)
        {
            server.log($"(#{id}|{remoteEP}({RequestCount})) {text}", level);
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
                return $"{{HttpConn #{id}|{remoteEP}({RequestCount})}}";
            else
                return $"{{HttpConn #{id}({RequestCount})}}";
        }

        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue) {
                if (disposing) {
                }

                ResponseHeaders = null;
                RequestHeaders = null;
                outputStream = null;
                outputWriter = null;

                disposedValue = true;
            }
        }

        ~HttpConnection()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
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

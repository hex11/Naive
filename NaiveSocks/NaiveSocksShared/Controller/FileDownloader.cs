using Naive.Console;
using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HttpClient = System.Net.Http.HttpClient;

namespace NaiveSocks
{
    class FileDownloadManager
    {
        class Item
        {
            public int id;
            public CancellationTokenSource cts;
            public FileDownloader dl;
            string statFile;

            public Item(int id, Uri src, string dst)
            {
                dl = new FileDownloader() { SourceUri = src };
                statFile = dst + ".dlstat";
                if (File.Exists(statFile)) {
                    dl.SetState(File.ReadAllText(statFile));
                }
                dl.DestFilePath = dst;
                this.id = id;
            }

            public async void Start()
            {
                if (dl.MainTask?.IsCompleted == false)
                    throw new Exception("Downloader task is already running");
                cts = new CancellationTokenSource();
                dl.CancellationToken = cts.Token;
                dl.Start();
                var logger = new Logger("DL#" + id, Logging.RootLogger);
                var lastTime = Logging.Runtime;
                var lastDl = dl.Downloaded;
                while (true) {
                    if (await dl.MainTask.WithTimeout(5000)) {
                        var curTime = Logging.Runtime;
                        var curDl = dl.Downloaded;
                        logger.info($"{dl.Downloaded:N0}/{dl.Length:N0} | {dl.DownloadingThreads} threads | {(curDl - lastDl) * 1000 / (curTime - lastTime) / 1024:N0} KB/s");
                        lastTime = curTime; lastDl = curDl;
                        File.WriteAllText(statFile, dl.GetState());
                    } else {
                        if (dl.State == DownloadState.Success) {
                            logger.info("Success");
                            File.Delete(statFile);
                        } else {
                            logger.warning("State: " + dl.State);
                        }
                        break;
                    }
                }
            }

            public void Stop()
            {
                cts.Cancel();
            }
        }

        Dictionary<int, Item> tasks = new Dictionary<int, Item>();
        IncrNumberGenerator dlTaskIdGen = new IncrNumberGenerator();
        public void HandleCommand(Command cmd)
        {
            var verb = cmd.ArgOrNull(0);
            if (verb == "new") {
                var src = new Uri(cmd.args[1]);
                var dst = cmd.args[2];
                var id = dlTaskIdGen.Get();
                var item = new Item(id, src, dst);
                if (cmd.args.Length > 3)
                    item.dl.MaxThread = int.Parse(cmd.args[3]);
                tasks.Add(id, item);
                item.Start();
                cmd.WriteLine($"Download task id " + id + " started");
            } else if (verb == "stop") {
                var id = int.Parse(cmd.args[1]);
                tasks[id].Stop();
            } else if (verb == "start") {
                var id = int.Parse(cmd.args[1]);
                tasks[id].Start();
            } else {
                cmd.WriteLine("Usage: dl (new URL FILE|stop ID|start ID)");
            }
        }
    }


    public class FileDownloader
    {
        // Settings:
        public string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.102 Safari/537.36";
        public int MaxThread = 5;
        public int MinRangeSize = 1 * 1024 * 1024; // 1 MiB
        public Uri SourceUri;
        public string DestFilePath;
        public HttpClient HttpClient;
        public CancellationToken CancellationToken;
        public int MaxErrors = 30;

        // Status:
        public Worker MainWorker;
        public DownloadState State;
        public List<Range> Ranges = new List<Range>(); // lock when changing list
        public bool? RangeSupported;
        public long Length = -2; // -2: requesting, -1: unknown (chunked encoding?)
        public long Downloaded;
        public Task MainTask;
        public int Errors;
        DateTime LastErrorTime = DateTime.MinValue;
        public bool IsRunning => MainTask?.IsCompleted == false;

        public int DownloadingThreads
        {
            get {
                int result = 0;
                for (int i = 0; i < Ranges.Count; i++) {
                    if (Ranges[i].Worker?.State == DownloadState.Downloading)
                        result++;
                }
                return result;
            }
        }

        public FileStream DestFile;

        public FileDownloader()
        {
            HttpClient = CreateHttpClient();
        }

        HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            return client;
        }

        public string GetState()
        {
            var sb = new StringBuilder();
            sb.Append(SourceUri).AppendLine();
            sb.Append(DestFilePath).AppendLine();
            sb.Append(Downloaded).AppendLine();
            sb.Append(Length).AppendLine();
            lock (Ranges) {
                foreach (var item in Ranges) {
                    sb.Append(item.Offset).Append("/").Append(item.Current).Append("/");
                    sb.Append(item.Offset + item.Length - 1).Append("/").AppendLine();
                }
            }
            sb.AppendLine(); // end of ranges
            return sb.ToString();
        }

        static readonly char[] seperator = new[] { '/' };

        public void SetState(string state)
        {
            var sr = new MyStringReader(state);
            SourceUri = new Uri(sr.ReadLine());
            DestFilePath = sr.ReadLine();
            sr.ReadLine(); // skip Downloaded, which should be calculated from Ranges
            Length = long.Parse(sr.ReadLine());
            string line;
            while (!string.IsNullOrEmpty(line = sr.ReadLine())) {
                var splits = line.Split(seperator, 4);
                var range = new Range { Offset = long.Parse(splits[0]), Current = long.Parse(splits[1]) };
                range.Length = long.Parse(splits[2]) - range.Offset + 1;
                Ranges.Add(range);
                Downloaded += range.Current;
            }
            if (Downloaded == Length)
                State = DownloadState.Success;
        }

        public Task Start()
        {
            return MainTask = RealStart();
        }

        public async Task RealStart()
        {
            if (State == DownloadState.Success)
                return;
#if DEBUG
            DebugPrinter();
#endif
            State = DownloadState.Requesting;
            try {
                HttpResponseMessage firstResponse = null;
                if (Length == -2) {
                    MainWorker = new Worker(this);
                    firstResponse = await MainWorker.Request(new HttpRequestMessage(HttpMethod.Get, SourceUri));
                    CancellationToken.ThrowIfCancellationRequested();
                    firstResponse.EnsureSuccessStatusCode();
                    RangeSupported = IsRangeSupported(firstResponse);
                    Length = GetLength(firstResponse);
                }
                if (DestFile == null) {
                    State = DownloadState.InitFile;
                    InitFile();
                }
                if (Ranges.Count == 0)
                    InitRanges();
                if (firstResponse != null) {
                    // keep using the first reponse to download the first range
                    Range firstRange = Ranges[0];
                    firstRange.Worker = MainWorker;
                    firstRange.WorkerTask = MainWorker.DownloadRange(firstRange, firstResponse);
                }
                State = DownloadState.Downloading;
                StartWorker();
                await Task.WhenAll(Ranges.Select(x => x.WorkerTask).Where(x => x != null));
            } catch (Exception e) {
                if (CancellationToken.IsCancellationRequested)
                    State = DownloadState.Cancelled;
                else
                    State = DownloadState.Error;
                throw;
            } finally {
                if (DestFile != null) {
                    DestFile.Dispose();
                    DestFile = null;
                }
            }
            // after Task.WhenAll:
            if (Length != -1 && Downloaded == Length) {
                State = DownloadState.Success;
            } else {
                State = DownloadState.Error;
                throw new Exception($"all worker completed but Downloaded({Downloaded}) != Length({Length})");
            }
        }

        async void DebugPrinter()
        {
            while (true) {
                Console.WriteLine("-------------- " + DateTime.Now.ToLongTimeString());
                Console.WriteLine(GetState());
                await Task.Delay(1000);
            }
        }

        private void StartWorker()
        {
            foreach (var range in Ranges) {
                if (range.Length != -1 && range.Remaining == 0)
                    continue;
                // start new worker or restart if failed.
                if (range.Worker == null)
                    range.Worker = new Worker(this);
                if (range.WorkerTask?.IsCompleted != false)
                    range.WorkerTask = range.Worker.DownloadRange(range);
            }
        }

        private void InitFile()
        {
            DestFile = File.Open(DestFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            if (Length > 0)
                DestFile.SetLength(Length);
        }

        private void InitRanges()
        {
            lock (Ranges) {
                if (RangeSupported == true && Length >= MinRangeSize * 2) {
                    var rangeCount = Math.Min(Length / MinRangeSize, MaxThread);
                    var lenPerRange = Length / rangeCount;
                    long cur = 0;
                    for (int i = 0; i < rangeCount - 1; i++) {
                        Ranges.Add(new Range { Offset = cur, Length = lenPerRange });
                        cur += lenPerRange;
                    }
                    Ranges.Add(new Range { Offset = cur, Length = Length - cur });
                } else {
                    Ranges.Add(new Range { Offset = 0, Length = Length });
                }
            }
        }

        private static bool IsRangeSupported(HttpResponseMessage resp)
        {
            return resp.Headers.AcceptRanges.Contains("bytes");
        }

        private static long GetLength(HttpResponseMessage resp)
        {
            if (resp.Headers.TransferEncodingChunked == true)
                return -1;
            var lenStr = resp.Content.Headers.GetValues("Content-Length").SingleOrDefault();
            if (lenStr == null)
                return -1;
            return long.Parse(lenStr);
        }

        long lastCP_Downloaded = -1;

        public string CheckAutoSave()
        {
            if (Length == -2)
                return null;
            var curDl = Downloaded;
            if (curDl == lastCP_Downloaded)
                return null;
            lastCP_Downloaded = curDl;
            return GetState();
        }

        int OnError(Exception e, Worker worker)
        {
            var now = DateTime.Now;
            int wait;
            if (Interlocked.Increment(ref Errors) > MaxErrors)
                wait = -1;
            else if (LastErrorTime == DateTime.MinValue || now - LastErrorTime > TimeSpan.FromSeconds(1))
                wait = 1000;
            else
                wait = 5000;
            LastErrorTime = now;
            return wait;
        }

        public class Range
        {
            public long Offset;
            public long Length;
            public long Current;
            public long CurrentWithOffset => Offset + Current;
            public long Remaining => Length - Current;
            public long End => Offset + Length;
            public Worker Worker;
            public Task WorkerTask;

            public override string ToString()
            {
                return $"{{Range offset={Offset} len={Length} cur={Current} state={Worker?.State} errors={Worker?.Errors}}}";
            }
        }

        public class Worker
        {
            public FileDownloader FileDownloader;
            public DownloadState State;
            public Range WorkingRange;
            public int Errors;

            CancellationToken CancellationToken => FileDownloader.CancellationToken;
            HttpClient HttpClient;

            public Worker(FileDownloader fileDownloader)
            {
                FileDownloader = fileDownloader;
            }

            public Task<HttpResponseMessage> Request(HttpRequestMessage httpRequest)
            {
                if (HttpClient == null)
                    HttpClient = FileDownloader.CreateHttpClient();
                State = DownloadState.Requesting;
                return HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, CancellationToken);
            }

            public async Task DownloadRange(Range range, HttpResponseMessage resp = null)
            {
                WorkingRange = range;
                RETRY:
                try {
                    bool unknownLength = range.Length == -1;
                    if (resp == null) // If there is not a response for this range, do a request to get one.
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, FileDownloader.SourceUri);
                        if (!unknownLength)
                            req.Headers.Range = new RangeHeaderValue(range.CurrentWithOffset, range.Offset + range.Length - 1);
                        resp = await Request(req);
                        CancellationToken.ThrowIfCancellationRequested();
                        if (!unknownLength) {
                            if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) {
                                throw new Exception("got http status code " + resp.StatusCode + ", expected 206.");
                            }
                        } else {
                            resp.EnsureSuccessStatusCode();
                        }
                    }

                    using (var stream = await resp.Content.ReadAsStreamAsync()) {
                        State = DownloadState.Downloading;
                        var fs = FileDownloader.DestFile;
                        const int MaxBufLen = 64 * 1024;
                        int bufLen = unknownLength ? MaxBufLen : (int)Math.Min(range.Remaining, MaxBufLen);
                        var buf = new byte[bufLen];
                        while (range.Remaining > 0 || unknownLength) {
                            int readLen = unknownLength ? bufLen : (int)Math.Min(range.Remaining, bufLen);
                            readLen = await stream.ReadAsync(buf, 0, readLen, CancellationToken);
                            CancellationToken.ThrowIfCancellationRequested();
                            if (readLen == 0) {
                                if (unknownLength)
                                    break;
                                else
                                    throw new Exception("Unexpected end-of-stream");
                            }
                            lock (fs) // use multiple FileStream instances to avoid locking (?)
                            {
                                if (fs.Position != range.CurrentWithOffset)
                                    fs.Position = range.CurrentWithOffset;
                                fs.Write(buf, 0, readLen); // no need to use async IO on file (?)
                                FileDownloader.Downloaded += readLen;
                            }
                            range.Current += readLen; // update Current **after** write to the file
                        }
                    }
                    State = DownloadState.Success;
                } catch (Exception e) {
                    if (CancellationToken.IsCancellationRequested) {
                        State = DownloadState.Cancelled;
                    } else {
                        Errors++;
                        var waitAndRetry = FileDownloader.OnError(e, this);
                        if (waitAndRetry >= 0) {
                            resp = null;
                            State = DownloadState.RetryWaiting;
                            if (waitAndRetry > 0)
                                await Task.Delay(waitAndRetry);
                            goto RETRY;
                            // Yes! It's GOTO, to avoid making a long awaiting chain.
                        }
                        State = DownloadState.Error;
                    }

                    throw;
                }
            }
        }

        struct MyStringReader
        {
            public MyStringReader(string str)
            {
                this.str = str;
                sb = new StringBuilder();
                cur = 0;
            }

            public string str;
            public StringBuilder sb;
            public int cur;

            public string ReadLine()
            {
                if (cur >= str.Length) return null;
                sb.Clear();
                int start = cur;
                while (true) {
                    var ch = str[cur++];
                    if (ch == '\r') continue;
                    else if (ch == '\n') return sb.ToString();
                    else sb.Append(ch);
                }
            }

            public string ReadUntil(char until)
            {
                if (cur >= str.Length) return null;
                sb.Clear();
                int start = cur;
                while (true) {
                    var ch = str[cur++];
                    if (ch == until) return sb.ToString();
                    else sb.Append(ch);
                }
            }
        }
    }

    public enum DownloadState
    {
        None,
        Requesting,
        InitFile,
        Downloading,
        RetryWaiting,
        Success,
        Error,
        Cancelled
    }
}

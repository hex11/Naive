using Naive.HttpSvr;
using Nett;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NaiveSocks
{
    class WebFileAdapter : WebBaseAdapter
    {
        public string dir { get; set; }

        public string dir_hosts { get; set; }

        public string allow { get; set; }
        private bool allow_list, allow_create, allow_edit, allow_netdl;

        public string index { get; set; }
        public string tmpl { get; set; }
        public Dictionary<string, string> data { get; set; }

        public StringOrArray gzip_wildcard { get; set; }
        public bool gzip_listpage { get; set; } = true;

        public ListMode list_mode { get; set; } = ListMode.auto;

        public enum ListMode
        {
            auto, // simple for curl, rich for others
            simple,
            rich,
        }

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("dir", dir);
        }

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            if (allow != null) {
                foreach (var item in allow.Split(' ', ',')) {
                    if (item.IsNullOrEmpty())
                        continue;
                    if (item == "list") {
                        allow_list = true;
                    } else if (item == "create") {
                        allow_create = true;
                    } else if (item == "edit") {
                        allow_edit = true;
                    } else if (item == "netdl") {
                        allow_netdl = true;
                    } else if (item == "all") {
                        Logger.warning("please use 'ALL' instead of 'all' in allow.");
                    } else if (item == "ALL") {
                        allow_list = true;
                        allow_create = true;
                        allow_edit = true;
                        allow_netdl = true;
                    } else {
                        Logger.warning($"unknown '{item}' in allow");
                    }
                }
            }
        }

        object tmplLock = new object();
        NaiveTemplate.Template _tmpl;
        DateTime _tmplLwt;

        private Task HandleFile(HttpConnection p, string realPath)
        {
            if (p.Url_qstr == "dlcancel") {
                p.Handled = true;
                p.setContentTypeTextPlain();
                //if (p.Method != "POST")
                //    return p.writeLineAsync("E: POST needed.");
                if (this.allow_netdl == false) {
                    return p.writeLineAsync($"E: {strMissingPermission("netdl")}.");
                }

                if (downloadTasks.TryGetValue(realPath, out var dlTask)) {
                    dlTask.Cancel();
                    return p.writeLineAsync("task is canceled.");
                } else {
                    return p.writeLineAsync("E: task not found.");
                }
            }
            foreach (var item in gzip_wildcard) {
                if (Wildcard.IsMatch(realPath, item)) {
                    p.outputStream.EnableGzipIfClientSupports();
                    break;
                }
            }
            return WebSvrHelper.HandleFileAsync(p, realPath);
        }

        private Task HandleDir(HttpConnection p, string realPath)
        {
            if (p.Url_qstr == "dllist") {
                if (this.allow_netdl == false) {
                    return p.writeLineAsync($"E: {strMissingPermission("netdl")}.");
                }
                var sb = new StringBuilder();
                foreach (var item in downloadTasks) {
                    if (item.Key.StartsWith(realPath, StringComparison.Ordinal)) {
                        sb.Append(item.Value.StatusText).Append("\n\n");
                    }
                }
                return p.writeAsync(sb.ToString());
            }
            if (p.Method == "POST") {
                return HandleUpload(p, realPath);
            } else {
                return HandleDirList(p, realPath);
            }
        }

        private Task HandleDirList(HttpConnection pp, string path, string infoText = null)
        {
            if (list_mode == ListMode.simple
                || (list_mode == ListMode.auto && pp.GetReqHeader("User-Agent").StartsWith("curl/"))) {
                return SimpleList(pp, path, infoText);
            } else {
                return RichList(pp, path, infoText);
            }
        }

        private Task SimpleList(HttpConnection pp, string path, string infoText)
        {
            var sb = new StringBuilder(128);
            int dirs = 0, files = 0;
            foreach (var dir in Directory.EnumerateDirectories(path)) {
                sb.Append("[dir]\t").Append(Path.GetFileName(dir)).Append("/\r\n");
                dirs++;
            }
            foreach (var file in Directory.EnumerateFiles(path)) {
                sb.Append("[file]\t").Append(Path.GetFileName(file)).Append("\r\n");
                files++;
            }
            if (sb.Length > 0) sb.Append("\r\n");
            sb.Append("[stat]\t").Append(dirs).Append(" dir(s), ").Append(files).Append(" file(s)\r\n");
            if (infoText != null) {
                sb.Append("[info]\t").Append(infoText).Append("\r\n");
            } else if (pp.Url_qstr == "help") {
                sb.Append("[help]\tcurl [-F <OPTION>]... <DIR_URL>\r\n" +
                    "\tOptions:\r\n" +
                    "\tfile=@FILE_TO_UPLOAD  mkdir=DIR_NAME  cp=\"(FROM...) TO\"\r\n" +
                    "\tmv=\"(FROM...) TO\"  rm=\"(FILE|DIR)...\"  netdl=\"URL [FILENAME]\"\r\n" +
                    "\ttextFileName=NEW_TEXT_FILE_NAME  textContent=NEW_TEXT_FILE_CONTENT\r\n");
            } else if (allow_create || allow_edit) {
                sb.Append("[tip]\tGET with ?help\r\n");
            }
            pp.setContentTypeTextPlain();
            return pp.EndResponseAsync(sb.ToString());
        }

        private Task RichList(HttpConnection pp, string path, string infoText)
        {
            lock (tmplLock) {
                var tmpl = Controller.ProcessFilePath(this.tmpl);
                if (tmpl != null && File.Exists(tmpl)) {
                    var fi = new FileInfo(tmpl);
                    DateTime lwt = fi.LastWriteTimeUtc;
                    if (_tmplLwt != lwt) {
                        _tmpl = new NaiveTemplate.Template(File.ReadAllText(tmpl, Encoding.UTF8)).TrimExcess();
                        _tmplLwt = lwt;
                    }
                } else {
                    _tmpl = null;
                }
            }
            var dat = new Dictionary<string, object>();
            if (data != null)
                foreach (var item in data)
                    dat.Add(item.Key, item.Value);
            if (infoText != null) {
                dat["info"] = infoText;
            }
            dat["can_upload"] = allow_create | allow_edit;
            if (gzip_listpage)
                pp.outputStream.EnableGzipIfClientSupports();
            return WebSvrHelper.WriteDirListPage(pp, path, _tmpl, new NaiveTemplate.TemplaterData(dat));
        }

        public override async Task HandleRequestAsyncImpl(HttpConnection p)
        {
            var realPath = p.Url_path;
            if (index != null && p.Url_path == "/") {
                p.Url_path = index;
            }
            try {
                string dirPath = null;
                if (dir_hosts != null) {
                    string hosts = Controller.ProcessFilePath(dir_hosts);
                    string host = p.Host;
                    // TODO: check host for security
                    var rr = WebSvrHelper.CheckPath(hosts, host, out var hostsSubDir);
                    if (rr == WebSvrHelper.PathResult.Directory) {
                        dirPath = hostsSubDir;
                    }
                }
                if (dirPath == null) dirPath = Controller.ProcessFilePath(dir);
                WebSvrHelper.PathResult r = WebSvrHelper.CheckPath(dirPath, p.Url_path, out var fsFilePath);
                if (p.Url_qstr == "dlstatus" && (r == WebSvrHelper.PathResult.File || r == WebSvrHelper.PathResult.NotFound)) {
                    if (downloadTasks.TryGetValue(fsFilePath, out var dlTask)) {
                        p.Handled = true;
                        p.setContentTypeTextPlain();
                        await p.writeLineAsync(dlTask.StatusText);
                    }
                } else if (r == WebSvrHelper.PathResult.File) {
                    p.Handled = true;
                    p.ResponseStatusCode = "200 OK";
                    await HandleFile(p, fsFilePath);
                } else if (r == WebSvrHelper.PathResult.Directory && allow_list) {
                    p.Handled = true;
                    p.ResponseStatusCode = "200 OK";
                    await HandleDir(p, fsFilePath);
                }
            } finally {
                p.Url_path = realPath;
            }
        }

        class DownloadTask
        {
            public DownloadTask(string url, string filePath)
            {
                Url = url;
                FilePath = filePath;
            }

            private WebClient webcli;

            public string Url { get; }
            public string FilePath { get; }

            public long TotalBytes { get; private set; }
            public long ReceivedBytes { get; private set; }

            public States State { get; private set; } = States.init;

            public DateTime StartTimeUtc;
            public DateTime StopTimeUtc = DateTime.MinValue;

            public enum States
            {
                init,
                running,
                canceled,
                error,
                success
            }

            public string StatusText => State + "\n"
                + ReceivedBytes + "/" + TotalBytes + "\n"
                + StartTimeUtc.ToString("R") + "\n"
                + ((StopTimeUtc == DateTime.MinValue ? DateTime.UtcNow : StopTimeUtc) - StartTimeUtc).TotalMilliseconds.ToString("#");

            public async Task Start()
            {
                try {
                    using (webcli = new WebClient()) {
                        webcli.DownloadProgressChanged += (s, e) => {
                            TotalBytes = e.TotalBytesToReceive;
                            ReceivedBytes = e.BytesReceived;
                        };
                        StartTimeUtc = DateTime.UtcNow;
                        State = States.running;
                        await webcli.DownloadFileTaskAsync(Url, FilePath);
                        State = States.success;
                    }
                } catch (Exception) {
                    if (State != States.canceled)
                        State = States.error;
                    throw;
                } finally {
                    StopTimeUtc = DateTime.UtcNow;
                    webcli = null;
                }
            }

            public void Cancel()
            {
                State = States.canceled;
                webcli?.CancelAsync();
            }
        }

        static Dictionary<string, DownloadTask> downloadTasks = new Dictionary<string, DownloadTask>();
        static ReaderWriterLockSlim downloadTasksLock = new ReaderWriterLockSlim();

        private async Task HandleUpload(HttpConnection p, string path)
        {
            bool responseListPage = p.ParseUrlQstr()["infoonly"] != "1";
            string info = null;
            if (!(allow_create | allow_edit)) {
                info = $"{strMissingPermission("create")} and {strMissingPermission("edit")}.";
                goto FAIL;
            }
            var reader = new MultipartFormDataReader(p);
            int count = 0;
            string saveFileName = null;
            string encoding = "utf-8";
            while (await reader.ReadNextPartHeader()) {
                if (reader.CurrentPartName == "file") {
                    var fileName = reader.CurrentPartFileName;
                    if (!CheckPathForWriting(path, fileName, out info, out var realPath))
                        goto FAIL;
                    if (!TryOpenFile(path, fileName + ".uploading", out var fs, out info, out var tempPath))
                        goto FAIL;
                    try {
                        using (fs) {
                            await NaiveUtils.StreamCopyAsync(reader, fs);
                        }
                        MoveOrReplace(tempPath, realPath);
                    } catch (Exception e) {
                        Logger.exception(e, Logging.Level.Warning, $"receiving file '{fileName}' from {p.myStream}.");
                        File.Delete(realPath);
                        if (e is DisconnectedException)
                            return;
                        info = $"IO error on '{saveFileName}'";
                        goto FAIL;
                    }
                    Logger.info($"uploaded '{fileName}' by {p.myStream}.");
                    count++;
                } else if (reader.CurrentPartName.Is("textFileName").Or("fileName")) {
                    saveFileName = await reader.ReadAllTextAsync();
                } else if (reader.CurrentPartName == "textFileEncoding") {
                    encoding = await reader.ReadAllTextAsync();
                } else if (reader.CurrentPartName == "textContent") {
                    if (!CheckPathForWriting(path, saveFileName, out info, out var realPath))
                        goto FAIL;
                    if (!TryOpenFile(path, saveFileName + ".uploading", out var fs, out info, out var tempPath))
                        goto FAIL;
                    try {
                        using (fs) {
                            if (encoding == "utf-8") {
                                await NaiveUtils.StreamCopyAsync(reader, fs, bs: 8 * 1024);
                            } else {
                                using (var sr = new StreamReader(reader)) {
                                    using (var sw = new StreamWriter(fs, Encoding.GetEncoding(encoding))) {
                                        const int bufLen = 8 * 1024;
                                        var buf = new char[bufLen];
                                        var read = await sr.ReadAsync(buf, 0, bufLen);
                                        await sw.WriteAsync(buf, 0, read);
                                    }
                                }
                            }
                        }
                        MoveOrReplace(tempPath, realPath);
                    } catch (Exception e) {
                        Logger.exception(e, Logging.Level.Warning, $"receiving text file '{saveFileName}' from {p.myStream}.");
                        File.Delete(tempPath);
                        if (e is DisconnectedException)
                            return;
                        info = $"IO error on '{saveFileName}'";
                        goto FAIL;
                    }
                    Logger.info($"uploaded text '{saveFileName}' by {p.myStream}.");
                    count++;
                } else if (reader.CurrentPartName == "mkdir") {
                    var dirName = await reader.ReadAllTextAsync();
                    if (!CheckPathForWriting(path, dirName, out info, out var realPath))
                        goto FAIL;
                    try {
                        Directory.CreateDirectory(realPath);
                    } catch (Exception) {
                        info = $"Failed to create directory '{dirName}'";
                        goto FAIL;
                    }
                    Logger.info($"created dir '{dirName}' by {p.myStream}.");
                    count++;
                } else if (reader.CurrentPartName == "rm") {
                    var delFile = await reader.ReadAllTextAsync();
                    if (!CheckPathForWriting(path, delFile, out info, out var realPath, out var r))
                        goto FAIL;
                    try {
                        if (r == WebSvrHelper.PathResult.Directory) {
                            Directory.Delete(realPath);
                        } else if (r == WebSvrHelper.PathResult.File) {
                            File.Delete(realPath);
                        } else {
                            info = $"Failed to delete '{delFile}' (not found).";
                            goto FAIL;
                        }
                    } catch (Exception) {
                        info = $"Failed to delete '{delFile}'";
                        goto FAIL;
                    }
                    Logger.info($"deleted '{delFile}' by {p.myStream}.");
                    count++;
                } else if (reader.CurrentPartName == "netdl") {
                    if (!allow_netdl) {
                        info = $"{strMissingPermission("netdl")}.";
                        goto FAIL;
                    }
                    var unparsed = await reader.ReadAllTextAsync();
                    var args = Naive.Console.Command.SplitArguments(unparsed);
                    string url, name;
                    if (args.Length == 0 || args.Length > 2) {
                        info = $"wrong arguments.";
                        goto FAIL;
                    }
                    url = args[0];
                    if (args.Length == 2) {
                        name = args[1];
                    } else {
                        int startIndex = url.LastIndexOf('/');
                        if (startIndex < 0) {
                            info = "can not determine a filename from the given url, please specify one.";
                            goto FAIL;
                        }
                        name = url.Substring(startIndex);
                    }
                    if (!CheckPathForWriting(path, name, out info, out var realPath, out var r))
                        goto FAIL;
                    if (r == WebSvrHelper.PathResult.Directory) {
                        name = Path.Combine(url.Substring(url.LastIndexOf('/')), name);
                        if (!CheckPathForWriting(path, name, out info, out realPath))
                            goto FAIL;
                    }
                    if (!CheckPathForWriting(path, name + ".downloading", out info, out var realDlPath))
                        goto FAIL;

                    var dlTask = new DownloadTask(url, realDlPath);

                    // double checked locking, add the new task into the dict if no task already exist.
                    downloadTasksLock.EnterReadLock();
                    var taskExists = downloadTasks.TryGetValue(realDlPath, out var oldTask);
                    downloadTasksLock.ExitReadLock();

                    if (!taskExists) {
                        downloadTasksLock.EnterWriteLock();
                        taskExists = downloadTasks.TryGetValue(realDlPath, out oldTask);
                        if (!taskExists) {
                            downloadTasks.Add(realDlPath, dlTask);
                        }
                        downloadTasksLock.ExitWriteLock();
                    }

                    if (taskExists) {
                        if (oldTask.State <= DownloadTask.States.running) {
                            info = "a task is already running on this path.";
                            goto FAIL;
                        } else {
                            info = $"override a '{oldTask.State}' task.\n";
                            downloadTasksLock.EnterWriteLock();
                            downloadTasks[realDlPath] = dlTask;
                            downloadTasksLock.ExitWriteLock();
                        }
                    }

                    try {
                        var t = NaiveUtils.RunAsyncTask(async () => {
                            try {
                                await dlTask.Start();
                                MoveOrReplace(realDlPath, realPath);
                            } finally {
                                // Whether success or not, remove the task from list after 5 minutes.
                                AsyncHelper.SetTimeout(5 * 60 * 1000, () => {
                                    downloadTasksLock.EnterWriteLock();
                                    downloadTasks.Remove(realDlPath);
                                    downloadTasksLock.ExitWriteLock();
                                });
                            }
                        });
                        if (await t.WithTimeout(200)) {
                            info += "downloading task is started.";
                        } else {
                            await t;
                            info += "downloaded.";
                        }
                    } catch (Exception e) {
                        Logger.exception(e, Logging.Level.Warning, $"downloading from '{url}' to '{realDlPath}'");
                        info += "downloading failed.";
                        goto FAIL;
                    }
                    count++;
                } else if (reader.CurrentPartName.Is("mv").Or("cp")) {
                    var unparsed = await reader.ReadAllTextAsync();
                    var args = Naive.Console.Command.SplitArguments(unparsed);
                    if (args.Length < 2) {
                        info = $"too few arguments";
                        goto FAIL;
                    }
                    string to = args[args.Length - 1];
                    if (!CheckPathForWriting(path, to, out info, out var toPath, out var toType))
                        goto FAIL;
                    bool multipleFrom = args.Length > 2;
                    if (multipleFrom && toType != WebSvrHelper.PathResult.Directory) {
                        info = "multiple 'from' when 'to' is not a directory!";
                        goto FAIL;
                    }
                    for (int i = 0; i < args.Length - 1; i++) {
                        string from = args[i];
                        if (!CheckPathForWriting(path, from, out info, out var fromPath))
                            goto FAIL;
                        string toFilePath = toType == WebSvrHelper.PathResult.Directory
                            ? Path.Combine(toPath, Path.GetFileName(from))
                            : toPath;
                        // TODO: refine directory moving/copying
                        try {
                            if (reader.CurrentPartName == "mv") {
                                MoveOrReplace(fromPath, toFilePath);
                            } else {
                                File.Copy(fromPath, toFilePath, true);
                            }
                        } catch (Exception e) {
                            Logger.exception(e, Logging.Level.Warning, $"file mv/cp from  '{fromPath}' to '{toFilePath}' by {p.myStream}.");
                            info = "error occurred during file operation at " + from;
                            goto FAIL;
                        }
                        count++;
                    }
                } else if (reader.CurrentPartName == "rmm") {
                    var unparsed = await reader.ReadAllTextAsync();
                    var args = Naive.Console.Command.SplitArguments(unparsed);
                    if (args.Length == 0) {
                        info = "no arguments.";
                        goto FAIL;
                    }
                    foreach (var delFile in args) {
                        if (!CheckPathForWriting(path, delFile, out info, out var realPath, out var r))
                            goto FAIL;
                        try {
                            if (r == WebSvrHelper.PathResult.Directory) {
                                Directory.Delete(realPath);
                            } else if (r == WebSvrHelper.PathResult.File) {
                                File.Delete(realPath);
                            } else {
                                info = $"Failed to delete '{delFile}' (not found)";
                                goto FAIL;
                            }
                        } catch (Exception) {
                            info = $"Failed to delete '{delFile}'";
                            goto FAIL;
                        }
                        Logger.info($"deleted '{delFile}' by {p.myStream}.");
                        count++;
                    }
                } else if (reader.CurrentPartName.Is("infoonly").Or("isajax")) {
                    responseListPage = false;
                } else {
                    info = $"Unknown part name '{reader.CurrentPartName}'";
                    goto FAIL;
                }
            }
            if (info == null || count > 1)
                info = $"Finished {count} operation{(count > 1 ? "s" : null)}.";
            FAIL:
            if (responseListPage) {
                await HandleDirList(p, path, info);
            } else {
                p.Handled = true;
                p.setContentTypeTextPlain();
                await p.writeLineAsync(info);
            }
        }

        private static void MoveOrReplace(string from, string to)
        {
            if (File.Exists(to)) {
                File.Replace(from, to, null);
            } else {
                Directory.Move(from, to);
            }
        }

        private bool TryOpenFile(string basePath, string relPath, out Stream fs, out string failReason, out string realPath)
        {
            fs = null;
            if (!CheckPathForWriting(basePath, relPath, out failReason, out realPath))
                return false;
            try {
                fs = File.Open(realPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            } catch (Exception e) {
                failReason = ($"Can not open '{relPath}' for writing.");
                return false;
            }
            return true;
        }

        bool CheckPathForWriting(string basePath, string relPath, out string failReason, out string realPath)
            => CheckPathForWriting(basePath, relPath, out failReason, out realPath, out _);

        bool CheckPathForWriting(string basePath, string relPath, out string failReason, out string realPath, out WebSvrHelper.PathResult r)
        {
            r = WebSvrHelper.PathResult.IllegalPath;
            failReason = null;
            if (relPath.IsNullOrEmpty()) {
                failReason = "Empty filename.";
                realPath = null;
                return false;
            }
            r = WebSvrHelper.CheckPath(basePath, relPath, out realPath);
            if (r == WebSvrHelper.PathResult.IllegalPath) {
                failReason = ($"Illegal filename '{relPath}'");
                return false;
            }
            if ((r == WebSvrHelper.PathResult.File || r == WebSvrHelper.PathResult.Directory)
                && !allow_edit) {
                failReason = ($"File '{relPath}' exists and {strMissingPermission("edit")}.");
                return false;
            }
            if (r == WebSvrHelper.PathResult.NotFound && !allow_create) {
                failReason = ($"File '{relPath}' doesn't exist and {strMissingPermission("create")}.");
                return false;
            }
            return true;
        }

        private static string strMissingPermission(string perm) => $"'{perm}' is not in the allowed list";
    }
}

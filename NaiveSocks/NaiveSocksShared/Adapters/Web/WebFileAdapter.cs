using Naive.HttpSvr;
using Nett;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks
{
    class WebFileAdapter : WebBaseAdapter
    {
        public string dir { get; set; }

        public string allow { get; set; }
        private bool allow_list, allow_create, allow_edit, allow_netdl;

        public string index { get; set; }
        public string tmpl { get; set; }
        public Dictionary<string, string> data { get; set; }

        object tmplLock = new object();
        NaiveTemplate.Template _tmpl;
        DateTime _tmplLwt;

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

        public override async Task HandleRequestAsyncImpl(HttpConnection p)
        {
            Task hitdir(HttpConnection pp, string path)
            {
                if (pp.Method == "POST") {
                    if (pp.ParseUrlQstr()["upload"] != "0") {
                        return HandleUpload(pp, path);
                    }
                    return AsyncHelper.CompletedTask;
                } else {
                    return HandleDirList(pp, path);
                }
            }
            var realPath = p.Url_path;
            if (index != null && p.Url_path == "/") {
                p.Url_path = index;
            }
            try {
                await WebSvrHelper.HandleDirectoryAsync(p, Controller.ProcessFilePath(dir),
                    WebSvrHelper.HandleFileAsync, allow_list ? hitdir : (Func<HttpConnection, string, Task>)null);
            } finally {
                p.Url_path = realPath;
            }
        }

        private async Task HandleUpload(HttpConnection pp, string path)
        {
            string info = null;
            if (!(allow_create | allow_edit)) {
                info = $"Uploading is not allowed.";
                goto FAIL;
            }
            var reader = new MultipartFormDataReader(pp);
            int count = 0;
            string saveFileName = null;
            string encoding = "utf-8";
            while (await reader.ReadNextPartHeader()) {
                if (reader.CurrentPartName == "files") {
                    var fileName = reader.CurrentPartFileName;
                    if (!TryOpenFile(path, fileName, out var fs, out info, out var realPath))
                        goto FAIL;
                    try {
                        using (fs) {
                            await NaiveUtils.StreamCopyAsync(reader, fs);
                        }
                    } catch (Exception e) {
                        File.Delete(realPath);
                        if (e is DisconnectedException)
                            return;
                        info = "read/wring error";
                        goto FAIL;
                    }
                    Logger.info($"uploaded '{fileName}' by {pp.myStream}.");
                    count++;
                } else if (reader.CurrentPartName.Is("textFileName").Or("fileName")) {
                    saveFileName = await reader.ReadAllTextAsync();
                } else if (reader.CurrentPartName == "textFileEncoding") {
                    encoding = await reader.ReadAllTextAsync();
                } else if (reader.CurrentPartName == "textContent") {
                    if (!TryOpenFile(path, saveFileName, out var fs, out info, out var realPath))
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
                    } catch (Exception e) {
                        File.Delete(realPath);
                        if (e is DisconnectedException)
                            return;
                        info = $"read/write error on '{saveFileName}'";
                        goto FAIL;
                    }
                    Logger.info($"uploaded text '{saveFileName}' by {pp.myStream}.");
                    count++;
                } else if (reader.CurrentPartName == "dirName") {
                    var dirName = await reader.ReadAllTextAsync();
                    if (!CheckPathForWriting(path, dirName, out info, out var realPath))
                        goto FAIL;
                    try {
                        Directory.CreateDirectory(realPath);
                    } catch (Exception) {
                        info = $"Failed to create directory '{dirName}'";
                        goto FAIL;
                    }
                    Logger.info($"created dir '{dirName}' by {pp.myStream}.");
                    count++;
                } else if (reader.CurrentPartName == "delFile") {
                    var delFile = await reader.ReadAllTextAsync();
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
                    Logger.info($"deleted '{delFile}' by {pp.myStream}.");
                    count++;
                } else if (reader.CurrentPartName == "netdl") {
                    if (!allow_netdl) {
                        info = $"'netdl' is not in allowed list.";
                        goto FAIL;
                    }
                    var unparsed = await reader.ReadAllTextAsync();
                    var args = Naive.Console.Command.SplitArguments(unparsed);
                    string url, name;
                    if (args.Length == 0 || args.Length > 2) {
                        info = $"wrong count of arguments";
                        goto FAIL;
                    }
                    url = args[0];
                    if (args.Length == 2) {
                        name = args[1];
                    } else {
                        name = url.Substring(url.LastIndexOf('/'));
                    }
                    if (!CheckPathForWriting(path, name, out info, out var realPath))
                        goto FAIL;
                    if (!CheckPathForWriting(path, name + ".downloading", out info, out var realDlPath))
                        goto FAIL;
                    var t = NaiveUtils.RunAsyncTask(async () => {
                        using (var webcli = new WebClient()) {
                            await webcli.DownloadFileTaskAsync(url, realDlPath);
                        }
                        MoveOrReplace(realDlPath, realPath);
                    });
                    if (await t.WithTimeout(200)) {
                        info = "downloading task is started.";
                    } else {
                        info = "downloaded file.";
                    }
                    count++;
                } else if (reader.CurrentPartName.Is("mv").Or("cp")) {
                    var unparsed = await reader.ReadAllTextAsync();
                    var args = Naive.Console.Command.SplitArguments(unparsed);
                    if (args.Length != 2) {
                        info = $"wrong count of arguments";
                        goto FAIL;
                    }
                    string from = args[0], to = args[1];
                    if (!CheckPathForWriting(path, from, out info, out var fromPath))
                        goto FAIL;
                    if (!CheckPathForWriting(path, to, out info, out var toPath))
                        goto FAIL;
                    if (reader.CurrentPartName == "mv") {
                        MoveOrReplace(fromPath, toPath);
                    } else {
                        File.Copy(fromPath, toPath, true);
                    }
                    count++;
                } else if (reader.CurrentPartName == "rm") {
                    var unparsed = await reader.ReadAllTextAsync();
                    var args = Naive.Console.Command.SplitArguments(unparsed);
                    if (args.Length == 0) {
                        info = $"no arguments.";
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
                        Logger.info($"deleted '{delFile}' by {pp.myStream}.");
                        count++;
                    }
                } else {
                    info = $"Unknown part name '{reader.CurrentPartName}'";
                    goto FAIL;
                }
            }
            if (info == null || count > 1)
                info = $"Finished {count} operation{(count > 1 ? "s" : null)}.";
            FAIL:
            await HandleDirList(pp, path, info);
        }

        private static void MoveOrReplace(string from, string to)
        {
            if (File.Exists(to)) {
                File.Replace(from, to, null);
            } else {
                File.Move(from, to);
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
                failReason = ($"Can not open '{relPath}'.");
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
                failReason = ($"File '{relPath}' exists and no allow = 'edit'.");
                return false;
            }
            return true;
        }

        private Task HandleDirList(HttpConnection pp, string path, string infoText = null)
        {
            lock (tmplLock) {
                var tmpl = Controller.ProcessFilePath(this.tmpl);
                if (tmpl != null && File.Exists(tmpl)) {
                    var fi = new FileInfo(tmpl);
                    DateTime lwt = fi.LastWriteTimeUtc;
                    if (_tmplLwt != lwt) {
                        _tmpl = new NaiveTemplate.Template(File.ReadAllText(tmpl, Encoding.UTF8));
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
            return WebSvrHelper.WriteDirListPage(pp, path, _tmpl, new NaiveTemplate.TemplaterData(dat));
        }
    }
}

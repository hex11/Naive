using NaiveTemplate;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    public static class WebSvrHelper
    {

        public static NameValueCollection ParseUrlQstr(this HttpConnection p)
        {
            return p.ParsedQstr;
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
            return p.RawRequest;
        }

        public static bool HandleIfNotModified(this HttpConnection p, string lastModified = null, string etag = null)
        {
            if (!DateTime.TryParse(lastModified, out var dt))
                dt = DateTime.MinValue;
            return HandleIfNotModified(p, dt, etag, lastModified);
        }


        public static bool HandleIfNotModified(this HttpConnection p, DateTime lastModified, string etag)
            => HandleIfNotModified(p, lastModified, etag, lastModified.ToString("R"));

        public static bool HandleIfNotModified(this HttpConnection p, DateTime lastModified, string etag, string lastModifiedString)
        {
            if (lastModified != null) {
                p.setHeader("Last-Modified", lastModifiedString);
            }
            if (etag != null) {
                p.setHeader("Etag", etag);
            }
            //p.setHeader("Cache-Control", "public,max-age=3600");
            var notmod = (etag != null && p.GetReqHeader("If-None-Match") == etag);
            if (!notmod) {
                if (lastModified > DateTime.MinValue) {
                    var sinceStr = p.GetReqHeader("If-Modified-Since");
                    if (sinceStr != null && DateTime.TryParse(sinceStr, out var since)) {
                        if (since <= lastModified)
                            notmod = true;
                    }
                }
            }
            if (notmod)
                p.ResponseStatusCode = "304 Not Modified";
            return notmod;
        }

        public static Task HandleDirectoryAsync(HttpConnection p, string dirpath, bool allowListDir = true)
        {
            return HandleDirectoryAsync(p, dirpath, HandleFileAsync, allowListDir);
        }

        public static Task HandleDirectoryAsync(HttpConnection p, string dirpath, Func<HttpConnection, string, Task> hitFile = null, bool allowListDir = true)
        {
            return HandleDirectoryAsync(p, dirpath, hitFile, allowListDir ? (Func<HttpConnection, string, Task>)WriteDirListPage : null);
        }

        public static Task HandleDirectoryAsync(HttpConnection p, string dirpath,
            Func<HttpConnection, string, Task> hitFile, Func<HttpConnection, string, Task> hitDir)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));
            if (dirpath == null)
                throw new ArgumentNullException(nameof(dirpath));

            var r = CheckPath(dirpath, p.Url_path, out var path);
            if (r == PathResult.IllegalPath)
                return AsyncHelper.CompletedTask;
            if (r == PathResult.File && hitFile != null) {
                p.Handled = true;
                p.ResponseStatusCode = "200 OK";
                return hitFile(p, path);
            } else if (r == PathResult.Directory && hitDir != null) {
                p.Handled = true;
                p.ResponseStatusCode = "200 OK";
                return hitDir(p, path);
            } else {
                //p.ResponseStatusCode = "404 Not Found";
                //p.writeLine("file not found");
            }
            return AsyncHelper.CompletedTask;
        }

        public enum PathResult
        {
            IllegalPath,
            NotFound,
            File,
            Directory
        }

        public static PathResult CheckPath(string dirPath, string relPath, out string path)
        {
            relPath = relPath.TrimStart('/');
            if (relPath.Contains("..") || Path.IsPathRooted(relPath)) {
                path = null;
                return PathResult.IllegalPath;
            }
            path = Path.Combine(dirPath, relPath);
            if (File.Exists(path)) {
                return PathResult.File;
            } else if (Directory.Exists(path)) {
                return PathResult.Directory;
            }
            return PathResult.NotFound;
        }

        public static Task WriteDirListPage(HttpConnection p, string path)
            => WriteDirListPage(p, path, null, null);

        public static Task WriteDirListPage(HttpConnection p, string path, Template tmpl)
            => WriteDirListPage(p, path, tmpl, null);

        public static async Task WriteDirListPage(HttpConnection p, string path, Template tmpl, TemplaterData data)
        {
            tmpl = tmpl ?? lazyListTmpl.Value;
            data = data ?? new TemplaterData();
            SetDirListData(p, path, data);
            await Engine.Instance.RunAsync(tmpl, data, p.outputWriter);
        }

        public static void SetDirListData(HttpConnection p, string path, TemplaterData data)
        {
            data.Add("list", async x => {
                try {
                    await WriteDirList(x, path, p.Url_path != "/");
                } catch (UnauthorizedAccessException e) {
                    await x.WriteAsync($"<p>Unauthorized</p>");
                }
            });
            string prefix = null;
            if (p.RealPathEscaped.EndsWith("/")) {
                data.Add("upPath", p.Url_path == "/" ? null : "../");
            } else {
                data.Add("upPath", p.Url_path == "/" ? null : "./");
                int slashIndex = p.RealPathEscaped.LastIndexOf('/');
                if (slashIndex >= 0)
                    prefix = p.RealPathEscaped.Substring(slashIndex + 1) + "/";
            }
            data.Add("dirPath", p.Url_path);
            var subData = new TemplaterData();
            data.Add("dirs", Directory.EnumerateDirectories(path).Select(x => {
                var name = Path.GetFileName(x);
                subData.Dict["url"] = prefix + HttpUtil.UrlEncode(name);
                subData.Dict["name"] = HttpUtil.HtmlAttributeEncode(name);
                return subData;
            }));
            data.Add("files", Directory.EnumerateFiles(path).Select(x => {
                var fi = new FileInfo(x);
                var name = fi.Name;
                subData.Dict["url"] = prefix + HttpUtil.UrlEncode(name);
                subData.Dict["name"] = HttpUtil.HtmlAttributeEncode(name);
                long length;
                try {
                    length = fi.Length;
                } catch (Exception) {
                    length = -1;
                }
                subData.Dict["size_n"] = length.ToString("N0");
                return subData;
            }));
        }

        static Lazy<Template> lazyListTmpl = new Lazy<Template>(() => new Template(GetListTmplString()), true);
        static Template dirTmpl = new Template("<div class='item dir'><a href='{{url}}/'>{{name}}/</a></div>\n");
        static Template fileTmpl = new Template("<div class='item file'><a href='{{url}}'>{{name}}</a></div>\n");

        static string GetListTmplString()
        {
            var name = ".HttpSvr.ListPage.html";
            System.Reflection.Assembly assembly = typeof(WebSvrHelper).Assembly;
            var stream = assembly.GetManifestResourceStream("NaiveSvrLib" + name);
            if (stream == null) {
                var name2 = assembly.GetManifestResourceNames().First((x) => x.EndsWith(name));
                if (name2 == null || (stream = assembly.GetManifestResourceStream(name2)) == null) {
                    Logging.logWithStackTrace("Unable to load the default listpage template", Logging.Level.Error);
                    return "";
                }
            }
            using (stream) {
                return stream.ReadAllText();
            }
        }

        public static async Task WriteDirList(TextWriter p, string path, bool withUpDir)
        {
            if (withUpDir) {
                await p.WriteLineAsync("<div class='item dir up'><a href=\"../\">../</a></div>");
            }
            var data = new TemplaterData();
            foreach (var item in Directory.EnumerateDirectories(path)) {
                var name = Path.GetFileName(item);
                data.Dict["url"] = HttpUtil.UrlEncode(name);
                data.Dict["name"] = HttpUtil.HtmlAttributeEncode(name);
                await Engine.Instance.RunAsync(dirTmpl, data, p);
            }
            foreach (var item in Directory.EnumerateFiles(path)) {
                var name = Path.GetFileName(item);
                data.Dict["url"] = HttpUtil.UrlEncode(name);
                data.Dict["name"] = HttpUtil.HtmlAttributeEncode(name);
                await Engine.Instance.RunAsync(fileTmpl, data, p);
                //await p.WriteLineAsync($"<div class='item file'><a href=\"{HttpUtil.UrlEncode(name)}\">{HttpUtil.HtmlAttributeEncode(name)}</a></div>");
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
                await HandleFileStreamAsync(p, fs, (type == null ? fi.Name : null), false);
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
                if (path.EndsWith(item.Key, StringComparison.OrdinalIgnoreCase))
                    return item.Value;
            }
            return null;
        }


        public static Task HandleFileDownloadAsync(HttpConnection p, string path)
            => HandleFileDownloadAsync(p, path, new FileInfo(path));
        public static Task HandleFileDownloadAsync(HttpConnection p, string path, FileInfo fileInfo)
            => HandleFileDownloadAsync(p, path, fileInfo.Name);
        public static async Task HandleFileDownloadAsync(HttpConnection p, string path, string fileName)
        {
            if (p.Method.Is("GET").Or("HEAD").IsFalse) {
                throw new Exception($"method is neither 'GET' nor 'HEAD' but '{p.Method}'");
            }
            p.Handled = true;
            p.outputStream.CurrentCompressionType = CompressionType.None;
            if (File.Exists(path) == false) {
                p.ResponseStatusCode = "404 Not Found";
                return;
            }
            using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                p.ResponseStatusCode = "200 OK";
                await HandleFileStreamAsync(p, fs, fileName, true);
            }
        }

        public static Task HandleFileStreamAsync(this HttpConnection p, Stream fs, string fileName, bool setContentType)
        {
            if (p.Method.Is("GET").Or("HEAD").IsFalse) {
                throw new Exception($"method is neither 'GET' nor 'HEAD' but '{p.Method}'");
            }
            if (setContentType)
                p.ResponseHeaders[HttpHeaders.KEY_Content_Type] = "application/octet-stream";
            if (fileName != null)
                p.ResponseHeaders["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
            return HandleSeekableStreamAsync(p, fs);
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
            if (p.Method == "HEAD") {
                await p.outputStream.SwitchToKnownLengthModeAsync(fileLength);
                await p.EndResponseAsync();
            } else {
                long realLength = endpos - beginpos + 1;
                if (p.outputStream.CurrentCompressionType == CompressionType.None) {
                    await p.outputStream.SwitchToKnownLengthModeAsync(realLength);
                } else {
                    await p.outputStream.SwitchToChunkedModeAsync();
                }
                stream.Position = beginpos;
                await NaiveUtils.StreamCopyAsync(from: stream, to: p.outputStream,
                    size: realLength, bs: (int)Math.Min(64 * 1024, realLength));
            }
        }
    }
}

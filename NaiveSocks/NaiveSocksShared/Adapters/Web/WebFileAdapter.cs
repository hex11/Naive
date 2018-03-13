using Naive.HttpSvr;
using Nett;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks
{
    class WebFileAdapter : WebBaseAdapter
    {
        public string dir { get; set; }
        public bool allow_list { get; set; }
        public string index { get; set; }
        public string tmpl { get; set; }
        public Dictionary<string, string> data { get; set; }

        object tmplLock = new object();
        NaiveTemplate.Template _tmpl;
        DateTime _tmplLwt;

        public override async Task HandleRequestAsyncImpl(HttpConnection p)
        {
            Task hitdir(HttpConnection pp, string path)
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
                return WebSvrHelper.WriteDirListPage(pp, path, _tmpl, new NaiveTemplate.TemplaterData(dat));
            }
            var realPath = p.Url_path;
            if (index != null) {
                p.Url_path = index;
            }
            try {
                await WebSvrHelper.HandleDirectoryAsync(p, Controller.ProcessFilePath(dir),
                    WebSvrHelper.HandleFileAsync, allow_list ? hitdir : (Func<HttpConnection, string, Task>)null);
            } finally {
                p.Url_path = realPath;
            }
        }
    }
}

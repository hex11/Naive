using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks
{
    class WebFileAdapter : WebBaseAdapter
    {
        public string dir { get; set; }
        public bool allow_list { get; set; }
        public string index { get; set; }

        public override async Task HandleRequestAsync(HttpConnection p)
        {
            var realPath = p.Url_path;
            if (index != null) {
                p.Url_path = index;
            }
            try {
                await NaiveUtils.HandleDirectoryAsync(p, Controller.ProcessFilePath(dir), allow_list);
            } finally {
                p.Url_path = realPath;
            }
        }
    }
}

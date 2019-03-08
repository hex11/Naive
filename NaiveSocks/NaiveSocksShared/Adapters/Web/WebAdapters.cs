using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;
using Nett;

namespace NaiveSocks
{
    public class WebTestAdapter : WebBaseAdapter
    {
        public override Task HandleRequestAsyncImpl(HttpConnection p)
        {
            p.setContentTypeTextPlain();
            return p.EndResponseAsync(p.RawRequest);
        }
    }

    public class WebAuthAdapter : WebBaseAdapter
    {
        public Dictionary<string, AdapterRefOrArray> users { get; set; }
        public AdapterRefOrArray @default { get; set; }
        public NeedAuth need_auth { get; set; } = NeedAuth.always;
        public StringOrArray filter_mode { get; set; }
        public string text { get; set; } = "Login";

        public enum NeedAuth
        {
            never,
            wrong,
            always,
        }

        public override void SetConfig(TomlTable toml)
        {
            base.SetConfig(toml);
            if (users == null)
                Logger.warning("'users' is null!");
        }

        protected override void GetDetail(GetDetailContext ctx)
        {
            base.GetDetail(ctx);
            ctx.AddField("users", users?.Count ?? 0);
        }

        public override Task HandleRequestAsyncImpl(HttpConnection p)
        {
            if (filter_mode.IsNull == false) {
                var auth = HttpAuth.Parse(p);
                if (auth != null && filter_mode.IsOrContains(auth)) {
                    return NaiveUtils.CompletedTask;
                } else {
                    return Unauthorized(p);
                }
            }

            if (users != null) {
                var auth = HttpAuth.Parse(p);
                if (auth != null) {
                    if (users.TryGetValue(auth, out var adapters)) {
                        return HttpInAdapter.HandleByAdapters(p, adapters, Logger);
                    } else {
                        Logger.warning("wrong auth from " + p);
                        if (need_auth == NeedAuth.wrong)
                            return Unauthorized(p);
                    }
                }
            }
            if (need_auth == NeedAuth.always)
                return Unauthorized(p);
            if (@default.IsNull == false)
                return HttpInAdapter.HandleByAdapters(p, @default, Logger);
            return NaiveUtils.CompletedTask;
        }

        private Task Unauthorized(HttpConnection p)
        {
            HttpAuth.SetUnauthorizedHeader(p, text);
            p.Handled = true;
            return NaiveUtils.CompletedTask;
        }
    }
}

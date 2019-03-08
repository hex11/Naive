using System;
using System.Text;

namespace Naive.HttpSvr
{
    public abstract class HttpAuthFilter : IHttpRequestHandler
    {
        public void HandleRequest(HttpConnection p)
        {
            var authInfo = HttpAuth.Parse(p);
            Auth(p, authInfo);
        }

        public abstract void Auth(HttpConnection p, string authInfo);

        public event Action<HttpAuthFilter, HttpConnection, string> Unauthorized;
        protected virtual void OnUnauthorized(HttpConnection p, string auth)
        {
            Unauthorized?.Invoke(this, p, auth);
        }
    }

    public static class HttpAuth
    {
        public static string Parse(HttpConnection p)
        {
            string auth = p.GetReqHeader(HttpHeaders.KEY_Authorization);
            if (auth != null && auth.StartsWith("Basic")) {
                var base64str = auth.Substring(5);
                return NaiveUtils.UTF8Encoding.GetString(Convert.FromBase64String(base64str));
            }
            return null;
        }

        public static void Split(string str, out string user, out string passwd)
        {
            var userend = str.IndexOf(':');
            if (userend == -1) {
                user = str;
                passwd = null;
            } else {
                user = str.Substring(0, userend);
                passwd = str.Substring(userend, str.Length - userend);
            }
        }

        public static void SetUnauthorizedHeader(HttpConnection p, string realm)
        {
            p.ResponseStatusCode = "401 Unauthorized";
            p.ResponseHeaders[HttpHeaders.KEY_WWW_Authenticate] = "Basic realm=\"" + realm + "\"";
        }
    }
}

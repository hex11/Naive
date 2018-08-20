using System;
using System.Text;

namespace Naive.HttpSvr
{
    public abstract class HttpAuthFilter : IHttpRequestHandler
    {
        public Encoding AuthBase64Encoding = new UTF8Encoding(false, false);
        public void HandleRequest(HttpConnection p)
        {
            var authInfo = GetAuthInfo(p, AuthBase64Encoding);
            Auth(p, authInfo);
        }

        public static HttpAuthInfo GetAuthInfo(HttpConnection p, Encoding base64Encoding)
        {
            string auth = p.RequestHeaders[HttpHeaders.KEY_Authorization] as string;
            var info = new HttpAuthInfo();
            info.RawAuthString = auth;
            if (auth != null && auth.Length > 0) {
                if (auth.StartsWith("Basic")) {
                    var base64str = auth.Substring(5);
                    info.DecodedAuthString = base64Encoding.GetString(Convert.FromBase64String(base64str));
                    var splits = info.DecodedAuthString.Split(new[] { ':' }, 2);
                    info.Username = splits[0];
                    if (splits.Length == 2)
                        info.Passwd = splits[1];
                }
            }
            return info;
        }

        public static void SetUnauthorizedHeader(HttpConnection p)
        {
            p.ResponseStatusCode = "401 Unauthorized";
            p.ResponseHeaders[HttpHeaders.KEY_WWW_Authenticate] = "basic";
        }

        public abstract void Auth(HttpConnection p, HttpAuthInfo authInfo);

        public event Action<HttpAuthFilter, HttpConnection, HttpAuthInfo> Unauthorized;
        protected virtual void OnUnauthorized(HttpConnection p, HttpAuthInfo authInfo)
        {
            Unauthorized?.Invoke(this, p, authInfo);
        }
    }

    public class HttpAuthInfo
    {
        public string RawAuthString;
        public string DecodedAuthString;
        public string Username;
        public string Passwd;
    }
}

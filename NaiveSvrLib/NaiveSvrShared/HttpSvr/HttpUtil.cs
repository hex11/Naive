using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;

namespace Naive.HttpSvr
{
    public class HttpUtil
    {
        public static string HtmlEncode(string text)
        {
            return System.Web.Util.HttpEncoder.HtmlEncode(text);
        }

        public static string HtmlAttributeEncode(string text)
        {
            return System.Web.Util.HttpEncoder.HtmlAttributeEncode(text);
        }

        public static string HtmlDecode(string text)
        {
            return System.Web.Util.HttpEncoder.HtmlDecode(text);
        }

        public static string UrlDecode(string text)
        {
            return System.Web.HttpUtility.UrlDecode(text);
        }

        public static string UrlDecode(string text, Encoding encoding)
        {
            return System.Web.HttpUtility.UrlDecode(text, encoding);
        }

        public static string UrlEncode(string text)
        {
            return System.Web.HttpUtility.UrlEncode(text);
        }

        public static string UrlEncode(string text, Encoding encoding)
        {
            return System.Web.HttpUtility.UrlEncode(text, encoding);
        }

        public static NameValueCollection ParseQueryString(string qstr)
        {
            return System.Web.HttpUtility.ParseQueryString(qstr);
        }

        public static NameValueCollection ParseQueryString(string qstr, Encoding e)
        {
            return System.Web.HttpUtility.ParseQueryString(qstr, e);
        }
    }
}

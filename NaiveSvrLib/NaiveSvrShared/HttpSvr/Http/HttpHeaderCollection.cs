using System;
using System.Collections.Generic;
using System.Text;

namespace Naive.HttpSvr
{
    public struct HttpHeader
    {
        public string Key, Value;

        public HttpHeader(string key, string value)
        {
            Key = key;
            Value = value;
        }
    }

    public class HttpHeaderCollection : List<HttpHeader>
    {
        public HttpHeaderCollection()
        {
        }

        public HttpHeaderCollection(int capacity) : base(capacity)
        {
        }

        public string this[string key]
        {
            get {
                foreach (var kv in this) {
                    if (key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
                return null;
            }
            set {
                for (int i = 0; i < this.Count; i++) {
                    if (string.Equals(this[i].Key, key, StringComparison.OrdinalIgnoreCase)) {
                        if (value == null) {
                            this.RemoveAt(i);
                        } else {
                            this[i] = new HttpHeader(key, value);
                        }
                        return;
                    }
                }
                this.Add(new HttpHeader(key, value));
            }
        }
    }
}

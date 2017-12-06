using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{

    public class NaiveWebsiteRouter : IHttpRequestHandler, IHttpRequestAsyncHandler
    {
        public event HttpRequestHandler NotFound;
        public event HttpRequestHandler FoundButNotHandled;
        private Hashtable pathroutes = new Hashtable();
        private List<object> filters = new List<object>();

        public bool AutoSetHandled = true;
        public bool AutoSetResponseCode = true;
        public string ResponseStatusCodeIfFound = "200 OK";

        public void AddRoute(string path, HttpRequestHandler handler)
        {
            pathroutes.Add(path, handler);
        }

        public void AddAsyncRoute(string path, HttpRequestAsyncHandler handler)
        {
            pathroutes.Add(path, handler);
        }

        public void AddFilter(HttpRequestHandler func)
        {
            filters.Add(func);
        }

        public void AddAsyncFilter(HttpRequestAsyncHandler func)
        {
            filters.Add(func);
        }

        public void RemoveRoute(string path)
        {
            pathroutes.Remove(path);
        }

        public void RemoveFilter(HttpRequestHandler func)
        {
            filters.Remove(func);
        }

        public void RemoveAsyncFilter(HttpRequestAsyncHandler func)
        {
            filters.Remove(func);
        }

        public void AddRoutesByAttributes(object obj)
        {
            try {
                bool haveException = false;
                var type = obj.GetType();
                var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var item in methods) {
                    try {
                        var attrs = item.GetCustomAttributes(typeof(BaseRouteAttribute), true);
                        if (attrs.Length == 0)
                            continue;

                        foreach (BaseRouteAttribute attr in attrs) {
                            attr.ProcessRoute(this, item, obj);
                        }
                    } catch (Exception e) {
                        if (!haveException) {
                            haveException = true;
                            Logging.exception(e, Logging.Level.Error, "AddRoutesByAttributes on " + item.Name);
                        }
                    }
                }
            } catch (Exception e) {
                Logging.exception(e, Logging.Level.Error, "AddRoutesByAttributes");
            }
        }

        public void HandleRequest(HttpConnection p)
        {
            HandleRequestAsync(p).RunSync();
        }

        public HttpRequestHandler FindHandler(HttpConnection p)
        {
            HttpRequestHandler handler = null;
            try {
                handler = pathroutes[p.Url_path] as HttpRequestHandler;
            } catch (KeyNotFoundException) {
                return null;
            }
            return handler;
        }

        public HttpRequestAsyncHandler FindAsyncHandler(HttpConnection p)
        {
            HttpRequestAsyncHandler handler = null;
            try {
                handler = pathroutes[p.Url_path] as HttpRequestAsyncHandler;
            } catch (KeyNotFoundException) {
                return null;
            }
            return handler;
        }

        public async Task HandleRequestAsync(HttpConnection p)
        {
            if (p.Handled)
                return;
            foreach (var filter in filters) {
                switch (filter) {
                case HttpRequestAsyncHandler f:
                    await f(p).CAF();
                    break;
                case HttpRequestHandler f:
                    await Task.Run(()=>f(p)).CAF();
                    break;
                }
                if (p.Handled)
                    return;
            }
            var handler = pathroutes[p.Url_path];
            if (handler != null) {
                if (AutoSetHandled)
                    p.Handled = true;
                if (AutoSetResponseCode)
                    p.ResponseStatusCode = ResponseStatusCodeIfFound;
                switch (handler) {
                case HttpRequestAsyncHandler f:
                    await f(p).CAF();
                    break;
                case HttpRequestHandler f:
                    await Task.Run(() => f(p)).CAF();
                    break;
                }
                if (p.Handled == false) {
                    FoundButNotHandled?.Invoke(p);
                }
            } else {
                NotFound?.Invoke(p);
            }
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class PathAttribute : BaseRouteAttribute
    {
        private string path;
        public PathAttribute(string path)
        {
            this.path = path;
        }

        public override void ProcessRoute(NaiveWebsiteRouter router, MethodInfo methodInfo, object obj)
        {
            router.AddRoute(path, (p) => {
                try {
                    methodInfo.Invoke(obj, new[] { p });
                } catch (TargetInvocationException e) {
                    throw new Exception("Router TargetInvocationException", e.InnerException);
                }
            });
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public abstract class BaseRouteAttribute : Attribute
    {
        public abstract void ProcessRoute(NaiveWebsiteRouter router, MethodInfo methodInfo, object obj);
    }
}

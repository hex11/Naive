using System;
using System.Net.Sockets;
using Naive;
using Naive.HttpSvr;
using Naive.Console;

namespace NaiveServer
{
    public interface IController
    {
        ArgParseResult Args { get; }
        bool Debug { get; }
        CommandHub CommandHub { get; }

        event HttpRequestHandler AfterHandle;
        event HttpRequestHandler BeforeHandle;
        event Action<HttpConnection, TcpClient, NaiveHttpServer> CreatedHttpConnection;
        event Action<TcpClient, NaiveHttpServer> CreatingHttpConnection;
        
        void AddNamedHandler(IModule module, string name, IHttpRequestAsyncHandler handler);
        void AddNamedHandler(IModule module, string name, IHttpRequestHandler handler);

        void AddHandlerProvider(IModule module, IHandlerProvider provider);
    }

    public static class IControllerExt
    {
        public static void AddHandlerProvider(this IController controller, IModule module, Func<string, IHttpRequestAsyncHandler> func)
        {
            controller.AddHandlerProvider(module, new LambdaHandlerProvider(func));
        }
    }

    public interface IHandlerProvider
    {
        IHttpRequestAsyncHandler GetHandler(string name);
    }

    public class LambdaHandlerProvider : IHandlerProvider
    {
        Func<string, IHttpRequestAsyncHandler> func;

        public LambdaHandlerProvider(Func<string, IHttpRequestAsyncHandler> func)
        {
            this.func = func ?? throw new ArgumentNullException(nameof(func));
        }

        public IHttpRequestAsyncHandler GetHandler(string name) => func(name);
    }
}
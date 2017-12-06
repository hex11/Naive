
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    public class NaiveWebsiteServer : NaiveHttpServer, IHttpRequestAsyncHandler
    {
        public NaiveWebsiteRouter Router = new NaiveWebsiteRouter();

        public NaiveWebsiteServer() : base()
        {
            this.handler = Router;
        }

        public override void HandleRequest(HttpConnection p)
        {
            Router.HandleRequest(p);
        }

        public async Task HandleRequestAsync(HttpConnection p)
        {
            await Router.HandleRequestAsync(p);
        }
    }
}

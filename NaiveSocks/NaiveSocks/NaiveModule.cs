using Naive.HttpSvr;
using NaiveServer;

namespace NaiveSocks
{
    public class NaiveModule : IModule, IHandlerProvider
    {
        private Controller nsController;

        public NaiveModule()
        {

        }

        public void Load(IController controller)
        {
            controller.AddHandlerProvider(this, this);
            nsController = new NaiveSocks.Controller();
            nsController.Logger.ParentLogger = Logging.RootLogger;
            Commands.AddCommands(controller.CommandHub, nsController, "ns");
        }

        public void Start()
        {
            loadController();
        }

        private void loadController()
        {
            nsController.LoadConfigFileOrWarning(Program.configFilePath);
            nsController.Start();
        }

        public void Stop()
        {
            nsController.Stop();
        }

        public IHttpRequestAsyncHandler GetHandler(string name)
        {
            var prefix = "nsocks_";
            if (name.StartsWith(prefix)) {
                return nsController.FindAdapter<IHttpRequestAsyncHandler>(name.Substring(prefix.Length));
            }
            return null;
        }
    }
}

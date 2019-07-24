using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NaiveSocks.WinForm
{
    class WinFormController
    {
        public Controller Controller { get; }

        WindowsFormsSynchronizationContext context;

        ManualResetEvent contextInited;

        public WinFormController()
        {
            this.Controller = new Controller();
        }

        public WinFormController(Controller controller)
        {
            this.Controller = controller;
        }

        public void RunUIThread()
        {
            contextInited = new ManualResetEvent(false);
            new System.Threading.Thread(this.Run) { Name = "GUI" }.Start();
            if (!contextInited.WaitOne(60000))
                throw new TimeoutException("UI thread context hasn't been initialized in 1 minute.");
        }

        void Run()
        {
            context = new WindowsFormsSynchronizationContext();
            contextInited.Set();
            Application.EnableVisualStyles();
            Application.Run();
        }

        public void ShowControllerForm(bool exitOnClose)
        {
            context.Post((s) => {
                var form = new ControllerForm(Controller);
                form.Show();
                if (exitOnClose) {
                    form.FormClosed += (s2, e2) => {
                        Environment.Exit(0);
                    };
                }
            }, null);
        }
    }
}

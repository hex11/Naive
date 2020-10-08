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

        ManualResetEvent contextInited = new ManualResetEvent(false);

        public WinFormController()
        {
            this.Controller = new Controller();
        }

        public WinFormController(Controller controller)
        {
            this.Controller = controller;
        }

        public void StartUIThread()
        {
            new System.Threading.Thread(() => this.RunAsUIThread()) { Name = "GUI" }.Start();
            if (!contextInited.WaitOne(60000))
                throw new TimeoutException("UI thread context hasn't been initialized in 1 minute.");
        }

        public void RunAsUIThread(Action beforeRun = null)
        {
            context = new WindowsFormsSynchronizationContext();
            contextInited.Set();
            Application.EnableVisualStyles();
            beforeRun?.Invoke();
            Application.Run();
        }

        public void ShowControllerForm(bool exitOnClose)
        {
            var form = new ControllerForm(Controller);
            form.Show();
            if (exitOnClose) {
                form.FormClosed += (s2, e2) => {
                    Environment.Exit(0);
                };
            }
        }

        public void Invoke(Action callback)
        {
            context.Post((s) => {
                callback();
            }, null);
        }
    }
}

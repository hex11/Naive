using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace NaiveSocks.WinForm
{
    class WinFormController
    {
        Controller controller;

        public WinFormController()
        {
            this.controller = new Controller();
        }

        public WinFormController(Controller controller)
        {
            this.controller = controller;
        }

        public void RunInNewThread()
        {
            new System.Threading.Thread(this.Run) { Name = "GUI" }.Start();
        }

        void Run()
        {
            var form = new ControllerForm(controller);
            Application.EnableVisualStyles();
            Application.Run(form);
        }
    }
}

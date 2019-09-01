using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

namespace NaiveSocks.WinForm
{
    public abstract class TabBase
    {
        internal MenuItem menuItem;

        public abstract Control View { get; }
        public abstract string Name { get; }

        public bool Showing { get; private set; }

        internal void SetShowing(bool show)
        {
            Showing = show;
            if (menuItem != null) menuItem.Enabled = !show;
            if (show) OnShow();
            else OnHide();
        }

        protected virtual void OnShow()
        {
        }

        protected virtual void OnHide()
        {
        }

        public virtual void OnTick()
        {
        }

        public virtual void Destroy()
        {
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace NaiveSocks.WinForm
{
    class TabForm : Form
    {
        public TabForm()
        {
            this.Size = new System.Drawing.Size(700, 400);
        }

        public bool DestroyOnClose;

        TabBase curTab;

        public void SetTab(TabBase tab)
        {
            if (curTab != null) {
                curTab.SetShowing(false);
                this.Controls.RemoveAt(0);
            }
            curTab = tab;
            tab.View.Dock = DockStyle.Fill;
            this.Controls.Add(tab.View);
            curTab.SetShowing(true);
            OnTabChanged(tab);
        }

        protected virtual void OnTabChanged(TabBase tab)
        {
            this.Text = tab.Name;
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DestroyOnClose) curTab?.Destroy();
            base.OnClosed(e);
        }
    }
}

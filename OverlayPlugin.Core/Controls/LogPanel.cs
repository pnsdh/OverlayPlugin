using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RainbowMage.OverlayPlugin.Controls
{
    public partial class LogPanel : UserControl
    {
        public LogPanel(TinyIoCContainer container)
        {
            InitializeComponent();

            // Fork: entries come from any thread (e.g. overlays' console); the box is written on
            // the UI thread, posted so that a thread holding a lock never waits for it.
            container.Resolve<ILogger>().RegisterListener((entry) =>
            {
                var line = $"[{entry.Time}] {entry.Level}: {entry.Message}" + Environment.NewLine;
                if (!logBox.IsHandleCreated || !logBox.InvokeRequired)
                    logBox.AppendText(line);
                else
                    logBox.BeginInvoke((Action)(() => logBox.AppendText(line)));
            });
        }
    }
}

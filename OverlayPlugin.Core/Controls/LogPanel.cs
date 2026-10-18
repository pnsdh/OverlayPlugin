using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
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
            var ui = SynchronizationContext.Current;
            container.Resolve<ILogger>().RegisterListener((entry) =>
            {
                var line = $"[{entry.Time}] {entry.Level}: {entry.Message}" + Environment.NewLine;
                // Posted to the UI thread this was made on (never written from another thread, which
                // would make the box's window there if it has none yet), dropped once it is gone.
                void Append()
                {
                    if (!logBox.IsDisposed)
                        logBox.AppendText(line);
                }
                if (ui == null || SynchronizationContext.Current == ui)
                    Append();
                else
                    ui.Post(_ => Append(), null);
            });
        }
    }
}

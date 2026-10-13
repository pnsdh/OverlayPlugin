using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using RainbowMage.HtmlRenderer;

namespace RainbowMage.OverlayPlugin
{
    public partial class GeneralConfigTab : UserControl
    {
        readonly TinyIoCContainer container;
        readonly string pluginDirectory;
        readonly PluginConfig config;
        readonly ILogger logger;

        private DateTime lastClick;

        public GeneralConfigTab(TinyIoCContainer container)
        {
            InitializeComponent();
            Dock = DockStyle.Fill;

            this.container = container;
            pluginDirectory = container.Resolve<PluginMain>().PluginDirectory;
            config = container.Resolve<PluginConfig>();
            logger = container.Resolve<ILogger>();

            cbErrorReports.Checked = config.ErrorReports;
            SetupGpuOption();
            cbUpdateCheck.Checked = config.UpdateCheck;
            cbHideOverlaysWhenNotActive.Checked = config.HideOverlaysWhenNotActive;
            cbHideOverlaysDuringCutscene.Checked = config.HideOverlayDuringCutscene;

            // Attach the event handlers only *after* loading the configuration because we'd otherwise trigger them ourselves.
            cbErrorReports.CheckedChanged += CbErrorReports_CheckedChanged;
            cbUpdateCheck.CheckedChanged += CbUpdateCheck_CheckedChanged;
            cbHideOverlaysWhenNotActive.CheckedChanged += cbHideOverlaysWhenNotActive_CheckedChanged;
            cbHideOverlaysDuringCutscene.CheckedChanged += cbHideOverlaysDuringCutscene_CheckedChanged;
        }

        // Fork: crash reports are always off (see Renderer.EnableErrorReports), so their checkbox's
        // place holds the GPU option.
        private void SetupGpuOption()
        {
            var ko = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
            var cbUseGpu = new CheckBox
            {
                Name = "cbUseGpu",
                Text = ko
                    ? "GPU로 오버레이 그리기 (CEF만, ACT를 다시 시작하면 적용. WebView2는 항상 GPU)"
                    : "Render overlays with the GPU (CEF overlays; applies after restarting ACT. WebView2 overlays always use it)",
                Location = cbErrorReports.Location,
                AutoSize = true,
                UseVisualStyleBackColor = true,
                Checked = config.UseGpu,
            };
            cbUseGpu.CheckedChanged += (o, e) =>
            {
                config.UseGpu = cbUseGpu.Checked;
                // CEF takes it only when it starts, once per ACT process: offer ACT's restart.
                Updater.Updater.TryRestartACT(true, ko
                    ? "GPU 옵션은 ACT를 다시 시작해야 적용됩니다."
                    : "The GPU option applies after restarting ACT.");
            };
            cbErrorReports.Visible = false;
            Controls.Add(cbUseGpu);
        }

        public void SetReadmeVisible(bool visible)
        {
            lblReadMe.Visible = visible;
            lblNewUserWelcome.Visible = visible;
        }

        private void btnUpdateCheck_MouseClick(object sender, MouseEventArgs e)
        {
            // Shitty double-click detection. I'd love to have a proper double click event on buttons in WinForms. =/
            double timePassed = 1000;
            var now = DateTime.Now;

            if (lastClick != null)
            {
                timePassed = now.Subtract(lastClick).TotalMilliseconds;
            }

            lastClick = now;

            Task.Run(() =>
            {
                Thread.Sleep(500);

                if (lastClick != now) return;
                Updater.Updater.PerformUpdateIfNecessary(pluginDirectory, container, true, timePassed < 500);
            });
        }

        private void CbErrorReports_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (cbErrorReports.Checked)
                {
                    Renderer.EnableErrorReports(ActGlobals.oFormActMain.AppDataFolder.FullName);
                }
                else
                {
                    Renderer.DisableErrorReports(ActGlobals.oFormActMain.AppDataFolder.FullName);
                }
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, $"Failed to switch error reports: {ex}");
                cbErrorReports.Checked = !cbErrorReports.Checked;

                MessageBox.Show($"Failed to switch error reports: {ex}", "OverlayPlugin", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            config.ErrorReports = cbErrorReports.Checked;

            MessageBox.Show("You have to restart ACT to apply this change.", "OverlayPlugin", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void CbUpdateCheck_CheckedChanged(object sender, EventArgs e)
        {
            config.UpdateCheck = cbUpdateCheck.Checked;
        }

        private void cbHideOverlaysWhenNotActive_CheckedChanged(object sender, EventArgs e)
        {
            config.HideOverlaysWhenNotActive = cbHideOverlaysWhenNotActive.Checked;
            container.Resolve<OverlayHider>().UpdateOverlays();
        }

        private void cbHideOverlaysDuringCutscene_CheckedChanged(object sender, EventArgs e)
        {
            config.HideOverlayDuringCutscene = cbHideOverlaysDuringCutscene.Checked;
            container.Resolve<OverlayHider>().UpdateOverlays();
        }

        private void lnkGithubRepo_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start(lnkGithubRepo.Text);
        }

        private void newUserWelcome_Click(object sender, EventArgs e)
        {

        }

        private void btnCactbotUpdate_Click(object sender, EventArgs e)
        {
            try
            {
                var asm = Assembly.Load("CactbotEventSource");
                var checkerType = asm.GetType("Cactbot.VersionChecker");
                var loggerType = typeof(ILogger);
                var configType = asm.GetType("Cactbot.CactbotEventSourceConfig");

                var esList = container.Resolve<Registry>().EventSources;
                IEventSource cactbotEs = null;

                foreach (var es in esList)
                {
                    if (es.Name == "Cactbot Config" || es.Name == "Cactbot")
                    {
                        cactbotEs = es;
                        break;
                    }
                }

                if (cactbotEs == null)
                {
                    MessageBox.Show("Cactbot is loaded but it never registered with OverlayPlugin!", "Error");
                    return;
                }

                var cactbotConfig = cactbotEs.GetType().GetProperty("Config").GetValue(cactbotEs);
                configType.GetField("LastUpdateCheck").SetValue(cactbotConfig, DateTime.MinValue);

                var checker = checkerType.GetConstructor(new Type[] { loggerType }).Invoke(new object[] { logger });
                checkerType.GetMethod("DoUpdateCheck", new Type[] { configType }).Invoke(checker, new object[] { cactbotConfig });
            }
            catch (FileNotFoundException)
            {
                MessageBox.Show("Could not find Cactbot!", "Error");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed: " + ex.ToString(), "Error");
            }
        }

        private void btnClipboardTechSupport_Click(object sender, EventArgs e)
        {
            var info = new ClipboardTechSupport(this.container);
            info.CopyToClipboard();
        }

        private void btnClearCEFCache_Click(object sender, EventArgs e)
        {
            container.Resolve<PluginMain>().ClearCacheOnRestart();
            btnClearCEFCache.Enabled = false;
            Updater.Updater.TryRestartACT(true, Resources.ClearCacheRestart);
        }
    }
}

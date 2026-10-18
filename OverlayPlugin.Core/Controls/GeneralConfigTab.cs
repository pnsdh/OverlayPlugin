using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
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
            SetupForkOptions();
            cbUpdateCheck.Checked = config.UpdateCheck;
            cbHideOverlaysWhenNotActive.Checked = config.HideOverlaysWhenNotActive;
            cbHideOverlaysDuringCutscene.Checked = config.HideOverlayDuringCutscene;

            // Attach the event handlers only *after* loading the configuration because we'd otherwise trigger them ourselves.
            cbErrorReports.CheckedChanged += CbErrorReports_CheckedChanged;
            cbUpdateCheck.CheckedChanged += CbUpdateCheck_CheckedChanged;
            cbHideOverlaysWhenNotActive.CheckedChanged += cbHideOverlaysWhenNotActive_CheckedChanged;
            cbHideOverlaysDuringCutscene.CheckedChanged += cbHideOverlaysDuringCutscene_CheckedChanged;
        }

        // Fork: the options laid out again: the general ones first, then how things are drawn (the GPU
        // option takes the place of the crash reports, which are always off, see
        // Renderer.EnableErrorReports, and config panels drawn by WebView2, see
        // Renderer.PanelsUseWebView2); buttons tall and wide enough for Korean text; a Discord link
        // under GitHub's.  Positions are worked out from the designer's, which Windows has already
        // scaled for the screen.
        private void SetupForkOptions()
        {
            var ko = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
            int row = cbUpdateCheck.Top - cbErrorReports.Top;
            int top = cbErrorReports.Top;
            int left = cbErrorReports.Left;
            int gap = row / 2;

            var cbUseGpu = new CheckBox
            {
                Name = "cbUseGpu",
                Text = ko
                    ? "GPU로 오버레이 그리기 (CEF만, ACT를 다시 시작하면 적용. WebView2는 항상 GPU)"
                    : "Render overlays with the GPU (CEF overlays; applies after restarting ACT. WebView2 overlays always use it)",
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
            var cbPanels = new CheckBox
            {
                Name = "cbPanelsUseWebView2",
                Text = ko
                    ? "플러그인 설정 화면(Cactbot 설정 등)도 WebView2로 그리기 (ACT를 다시 시작하면 적용)"
                    : "Draw plugins' config panels (e.g. cactbot's) with WebView2 too (applies after restarting ACT)",
                AutoSize = true,
                UseVisualStyleBackColor = true,
                Checked = config.PanelsUseWebView2,
                Enabled = RainbowMage.HtmlRenderer.Renderer.WebView2Available || config.PanelsUseWebView2,
            };
            cbPanels.CheckedChanged += (o, e) =>
            {
                config.PanelsUseWebView2 = cbPanels.Checked;
                Updater.Updater.TryRestartACT(true, ko
                    ? "설정 화면을 그리는 방식은 ACT를 다시 시작해야 바뀝니다."
                    : "How config panels are drawn changes after restarting ACT.");
            };
            cbErrorReports.Visible = false;
            Controls.Add(cbUseGpu);
            Controls.Add(cbPanels);

            // The options: general ones, then drawing.
            var options = new Control[] { cbUpdateCheck, cbHideOverlaysWhenNotActive, cbHideOverlaysDuringCutscene };
            for (int i = 0; i < options.Length; i++)
                options[i].Location = new Point(left, top + i * row);
            int drawing = top + options.Length * row + gap;
            cbUseGpu.Location = new Point(left, drawing);
            cbPanels.Location = new Point(left, drawing + row);

            // The buttons, two to a row, each column as wide as its longest text.
            int buttonHeight = btnUpdateCheck.Height + row / 5;
            int buttonGap = btnClipboardTechSupport.Top - btnUpdateCheck.Bottom;
            int buttonsTop = drawing + 2 * row + gap;
            int Width(params Button[] buttons) => buttons.Max(b => Math.Max(b.Width, TextRenderer.MeasureText(b.Text, b.Font).Width + row));
            int firstWidth = Width(btnUpdateCheck, btnClipboardTechSupport);
            int secondWidth = Width(btnCactbotUpdate, btnClearCEFCache);
            int secondLeft = btnUpdateCheck.Left + firstWidth + (btnCactbotUpdate.Left - btnUpdateCheck.Right);
            void Place(Button b, int x, int y, int w) => b.Bounds = new Rectangle(x, y, w, buttonHeight);
            Place(btnUpdateCheck, btnUpdateCheck.Left, buttonsTop, firstWidth);
            Place(btnCactbotUpdate, secondLeft, buttonsTop, secondWidth);
            Place(btnClipboardTechSupport, btnUpdateCheck.Left, buttonsTop + buttonHeight + buttonGap, firstWidth);
            Place(btnClearCEFCache, secondLeft, buttonsTop + buttonHeight + buttonGap, secondWidth);

            // What follows the buttons moves with them, with a Discord line under GitHub's.
            int shift = btnClipboardTechSupport.Bottom + row - lblGithub.Top;
            foreach (var c in new Control[] { lblGithub, lnkGithubRepo, lblReadMe, lblNewUserWelcome })
                c.Top += shift;
            var lblDiscord = new Label
            {
                Name = "lblDiscord",
                Text = "Discord:",
                AutoSize = true,
                Location = new Point(lblGithub.Left, lblGithub.Top + row - row / 5),
            };
            var lnkDiscord = new LinkLabel
            {
                Name = "lnkDiscord",
                Text = ClipboardTechSupport.DiscordInvite,
                AutoSize = true,
                Location = new Point(lnkGithubRepo.Left, lblDiscord.Top),
            };
            lnkDiscord.LinkClicked += (o, e) => Process.Start(ClipboardTechSupport.DiscordInvite);
            Controls.Add(lblDiscord);
            Controls.Add(lnkDiscord);
            lblReadMe.Top += row;
            lblNewUserWelcome.Top += row;

            // A word after copying the tech support info: where it goes, and how long it is.
            lblCopied = new Label
            {
                Name = "lblCopied",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Visible = false,
                Location = new Point(btnClearCEFCache.Right + row / 2, btnClearCEFCache.Top + (buttonHeight - Font.Height) / 2),
            };
            Controls.Add(lblCopied);
        }

        private Label lblCopied;

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
            // Fork: say whether it worked, and where to paste it.
            var ko = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
            try
            {
                var info = new ClipboardTechSupport(this.container);
                int length = info.CopyToClipboard();
                lblCopied.Text = ko
                    ? $"복사했습니다 ({length}자). 디스코드에 붙여 넣으세요."
                    : $"Copied ({length} characters). Paste it in Discord.";
            }
            catch (Exception ex)
            {
                // E.g. another program holding the clipboard.
                lblCopied.Text = (ko ? "복사하지 못했습니다: " : "Could not copy: ") + ex.Message;
            }
            lblCopied.Visible = true;
        }

        private void btnClearCEFCache_Click(object sender, EventArgs e)
        {
            container.Resolve<PluginMain>().ClearCacheOnRestart();
            btnClearCEFCache.Enabled = false;
            Updater.Updater.TryRestartACT(true, Resources.ClearCacheRestart);
        }
    }
}

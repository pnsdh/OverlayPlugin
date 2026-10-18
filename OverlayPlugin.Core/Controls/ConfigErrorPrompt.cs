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
    public partial class ConfigErrorPrompt : Form
    {
        public ConfigErrorPrompt()
        {
            InitializeComponent();
            FitToText();
        }

        // Fork: the buttons in Korean, and the window as tall as its text: the designer's fixed
        // heights cut the buttons off once the text wraps to more lines (larger fonts, Korean).
        private void FitToText()
        {
            if (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko")
            {
                btnReset.Text = "설정 초기화하고 계속";
                btnCancel.Text = "OverlayPlugin 끄기";
            }
            int pad = errorExplanation.Left;
            int width = ClientSize.Width - 2 * pad;
            int textHeight = errorExplanation.GetPreferredSize(new Size(width, 0)).Height;
            int buttonHeight = Math.Max(panel1.Height, Font.Height * 2 + pad / 2);
            SuspendLayout();
            errorExplanation.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            panel1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            errorExplanation.Bounds = new Rectangle(pad, errorExplanation.Top, width, textHeight);
            panel1.Bounds = new Rectangle(panel1.Left, errorExplanation.Bottom + pad, panel1.Width, buttonHeight);
            ClientSize = new Size(ClientSize.Width, panel1.Bottom + pad);
            ResumeLayout();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void btnReset_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Yes;
            Close();
        }
    }
}

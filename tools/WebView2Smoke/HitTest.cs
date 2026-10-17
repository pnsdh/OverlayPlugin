using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

static class HitTest
{
    // Reads the alpha of one page pixel through the DevTools protocol.
    public static async Task<int> AlphaAt(CoreWebView2 core, double x, double y)
    {
        var args = new JObject { ["format"] = "png", ["clip"] = new JObject { ["x"] = x, ["y"] = y, ["width"] = 1, ["height"] = 1, ["scale"] = 1 } };
        var json = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args.ToString());
        var data = Convert.FromBase64String((string)JObject.Parse(json)["data"]);
        using (var ms = new MemoryStream(data))
        using (var bmp = new Bitmap(ms))
            return bmp.GetPixel(0, 0).A;
    }
}

using System.Drawing;
using System.Windows.Forms;

internal static partial class UiChecks
{
    private static void TooltipFirstFrameChecks()
    {
        using var owner = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(500, 200, 200, 200) };
        owner.Show();
        Application.DoEvents();
        owner.Activate();
        Application.DoEvents();
        using var tip = new WidgetTip();
        var firstFrames = 0;
        void Ready()
        {
            var frame = Get<Bitmap>(tip, "_preparedFrame");
            Check(frame.Size == tip.ClientSize && frame.Width > 0 && frame.Height > 0,
                "Tooltip became visible without its final sized frame.");
            Check(frame.GetPixel(0, 0).A == 0 && frame.GetPixel(frame.Width / 2, frame.Height / 2).A == 255,
                "Tooltip lost its rounded alpha mask or opaque text interior.");
            var palette = Get<Palette>(tip, "_p");
            var textPixels = 0;
            for (var y = 8; y < frame.Height - 8; y++)
            for (var x = 12; x < frame.Width - 12; x++)
            {
                var c = frame.GetPixel(x, y);
                if (Math.Abs(c.R - palette.Bg.R) + Math.Abs(c.G - palette.Bg.G) + Math.Abs(c.B - palette.Bg.B) > 100) textPixels++;
            }
            Check(textPixels > 20, "The first tooltip frame contains only a border, without its text.");
        }
        tip.VisibleChanged += (_, _) => { if (tip.Visible) { Ready(); firstFrames++; } };
        foreach (var theme in new[] { WidgetTheme.Dark, WidgetTheme.Light })
        foreach (var opacity in new[] { 1.0, .75 })
        {
            var appearance = new AppSettings { Theme = theme, WidgetFontFamily = "Arial", WidgetTextSize = 120 };
            tip.ShowBeside(owner, "Hover text · æøå\nDetails are ready immediately", owner.Top + 50, Palette.For(appearance), opacity, appearance: appearance);
            Ready(); // no message pumping: content must already be present when ShowBeside returns
            tip.ShowBeside(owner, "Updated hover text · 0123456789", owner.Top + 80, Palette.For(appearance), opacity, appearance: appearance);
            Ready();
            Application.DoEvents();
            Check(Form.ActiveForm != tip, "A tooltip took focus from its owner.");
            tip.HideTip();
        }
        Check(firstFrames == 4, "Not all first-show tooltip frames were checked.");
        Console.WriteLine("PASS Tooltip first frame: text/border/background are ready before visibility; immediate updates, rounded alpha, dark/light, opacity and passive focus");
    }
}

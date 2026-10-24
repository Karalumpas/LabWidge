using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;

internal static partial class UiChecks
{
    private static void RetainedRendering()
    {
        using var fixture = new Fixture();
        var widget = fixture.Widget;
        widget.CreateControl();
        var settings = fixture.Settings;
        settings.LaunchItems = Enumerable.Range(1, 4).Select(i => new LaunchItem { Name = $"Shortcut {i}", Target = $"labwidge-test://{i}" }).ToList();
        settings.ShowLaunchRail = true;
        Bitmap Fresh()
        {
            var frame = new Bitmap(widget.ClientSize.Width, widget.ClientSize.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(frame);
            g.Clear(widget.BackColor);
            Call(widget, "Render", g);
            return frame;
        }
        bool EqualFrames(Bitmap first, Bitmap second)
        {
            if (first.Size != second.Size) return false;
            var rect = new Rectangle(Point.Empty, first.Size);
            var a = first.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var b = second.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var left = new byte[a.Stride * a.Height];
                var right = new byte[b.Stride * b.Height];
                System.Runtime.InteropServices.Marshal.Copy(a.Scan0, left, 0, left.Length);
                System.Runtime.InteropServices.Marshal.Copy(b.Scan0, right, 0, right.Length);
                for (var y = 0; y < first.Height; y++)
                    if (!left.AsSpan(y * a.Stride, first.Width * 3).SequenceEqual(right.AsSpan(y * b.Stride, second.Width * 3))) return false;
                return true;
            }
            finally { first.UnlockBits(a); second.UnlockBits(b); }
        }
        void Same(string label)
        {
            var frame = widget.PaintSurface();
            using var reference = Fresh();
            Check(EqualFrames(frame, reference), $"Retained rendering differs from a fresh frame: {label}");
            var expectedHits = Hits(widget).Select(hit => (hit.Rect, hit.Tip)).ToArray();
            widget.PaintSurface(true);
            Check(expectedHits.SequenceEqual(Hits(widget).Select(hit => (hit.Rect, hit.Tip))),
                $"Retained rendering changed hit targets: {label}");
        }
        foreach (var theme in new[] { WidgetTheme.Dark, WidgetTheme.Light })
        foreach (var placement in new[] { RailPlacement.Left, RailPlacement.Right, RailPlacement.Section })
        {
            settings.Theme = theme;
            settings.LaunchRailPlacement = placement;
            settings.WidgetFontFamily = theme == WidgetTheme.Light ? "Arial" : "Segoe UI";
            settings.WidgetTextSize = theme == WidgetTheme.Light ? 120 : 100;
            settings.WidgetBoldText = theme == WidgetTheme.Light;
            widget.ApplySettings(settings);
            Call(widget, "FitSize");
            widget.PaintSurface(true);
            var points = Hits(widget).Where(hit => hit.Rect.Width > 0 && hit.Rect.Height > 0)
                .Select(hit => Point.Round(new PointF(hit.Rect.X + hit.Rect.Width / 2, hit.Rect.Y + hit.Rect.Height / 2)))
                .Where(point => widget.ClientRectangle.Contains(point)).Distinct().Take(18).ToList();
            points.Add(new Point(-1, -1));
            foreach (var point in points)
            {
                widget.GetType().GetField("_mouse", Private)!.SetValue(widget, point);
                Same($"{theme}/{placement}/hover {point}");
            }
            foreach (var progress in new[] { 0f, .15f, .5f, .85f, 1f, .5f, 0f })
            {
                widget.GetType().GetField("_railOpen", Private)!.SetValue(widget, progress);
                Same($"{theme}/{placement}/rail {progress}");
            }
            Call(widget, "ScrollTo", 90f);
            Same($"{theme}/{placement}/scroll");
            settings.ColorPreset = ColorPreset.Violet;
            widget.ApplySettings(settings);
            Same($"{theme}/{placement}/settings");
        }
        settings.CompactMode = true;
        widget.ApplySettings(settings);
        Call(widget, "FitSize");
        widget.PaintSurface(true);
        foreach (var point in new[] { new Point(30, 25), new Point(100, 50), new Point(-1, -1) })
        {
            widget.GetType().GetField("_mouse", Private)!.SetValue(widget, point);
            Same($"compact hover {point}");
        }
        settings.CompactMode = false;
        settings.LaunchRailPlacement = RailPlacement.Left;
        settings.Theme = WidgetTheme.Dark;
        widget.ApplySettings(settings);
        Call(widget, "FitSize");
        widget.PaintSurface(true);
        Set(fixture.Audio, "DefaultId", "headset");
        widget.Invalidate();
        Same("new audio data");
        var surface = widget.PaintSurface();
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 30; i++) widget.PaintSurface(true);
        var full = timer.Elapsed.TotalMilliseconds / 30;
        timer.Restart();
        for (var i = 0; i < 30; i++)
        {
            widget.GetType().GetField("_railOpen", Private)!.SetValue(widget, (i % 11) / 10f);
            Check(ReferenceEquals(surface, widget.PaintSurface()), "Interaction allocated another RGB frame.");
        }
        var partial = timer.Elapsed.TotalMilliseconds / 30;
        Console.WriteLine($"PASS Retained rendering: hover/leave, rail animation, scroll, themes/fonts, left/right/section match full pixels and hit targets; {full:0.00} ms full / {partial:0.00} ms rail frame");
    }
}

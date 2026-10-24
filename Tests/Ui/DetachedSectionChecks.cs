using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

internal static partial class UiChecks
{
    private static void DetachedSectionChecks()
    {
        using var fixture = new Fixture();
        var widget = fixture.Widget;
        var settings = fixture.Settings;
        Call(widget, "FitSize");
        var area = Screen.FromControl(widget).WorkingArea;
        widget.Location = new Point(area.Left + area.Width / 2 - widget.Width / 2, area.Top + 30);
        var order = settings.SectionOrder?.ToArray();
        SectionWindows.OpenPinnedAt("system", new Point(area.Left + 20, area.Top + 50));
        Call(widget, "CollapseDetachedSection", "system");
        Check(settings.CollapsedSystem && fixture.SaveCount > 0, "Detached section was not collapsed and saved.");
        Check(order == null ? settings.SectionOrder == null : order.SequenceEqual(settings.SectionOrder!),
            "Detaching changed section order.");
        var open = (IDictionary)typeof(SectionWindows).GetField("Open", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var panel = (PopupPanel)open["system"]!;
        var left = new Point(area.Left, panel.Top);
        var right = new Point(area.Right - panel.Width, panel.Top);
        Check(left.X + panel.Width / 2 < widget.Left + widget.Width / 2
            && right.X + panel.Width / 2 > widget.Left + widget.Width / 2, "Fixture needs room on both sides of the widget.");
        foreach (var theme in new[] { WidgetTheme.Dark, WidgetTheme.Light })
        {
            settings.Theme = theme;
            widget.ApplySettings(settings);
            foreach (var onLeft in new[] { true, false })
            {
                panel.Location = onLeft ? left : right;
                using var frame = Frame(widget, $"detached/{theme}-{(onLeft ? "left" : "right")}.png");
                Check((bool?)Call(widget, "DetachedHeaderSide", "system") == onLeft, "Gradient did not follow the detached window.");
                var bounds = Get<IEnumerable>(widget, "_sectionBounds").Cast<object>()
                    .First(b => (string)b.GetType().GetProperty("Key")!.GetValue(b)! == "system");
                var y = (int)((float)bounds.GetType().GetProperty("Top")!.GetValue(bounds)! + 17 * widget.DeviceDpi / 96f);
                var contentLeft = (float)widget.GetType().GetProperty("ContentLeft", Private)!.GetValue(widget)!;
                var headerTop = (float)bounds.GetType().GetProperty("Top")!.GetValue(bounds)!;
                Call(widget, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0,
                    (int)(contentLeft + 13 * widget.DeviceDpi / 96f), (int)(headerTop + 6 * widget.DeviceDpi / 96f), 0));
                Check(widget.Cursor == Cursors.SizeAll, "The section drag handle did not show a four-way cursor.");
                Call(widget, "OnMouseLeave", EventArgs.Empty);
                var a = frame.GetPixel((int)contentLeft + 20, y);
                var b = frame.GetPixel(frame.Width - 20, y);
                var accent = Palette.For(settings).Blue;
                int Distance(Color c) => Math.Abs(c.R - accent.R) + Math.Abs(c.G - accent.G) + Math.Abs(c.B - accent.B);
                Check(onLeft ? Distance(a) < Distance(b) : Distance(b) < Distance(a), "Gradient opacity did not point toward the window.");
            }
        }
        widget.PaintSurface(true);
        panel.Left -= 3; // same side: moving must not trigger a full repaint on every pixel
        Check(!Get<bool>(widget, "_surfaceDirty"), "A same-side window movement invalidated the entire widget.");
        settings.CollapseSectionOnDetach = false;
        Call(widget, "SetCollapsed", "system", false);
        Call(widget, "CollapseDetachedSection", "system");
        Check(!settings.CollapsedSystem, "Collapse preference was ignored.");
        settings.HighlightDetachedSections = false;
        widget.ApplySettings(settings);
        using var disabled = Frame(widget, "detached/marker-disabled.png");
        SectionWindows.Close("system");
        Check(Call(widget, "DetachedHeaderSide", "system") == null && Get<bool>(widget, "_surfaceDirty"),
            "Closing the section window did not remove and invalidate its marker.");
        settings.CollapseSectionOnDetach = true;
        settings.CollapsedPrice = false;
        Get<HashSet<string>>(widget, "_expandedSummaries").Add("price");
        SectionWindows.OpenPinnedAt("price", left);
        Call(widget, "CollapseDetachedSection", "price");
        Check(!settings.CollapsedPrice && !Get<HashSet<string>>(widget, "_expandedSummaries").Contains("price"),
            "A pinned summary was not collapsed independently of its detail setting.");
        Call(widget, "ToggleHeader", "price", (Action)(() => { }));
        Check(Get<HashSet<string>>(widget, "_expandedSummaries").Contains("price"), "A detached pinned summary could not be expanded again.");
        SectionWindows.Close("price");
        Check(!settings.CollapsedPrice && Get<HashSet<string>>(widget, "_expandedSummaries").Contains("price"),
            "Closing a detached pinned summary did not expand it.");
        SectionWindows.OpenPinnedAt("system", left);
        Call(widget, "CollapseDetachedSection", "system");
        Check(settings.CollapsedSystem, "Section did not collapse before return test.");
        SectionWindows.Close("system");
        Check(!settings.CollapsedSystem, "Closing a detached section did not expand it in the widget.");
        settings.CollapseSectionOnDetach = false;
        var clone = settings.Clone();
        using var page = new WindowsPage();
        page.LoadFrom(clone);
        var saved = new AppSettings();
        page.SaveTo(saved);
        Check(!saved.CollapseSectionOnDetach && !saved.HighlightDetachedSections && saved.DetachedSectionHighlightOpacity == 95,
            "Detached section preferences did not round-trip through the settings page.");
        SectionWindows.CloseAllForShutdown();
        Console.WriteLine("PASS Detached sections: collapse/save, unchanged order, gradient directions in both themes, no per-pixel invalidation, preferences and closing cleanup");
    }
}

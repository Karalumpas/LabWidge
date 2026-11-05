using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

internal static partial class UiChecks
{
    /// <summary>
    /// Windows land on a screen that is there, dialogs open where the widget is, Alt+F4 hides the widget for good,
    /// the tray icon has a sane size, and the IP and DNS window follows the theme.
    /// </summary>
    private static void PlacementChecks()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;

        // A dialog opens centred in the area it is given, before and after it is shown
        var target = new Rectangle(area.Left + 50, area.Top + 40, Math.Min(900, area.Width - 100), Math.Min(700, area.Height - 80));
        using (var dialog = new Form { ClientSize = new Size(400, 300), ShowInTaskbar = false })
        {
            WindowZOrder.CenterOn(dialog, target);
            dialog.Show();
            Application.DoEvents();
            var centre = new Point(dialog.Left + dialog.Width / 2, dialog.Top + dialog.Height / 2);
            var expected = new Point(target.Left + target.Width / 2, target.Top + target.Height / 2);
            Check(Math.Abs(centre.X - expected.X) <= 2 && Math.Abs(centre.Y - expected.Y) <= 2,
                $"The dialog was not centred in the widget's screen area ({centre} vs {expected}).");
        }

        // A window left where no screen is comes back; one reachable by its top strip stays put
        using (var lost = new Form { StartPosition = FormStartPosition.Manual, ClientSize = new Size(300, 200), ShowInTaskbar = false })
        {
            lost.Show();
            lost.Location = new Point(SystemInformation.VirtualScreen.Right + 500, SystemInformation.VirtualScreen.Bottom + 500);
            WindowZOrder.KeepOnScreen(lost);
            Check(Screen.AllScreens.Any(s => s.WorkingArea.Contains(lost.Bounds)), $"A window off every screen was not moved back ({lost.Bounds}).");

            var straddle = new Point(area.Right - 150, area.Top + 100);
            lost.Location = straddle;
            WindowZOrder.KeepOnScreen(lost);
            Check(lost.Location == straddle, "A window partly on a screen was moved.");
        }
        Console.WriteLine("PASS UI: dialogs open centred on the widget's screen; windows off every screen come back, others stay");

        using (var fixture = new Fixture())
        {
            var widget = fixture.Widget;
            fixture.Settings.WidgetVisible = true;
            widget.ShowWidget();
            Application.DoEvents();

            // The screens changed while the widget was far away: it comes back without waiting to be shown again
            widget.Location = new Point(SystemInformation.VirtualScreen.Right + 800, area.Top + 100);
            widget.GetType().GetMethod("OnDisplaySettingsChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(widget, new object?[] { null, EventArgs.Empty });
            PumpUntil(() => Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(widget.Bounds)));

            // Alt+F4 (a user close) hides the widget and remembers it
            var saves = fixture.SaveCount;
            widget.Close();
            Application.DoEvents();
            Check(!widget.IsDisposed && !widget.Visible, "Alt+F4 did not just hide the widget.");
            Check(!fixture.Settings.WidgetVisible && fixture.SaveCount > saves, "A widget hidden with Alt+F4 would show again at the next start.");
        }
        Console.WriteLine("PASS UI: the widget follows a screen change, and Alt+F4 hides it until it is shown again");

        var icon = TrayAppContext.TrayIconSize();
        Check(icon is >= 16 and <= 64, $"The tray icon size is not sensible: {icon}.");
        Console.WriteLine($"PASS UI: the tray icon follows the taskbar's scaling ({icon} px here)");

        // The IP and DNS window in both themes: no light tab strip in the dark theme
        foreach (var theme in new[] { WidgetTheme.Dark, WidgetTheme.Light })
        {
            var settings = new AppSettings { Theme = theme };
            PopupForm.ShowPopup("203.0.113.10", "2026-11-05 20:00:00", settings, () => { }, () => Task.CompletedTask, () => { }, () => { }, () => Task.CompletedTask);
            var popup = (PopupForm)typeof(PopupForm).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Application.DoEvents();
            var palette = Palette.For(settings);
            Check(popup.BackColor == SettingsTheme.PageColor(palette), $"The {theme} window does not use the theme's colours.");
            Check(!Descendants(popup).OfType<TabControl>().Any(), "The window still has a system tab strip.");
            Check(Descendants(popup).OfType<Label>().All(l => l.ForeColor != Color.Black || !palette.IsDark), "Black text in the dark theme.");
            using (var bitmap = new Bitmap(popup.Width, popup.Height))
            {
                popup.DrawToBitmap(bitmap, new Rectangle(Point.Empty, popup.Size));
                Directory.CreateDirectory(Path.Combine(Output, "settings"));
                bitmap.Save(Path.Combine(Output, "settings", $"ip-dns-{theme}.png"), ImageFormat.Png);
                var corner = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height - 60);
                Check(palette.IsDark ? corner.GetBrightness() < 0.3f : corner.GetBrightness() > 0.7f, $"The {theme} window was drawn in the other theme's colours.");
            }
            PopupForm.SetStatus("Error", StatusLevel.Error);
            popup.Close();
        }
        Console.WriteLine("PASS UI: the IP and DNS window follows the light and dark theme");
    }
}

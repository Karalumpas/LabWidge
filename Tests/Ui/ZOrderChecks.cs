using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static partial class UiChecks
{
    /// <summary>A window of another program over the widget: the tray click must bring the widget up, not hide it.</summary>
    private static void ZOrderChecks()
    {
        using var fixture = new Fixture();
        var widget = fixture.Widget;
        fixture.Settings.WidgetTopMost = false;
        widget.ApplySettings(fixture.Settings);
        widget.ShowWidget();
        Application.DoEvents();
        Check(!widget.IsCovered(), "A freshly shown widget counted as covered.");

        // Another process, since the widget's own windows never count as covering it
        var script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object Windows.Forms.Form; "
                     + "$f.Text = 'LabWidge z-order check'; $f.StartPosition = 'Manual'; "
                     + $"$f.Bounds = New-Object Drawing.Rectangle({widget.Left - 40}, {widget.Top - 40}, {widget.Width + 80}, {widget.Height + 80}); "
                     + "[Windows.Forms.Application]::Run($f)";
        using var cover = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{script}\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var hwnd = IntPtr.Zero;
            var until = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < until && (hwnd = FindWindow(null, "LabWidge z-order check")) == IntPtr.Zero)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
            Check(hwnd != IntPtr.Zero, "The covering window did not open.");
            // Put it above the widget, as the user activating another app would
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
            Application.DoEvents();
            Check(widget.IsCovered(), "A window over the widget was not seen.");

            widget.BringToTop();
            Application.DoEvents();
            Check(!widget.IsCovered(), "The widget did not come to the top.");
            Check(!widget.TopMost, "Bringing the widget up left it always on top.");
            Console.WriteLine("PASS UI: a covered widget is noticed and brought to the top without becoming always on top");
        }
        finally
        {
            if (!cover.HasExited) cover.Kill();
            widget.Hide();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}

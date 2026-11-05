using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static partial class UiChecks
{
    /// <summary>
    /// A window of another program over the widget and a pinned window: the tray click must bring them up, not hide
    /// the widget. Also the pieces that make a second start, an open dialog and a hidden tray icon visible.
    /// </summary>
    private static void ZOrderChecks()
    {
        using var fixture = new Fixture();
        var widget = fixture.Widget;
        fixture.Settings.WidgetTopMost = false;
        widget.ApplySettings(fixture.Settings);
        widget.ShowWidget();
        var area = Screen.FromControl(widget).WorkingArea;
        SectionWindows.OpenPinnedAt("system", new Point(area.Left + 60, area.Top + 60));
        var open = (IDictionary)typeof(SectionWindows).GetField("Open", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var pinned = (PopupPanel)open["system"]!;
        Application.DoEvents();
        Check(pinned.Pinned && !pinned.TopMost, "The pinned window did not follow \"always on top\" off.");
        Check(!widget.IsCovered() && !SectionWindows.AnyPinnedCovered(), "Freshly shown windows counted as covered.");

        // Another process, since LabWidge's own windows never count as covering it
        var cover = Rectangle.Union(widget.Bounds, pinned.Bounds);
        cover.Inflate(40, 40);
        var script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object Windows.Forms.Form; "
                     + "$f.Text = 'LabWidge z-order check'; $f.StartPosition = 'Manual'; "
                     + $"$f.Bounds = New-Object Drawing.Rectangle({cover.Left}, {cover.Top}, {cover.Width}, {cover.Height}); "
                     + "[Windows.Forms.Application]::Run($f)";
        using var other = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{script}\"")
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
            // Put it above LabWidge's windows, as the user activating another app would
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
            Application.DoEvents();
            Check(widget.IsCovered(), "A window over the widget was not seen.");
            Check(SectionWindows.AnyPinnedCovered(), "A window over the pinned window was not seen.");

            SectionWindows.BringPinnedToTop();
            widget.BringToTop();
            Application.DoEvents();
            Check(!widget.IsCovered() && !SectionWindows.AnyPinnedCovered(), "The widget and pinned window did not come to the top.");
            Check(!widget.TopMost && !pinned.TopMost, "Bringing the windows up left them always on top.");
            Console.WriteLine("PASS UI: a covered widget and pinned window are noticed and brought up without becoming always on top");

            // An open settings window that was minimized and covered comes back when asked for again
            using var dialog = new Form { Text = "LabWidge dialog check", StartPosition = FormStartPosition.Manual, Bounds = widget.Bounds, ShowInTaskbar = true };
            dialog.Show();
            dialog.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            WindowZOrder.BringDialogForward(dialog);
            Application.DoEvents();
            Check(dialog.WindowState == FormWindowState.Normal, "A minimized dialog was not restored.");
            Check(!WindowZOrder.IsCovered(dialog), "The dialog was not brought above the other program's window.");
            Console.WriteLine("PASS UI: an open dialog asked for again is restored and brought forward");
        }
        finally
        {
            if (!other.HasExited) other.Kill();
            pinned.CloseForShutdown();
            widget.Hide();
        }

        // A second start reaches the running instance through the named event
        using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName))
        {
            typeof(Program).GetMethod("SignalRunningInstance", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            Check(show.WaitOne(2000), "A second start did not signal the running instance.");
        }
        Console.WriteLine("PASS UI: a second start signals the running LabWidge instead of doing nothing");

        // Windows 11 keeps a new tray icon under ^ until the user turns it on by the clock
        var win11 = new Version(10, 0, 26100);
        const string exe = @"C:\Program Files\WindowsApps\Mede.LabWidge_1.24.0.0_x64__mt9dvqt5fydp0\LabWidge.exe";
        const string stored = @"{6D809377-6AF0-444B-8957-A3773F02200E}\WindowsApps\Mede.LabWidge_1.24.0.0_x64__mt9dvqt5fydp0\LabWidge.exe";
        Check(TrayIconVisibility.SamePath(stored, exe), "A path stored with a known folder id was not recognised.");
        Check(!TrayIconVisibility.SamePath(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Other\LabWidge.exe", exe), "Another program's path matched.");
        Check(TrayIconVisibility.IsHidden(exe, new[] { (stored, false) }, win11), "An icon under ^ was not seen as hidden.");
        Check(TrayIconVisibility.IsHidden(exe, Array.Empty<(string, bool)>(), win11), "A new icon without an entry was not seen as hidden.");
        Check(!TrayIconVisibility.IsHidden(exe, new[] { (stored, true) }, win11), "An icon by the clock was seen as hidden.");
        Check(!TrayIconVisibility.IsHidden(@"C:\Users\x\AppData\Local\LabWidge\LabWidge.exe", new[] { (@"C:\Users\x\AppData\Local\LabWidge\labwidge.EXE", true) }, win11),
            "A plain path was not matched without regard to case.");
        Check(!TrayIconVisibility.IsHidden(exe, Array.Empty<(string, bool)>(), new Version(10, 0, 19045)), "Windows 10 was told about Windows 11's ^.");
        Console.WriteLine("PASS UI: the welcome hint knows when Windows keeps the bolt under ^");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}

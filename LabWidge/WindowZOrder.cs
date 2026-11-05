using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// Where LabWidge's windows are: whether one is covered by another program's window, bringing it to the top without
/// taking focus, and keeping windows on a screen that is there.
/// </summary>
internal static class WindowZOrder
{
    /// <summary>
    /// True when a visible window of another program lies over <paramref name="form"/>. Invisible overlays,
    /// cloaked windows (other virtual desktops, suspended apps) and the taskbar do not count.
    /// </summary>
    public static bool IsCovered(Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated || !form.Visible) return false;
        var own = FrameBounds(form.Handle);
        var pid = (uint)Environment.ProcessId;
        // GW_HWNDPREV walks up the z-order: every window found is above this one
        for (var hwnd = GetWindow(form.Handle, GwHwndPrev); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GwHwndPrev))
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid) continue; // LabWidge's own windows: the widget, tooltips and section windows
            if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExTransparent) != 0) continue;
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (IsShellWindow(hwnd)) continue;

            var overlap = System.Drawing.Rectangle.Intersect(own, FrameBounds(hwnd));
            if (overlap.Width > 4 && overlap.Height > 4) return true;
        }
        return false;
    }

    /// <summary>Puts the window above all normal windows, without activating it.</summary>
    public static void BringToTop(Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        // A normal window is raised by making it topmost for a moment; HWND_NOTOPMOST then leaves it at the top of the normal windows
        SetWindowPos(form.Handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        if (!form.TopMost) SetWindowPos(form.Handle, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// Brings a dialog forward: restored if minimized, raised, and focused where Windows allows it. A message box
    /// the dialog has open gets the focus instead, since the dialog itself is disabled while it shows.
    /// </summary>
    public static void BringDialogForward(Form dialog)
    {
        if (dialog.IsDisposed || !dialog.IsHandleCreated) return;
        if (dialog.WindowState == FormWindowState.Minimized) dialog.WindowState = FormWindowState.Normal;
        BringToTop(dialog);
        var target = GetLastActivePopup(dialog.Handle);
        // Without permission to take focus, Windows flashes the taskbar button instead – still a sign the window is open
        SetForegroundWindow(target != IntPtr.Zero ? target : dialog.Handle);
    }

    /// <summary>
    /// Opens a dialog centred in <paramref name="area"/> (e.g. the screen the widget is on) instead of where Windows
    /// would put it. Centred again once it is shown if Windows rescaled it for that screen's DPI.
    /// </summary>
    public static void CenterOn(Form form, Rectangle area)
    {
        form.StartPosition = FormStartPosition.Manual;
        var loaded = Size.Empty;
        void Center() => form.Location = new Point(area.Left + Math.Max(0, (area.Width - form.Width) / 2),
                                                   area.Top + Math.Max(0, (area.Height - form.Height) / 2));
        form.Load += (_, _) => { Center(); loaded = form.Size; };
        form.Shown += (_, _) => { if (form.Size != loaded) Center(); };
    }

    /// <summary>Moves a window that no screen shows any more (a screen was unplugged) onto the nearest screen.</summary>
    public static void KeepOnScreen(Form form)
    {
        if (form.IsDisposed || !form.Visible) return;
        // A window still reachable by its top strip stays where the user put it, also across two screens
        var top = new Rectangle(form.Left + 20, form.Top + 4, Math.Max(1, form.Width - 40), 24);
        if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(top))) return;
        var area = Screen.FromRectangle(form.Bounds).WorkingArea;
        var x = Math.Clamp(form.Left, area.Left, Math.Max(area.Left, area.Right - form.Width));
        var y = Math.Clamp(form.Top, area.Top, Math.Max(area.Top, area.Bottom - form.Height));
        if (x != form.Left || y != form.Top) form.Location = new Point(x, y);
    }

    /// <summary>The taskbars and the tray's flyouts, which are open right when the tray icon is clicked.</summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var name = new System.Text.StringBuilder(64);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow"
            or "TopLevelWindowForOverflowXamlIsland" or "Xaml_WindowedPopupClass" or "XamlExplorerHostIslandWindow";
    }

    /// <summary>The visible frame: GetWindowRect includes the invisible resize borders around normal windows.</summary>
    private static System.Drawing.Rectangle FrameBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out Rect r, Marshal.SizeOf<Rect>()) != 0) GetWindowRect(hwnd, out r);
        return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private const uint GwHwndPrev = 3;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10;
    private static readonly IntPtr HwndTopMost = new(-1), HwndNoTopMost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect value, int size);
}

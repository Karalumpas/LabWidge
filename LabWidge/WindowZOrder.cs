using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>Whether a window is covered by another program's window, and bringing it to the top without taking focus.</summary>
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

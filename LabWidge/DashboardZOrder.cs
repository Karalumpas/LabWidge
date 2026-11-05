using System.Runtime.InteropServices;

/// <summary>Whether the widget is covered by another window, and bringing it to the top without taking focus.</summary>
internal sealed partial class DashboardForm
{
    /// <summary>
    /// True when a visible window of another program lies over the widget. Invisible overlays, cloaked
    /// windows (other virtual desktops, suspended apps) and the taskbar do not count.
    /// </summary>
    public bool IsCovered()
    {
        if (!IsHandleCreated || !Visible) return false;
        var own = FrameBounds(Handle);
        var pid = (uint)Environment.ProcessId;
        // GW_HWNDPREV walks up the z-order: every window found is above the widget
        for (var hwnd = GetWindow(Handle, GwHwndPrev); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GwHwndPrev))
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid) continue; // the widget's own tooltip and section windows
            if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExTransparent) != 0) continue;
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (IsShellWindow(hwnd)) continue;

            var other = FrameBounds(hwnd);
            var overlap = System.Drawing.Rectangle.Intersect(own, other);
            if (overlap.Width > 4 && overlap.Height > 4) return true;
        }
        return false;
    }

    /// <summary>Puts the widget above all normal windows, without activating it.</summary>
    public void BringToTop()
    {
        if (!IsHandleCreated) return;
        // A normal window is raised by making it topmost for a moment; HWND_NOTOPMOST then leaves it at the top of the normal windows
        SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        if (!TopMost) SetWindowPos(Handle, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect value, int size);
}

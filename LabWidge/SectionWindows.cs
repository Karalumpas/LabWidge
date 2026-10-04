using System.Drawing;

/// <summary>
/// Keeps track of the open section windows: at most one per section. The widget gives it a factory that knows the
/// services; the tray app restores pinned windows at start and closes them at exit.
/// </summary>
internal static class SectionWindows
{
    /// <summary>The sections that have a window, in the order they are offered.</summary>
    public static readonly string[] Keys = WidgetPlugins.All.Select(p => p.Key).ToArray();

    private static readonly Dictionary<string, PopupPanel> Open = new();
    private static readonly Dictionary<string, DateTime> ClosedAt = new();

    /// <summary>The current settings; set by the widget whenever they are replaced.</summary>
    public static AppSettings? Settings { get; set; }

    /// <summary>Creates the window for a section, or null if the section has nothing to show (e.g. not set up).</summary>
    public static Func<string, PopupPanel?>? Factory { get; set; }
    public static event Action<string>? WindowOpened;

    public static bool Supports(string key) => Keys.Contains(key);

    public static bool IsOpen(string key) => Open.TryGetValue(key, out var w) && !w.IsDisposed && w.Visible;

    public static bool AnyOpen(params string[] keys) => keys.Any(IsOpen);

    /// <summary>Opens the section's window next to the widget – or closes it if it is already open and not pinned.</summary>
    public static void Toggle(string key, Rectangle near)
    {
        if (IsOpen(key))
        {
            var open = Open[key];
            if (open.Pinned) open.Activate();
            else open.Close();
            return;
        }

        // A click on the widget first makes a pop-up lose focus and close; without this grace period
        // the same click would open it again straight away
        if (ClosedAt.TryGetValue(key, out var closed) && (DateTime.Now - closed).TotalMilliseconds < 250) return;
        Create(key)?.ShowNear(near);
    }

    /// <summary>Opens the section's window pinned at a point on the desktop – a section dragged out of the widget.</summary>
    public static void OpenPinnedAt(string key, Point screen)
    {
        if (IsOpen(key))
        {
            Open[key].ShowPinnedAt(screen);
            return;
        }
        Create(key)?.ShowPinnedAt(screen);
    }

    /// <summary>Opens the windows that were pinned and open when LabWidge closed.</summary>
    public static void RestorePinned(AppSettings settings)
    {
        foreach (var key in Keys)
        {
            if (settings.IsPluginEnabled(key) && settings.SectionWindows.TryGetValue(key, out var state) && state.Pinned && state.Open && !IsOpen(key))
            {
                try
                {
                    Create(key)?.ShowRestored();
                }
                catch (Exception ex)
                {
                    Logger.Error($"The {key} window could not be restored: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Closes the pop-ups (not the pinned windows), e.g. when the widget is hidden.</summary>
    public static void ClosePopups()
    {
        foreach (var w in Open.Values.ToList())
            if (!w.IsDisposed && !w.Pinned) w.Close();
    }

    public static void Close(string key)
    {
        if (IsOpen(key)) Open[key].Close();
    }

    /// <summary>Closes every window at exit; pinned windows remember that they were open.</summary>
    public static void CloseAllForShutdown()
    {
        foreach (var w in Open.Values.ToList())
            if (!w.IsDisposed) w.CloseForShutdown();
        Open.Clear();
    }

    public static void ApplySettings(AppSettings settings)
    {
        foreach (var w in Open.Values.ToList())
            if (!w.IsDisposed)
            {
                if (!settings.IsPluginEnabled(w.Key)) w.CloseForShutdown();
                else w.ApplySettings(settings);
            }
    }

    private static PopupPanel? Create(string key)
    {
        var window = Factory?.Invoke(key);
        if (window == null) return null;
        Open[key] = window;
        window.Shown += (_, _) => WindowOpened?.Invoke(key);
        window.FormClosed += (_, _) =>
        {
            ClosedAt[key] = DateTime.Now;
            if (Open.TryGetValue(key, out var current) && ReferenceEquals(current, window)) Open.Remove(key);
        };
        return window;
    }
}

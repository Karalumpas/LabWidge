using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

/// <summary>
/// The icons for the rail. Programs and shortcuts get their Windows icon in high resolution. Websites get the icon the site
/// itself advertises; only when the site gives none (some block programs) is DuckDuckGo's icon service asked with the host name.
/// A website's icon is kept on disk, so it is fetched once. Without an icon, a letter is drawn.
/// </summary>
internal static class LaunchIcons
{
    public const int Size = 64;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Loading = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    /// <summary>The shell sometimes fails when several icons are extracted at once – one at a time is reliable.</summary>
    private static readonly object ShellGate = new();

    public static string CacheDir { get; } = Path.Combine(WebViewProfile.DataDir, "icons");

    /// <summary>Raised (on a background thread) when an icon has finished loading.</summary>
    public static event Action? Updated;

    private static HttpClient CreateHttp()
    {
        var http = HttpClientFactory.Create(TimeSpan.FromSeconds(15));
        // Many sites answer a bot-like user agent with an error page instead of their HTML
        http.DefaultRequestHeaders.UserAgent.Clear();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 LabWidge");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,image/png,image/*;q=0.9,*/*;q=0.8");
        return http;
    }

    private static string Key(LaunchItem item) =>
        !string.IsNullOrWhiteSpace(item.IconPath) ? "file:" + item.IconPath!.Trim()
        : LaunchItem.WebUri(item.Target) is { } uri ? "web:" + uri.Authority.ToLowerInvariant()
        : "file:" + item.LaunchTarget();

    /// <summary>The icon if it is ready – otherwise null, and it starts loading.</summary>
    public static Bitmap? Get(LaunchItem item)
    {
        var key = Key(item);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var icon)) return icon;
            if (!Loading.Add(key)) return null;
        }
        var target = item.LaunchTarget();
        var iconPath = item.IconPath;
        _ = Task.Run(async () =>
        {
            Bitmap? icon = null;
            try
            {
                icon = !string.IsNullOrWhiteSpace(iconPath) ? FromFile(Environment.ExpandEnvironmentVariables(iconPath.Trim().Trim('"')))
                    : LaunchItem.WebUri(target) is { } uri ? await FromWebAsync(uri)
                    : await OnSta(() => ShellIcon(target));
            }
            catch (Exception ex)
            {
                Logger.Info($"Rail: no icon for {target}: {ex.Message}");
            }
            lock (Gate)
            {
                Cache[key] = icon;
                Loading.Remove(key);
            }
            Updated?.Invoke();
        });
        return null;
    }

    /// <summary>Forgets the icons, also the saved website icons, so they are fetched again.</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            foreach (var icon in Cache.Values) icon?.Dispose();
            Cache.Clear();
        }
        try { if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, recursive: true); }
        catch (Exception ex) { Logger.Error($"Could not delete the icon cache: {ex.Message}"); }
        Updated?.Invoke();
    }

    // ---------- Files and programs ----------

    private static Bitmap? FromFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" && File.Exists(path))
            return Decode(File.ReadAllBytes(path));
        return OnSta(() => ShellIcon(path)).GetAwaiter().GetResult();
    }

    /// <summary>The shell's own icon for a file, shortcut, program or shell:AppsFolder item – as it looks in Explorer.</summary>
    private static Bitmap? ShellIcon(string path)
    {
        if (path.Contains("://")) return null; // a protocol like discord:// has no file to take an icon from
        lock (ShellGate) return ShellIconCore(path);
    }

    private static Bitmap? ShellIconCore(string path)
    {
        var guid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref guid, out var factory) != 0 || factory == null)
        {
            // "notepad.exe" without a folder: let Windows find it on the PATH
            var found = SearchPath(path);
            if (found == null || SHCreateItemFromParsingName(found, IntPtr.Zero, ref guid, out factory) != 0 || factory == null) return null;
        }
        try
        {
            const int BiggerSizeOk = 0x1, IconOnly = 0x4;
            if (factory.GetImage(new NativeSize(Size, Size), BiggerSizeOk | IconOnly, out var hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            try { return FromHBitmap(hbitmap); }
            finally { DeleteObject(hbitmap); }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static string? SearchPath(string file)
    {
        if (Path.IsPathRooted(file)) return null;
        var sb = new StringBuilder(260);
        return SearchPathW(null, file, null, sb.Capacity, sb, IntPtr.Zero) > 0 ? sb.ToString() : null;
    }

    /// <summary>Copies a 32-bit shell bitmap with its transparency (Image.FromHbitmap would drop the alpha channel).</summary>
    private static Bitmap FromHBitmap(IntPtr hbitmap)
    {
        GetObject(hbitmap, Marshal.SizeOf<NativeBitmap>(), out var info);
        if (info.bmBitsPixel != 32 || info.bmBits == IntPtr.Zero) return Scale(Image.FromHbitmap(hbitmap));
        using var view = new Bitmap(info.bmWidth, info.bmHeight, info.bmWidthBytes, PixelFormat.Format32bppPArgb, info.bmBits);
        var copy = new Bitmap(info.bmWidth, info.bmHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(copy)) g.DrawImage(view, 0, 0, info.bmWidth, info.bmHeight);
        copy.RotateFlip(RotateFlipType.RotateNoneFlipY); // a DIB section is stored bottom-up
        return Scale(copy);
    }

    private static Task<T> OnSta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    // ---------- Websites ----------

    private static async Task<Bitmap?> FromWebAsync(Uri page)
    {
        var file = Path.Combine(CacheDir, Hash(page.Authority.ToLowerInvariant()) + ".png");
        if (File.Exists(file))
        {
            try { return Decode(await File.ReadAllBytesAsync(file)); }
            catch { /* damaged – fetched again */ }
        }

        var candidates = new List<Uri>();
        try
        {
            using var response = await Http.GetAsync(page, HttpCompletionOption.ResponseHeadersRead);
            var final = response.RequestMessage?.RequestUri ?? page;
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.Contains("html") != false)
            {
                var (bytes, _) = await ReadAtMostAsync(response, 768 * 1024);
                var html = Encoding.UTF8.GetString(bytes);
                candidates.AddRange(IconLinks(html, final));
            }
            candidates.Add(new Uri(final, "/apple-touch-icon.png"));
            candidates.Add(new Uri(final, "/favicon.ico"));
        }
        catch (Exception ex)
        {
            Logger.Info($"Rail: could not read {page.Host}: {ex.Message}");
            candidates.Add(new Uri(page, "/apple-touch-icon.png"));
            candidates.Add(new Uri(page, "/favicon.ico"));
        }
        // Last resort for sites that turn programs away (e.g. behind Cloudflare's bot check): only the host name is sent
        candidates.Add(new Uri($"https://icons.duckduckgo.com/ip3/{Uri.EscapeDataString(page.Host)}.ico"));

        foreach (var url in candidates.Distinct())
        {
            try
            {
                // An icon is a few kB; a site that sends more is not read into memory
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxIconBytes) continue;
                var (bytes, complete) = await ReadAtMostAsync(response, MaxIconBytes);
                if (!complete || Decode(bytes) is not { } icon) continue;
                Directory.CreateDirectory(CacheDir);
                icon.Save(file, ImageFormat.Png);
                return icon;
            }
            catch
            {
                // the next candidate
            }
        }
        return null;
    }

    private const int MaxIconBytes = 2 * 1024 * 1024;

    /// <summary>Reads at most <paramref name="max"/> bytes. Complete is false when the response is longer.</summary>
    private static async Task<(byte[] Bytes, bool Complete)> ReadAtMostAsync(HttpResponseMessage response, int max)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length < max)
        {
            var n = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, max - buffer.Length)));
            if (n == 0) return (buffer.ToArray(), true);
            buffer.Write(chunk, 0, n);
        }
        return (buffer.ToArray(), await stream.ReadAsync(chunk.AsMemory(0, 1)) == 0);
    }

    /// <summary>The icons a page lists in its &lt;link&gt; tags, the largest first. SVG is left out – GDI+ cannot draw it.</summary>
    internal static IEnumerable<Uri> IconLinks(string html, Uri baseUri)
    {
        var found = new List<(Uri Url, int Score)>();
        foreach (Match tag in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var rel = Attribute(tag.Value, "rel")?.ToLowerInvariant() ?? "";
            var href = Attribute(tag.Value, "href");
            if (!rel.Contains("icon") || rel.Contains("mask") || string.IsNullOrWhiteSpace(href)) continue;
            var type = Attribute(tag.Value, "type") ?? "";
            href = System.Net.WebUtility.HtmlDecode(href.Trim());
            if (type.Contains("svg") || href.Contains(".svg", StringComparison.OrdinalIgnoreCase) || href.StartsWith("data:")) continue;
            if (!Uri.TryCreate(baseUri, href, out var url) || url.Scheme is not ("http" or "https")) continue;
            var sizes = Attribute(tag.Value, "sizes") ?? "";
            var size = Regex.Matches(sizes, @"(\d+)\s*[xX]\s*\d+").Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
            if (size == 0) size = rel.Contains("apple-touch") ? 180 : 32;
            // Prefer the size closest to what the rail draws – a 512 px icon is only a slower download
            var score = size >= Size ? 10000 - size : size;
            found.Add((url, score));
        }
        return found.OrderByDescending(f => f.Score).Select(f => f.Url);
    }

    private static string? Attribute(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{name}\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        return !m.Success ? null : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
    }

    // ---------- Images ----------

    /// <summary>
    /// Reads PNG, JPEG, GIF, BMP, ICO (the largest image in it) and – through Windows' own decoders – WebP, which many sites use.
    /// Null when it is not an image or too small to use.
    /// </summary>
    internal static Bitmap? Decode(byte[] bytes)
    {
        if (bytes.Length < 16) return null;
        Bitmap? source = null;
        try
        {
            using var stream = new MemoryStream(bytes);
            if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
            {
                using var ico = new Icon(stream, 256, 256); // the frame closest to 256 px
                source = ico.ToBitmap();
            }
            else
            {
                using var image = Image.FromStream(stream);
                source = new Bitmap(image);
            }
        }
        catch
        {
            try { source = DecodeWithWindows(bytes).GetAwaiter().GetResult(); }
            catch { source = null; }
        }
        if (source == null) return null;
        if (source.Width < 16 || source.Height < 16) { source.Dispose(); return null; }
        return Scale(source);
    }

    private static async Task<Bitmap> DecodeWithWindows(byte[] bytes)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var frame = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Straight);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyToBuffer(pixels.AsBuffer());
        var bitmap = new Bitmap(frame.PixelWidth, frame.PixelHeight, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < bitmap.Height; y++)
                Marshal.Copy(pixels, y * bitmap.Width * 4, data.Scan0 + y * data.Stride, bitmap.Width * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    /// <summary>Scales down to <see cref="Size"/> (never up) and disposes the original.</summary>
    private static Bitmap Scale(Bitmap source)
    {
        if (source.Width <= Size && source.Height <= Size) return source;
        var factor = (float)Size / Math.Max(source.Width, source.Height);
        var result = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * factor)), Math.Max(1, (int)Math.Round(source.Height * factor)), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, result.Width, result.Height);
        }
        source.Dispose();
        return result;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>A letter on a coloured tile, for items without an icon. The colour follows the name, so it stays the same.</summary>
    public static void DrawLetter(Graphics g, string name, RectangleF rect, Font font)
    {
        var letter = name.Trim().FirstOrDefault(char.IsLetterOrDigit);
        var hue = name.Aggregate(17, (h, c) => h * 31 + char.ToLowerInvariant(c)) & 0x7fffffff;
        var colors = new[] { Color.FromArgb(88, 101, 242), Color.FromArgb(229, 72, 77), Color.FromArgb(16, 163, 127),
                             Color.FromArgb(245, 159, 0), Color.FromArgb(193, 53, 132), Color.FromArgb(0, 120, 212) };
        using (var path = WidgetIcon.RoundedRect(rect, rect.Width * 0.24f))
        using (var brush = new SolidBrush(colors[hue % colors.Length]))
            g.FillPath(brush, path);
        TextRenderer.DrawText(g, letter == default ? "?" : char.ToUpperInvariant(letter).ToString(), font, Rectangle.Round(rect), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping);
    }

    // ---------- Interop ----------

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize
    {
        public readonly int Width, Height;
        public NativeSize(int width, int height) { Width = width; Height = height; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int SearchPathW(string? path, string file, string? extension, int length, StringBuilder buffer, IntPtr filePart);
}

/// <summary>
/// The shortcuts: a rail docked on the left or the right of the widget, or a section among the others. The rail is a pill centred
/// on the widget's height that grows with the number of icons, and scrolls with the wheel when they do not fit. Drag the rail –
/// or the section's header – to dock it on the other side or to drop it between the sections.
/// </summary>
internal sealed partial class DashboardForm
{
    public const string ShortcutsKey = "shortcuts";
    private const float RailLogicalWidth = 56;
    private const float RailTile = 44;
    private const float RailGap = 6;
    private const float RailEdge = 14;     // the tab keeps this far from the widget's top and bottom
    private const float RailPadOut = 7;    // room between the icons and the tab's outer edge
    private const float RailPadIn = 5;
    private const float RailPadV = 6;
    private const float RailDockTile = 40;
    private const float RailDockGap = 4;

    private float _railScroll;
    private bool _downRail;
    private RailDrag? _railDrag;

    private sealed class RailDrag
    {
        public RailPlacement Target;
        /// <summary>The section the shortcuts are dropped in front of; null = after the last one.</summary>
        public string? Before;
        public float LineY;
        public Point Mouse;
    }

    private bool ShowsShortcuts => _settings.ShowLaunchRail && _settings.LaunchItems.Count > 0;
    private bool RailDocked => ShowsShortcuts && _settings.LaunchRailPlacement != RailPlacement.Section && !_settings.CompactMode;

    /// <summary>How wide the docked rail is right now – 0 when it is off, empty, a section or the widget is compact.</summary>
    private float RailWidth => RailDocked ? U(RailLogicalWidth) : 0;

    /// <summary>Where the sections start: after the rail when it is docked on the left.</summary>
    private float ContentLeft => RailDocked && _settings.LaunchRailPlacement == RailPlacement.Left ? RailWidth : 0;

    private RectangleF RailColumn() => !RailDocked ? RectangleF.Empty
        : _settings.LaunchRailPlacement == RailPlacement.Left ? new RectangleF(0, 0, RailWidth, ClientSize.Height)
        : new RectangleF(ClientSize.Width - RailWidth, 0, RailWidth, ClientSize.Height);

    private Color PillColor => _p.IsDark ? Color.FromArgb(27, 32, 41) : Color.FromArgb(240, 243, 247);

    private void OnLaunchIconsUpdated() => OnDataUpdated();

    /// <summary>
    /// The tab around the docked rail's icons, laid out as if docked on the left: against the widget's edge, centred on its height
    /// and growing with the icons up to the full height. The rest of the rail column is transparent.
    /// </summary>
    private RectangleF RailTab(float height)
    {
        float tile = U(RailDockTile), gap = U(RailDockGap), top = U(RailEdge);
        var view = height - top * 2;
        var content = _settings.LaunchItems.Count * (tile + gap) - gap;
        var tabHeight = Math.Min(content + U(RailPadV) * 2, view);
        var tabWidth = tile + U(RailPadOut) + U(RailPadIn);
        return new RectangleF(RailWidth - tabWidth, top + (view - tabHeight) / 2, tabWidth, tabHeight);
    }

    private void DrawRail(Graphics g, float width, float height, float top)
    {
        if (!RailDocked) return;
        var rail = RailWidth;
        var right = _settings.LaunchRailPlacement == RailPlacement.Right;
        var count = _settings.LaunchItems.Count;
        float tile = U(RailDockTile), gap = U(RailDockGap), padV = U(RailPadV), step = tile + gap;

        // Laid out as if docked on the left; a rail on the right is the mirror image
        RectangleF Box(RectangleF r) => right ? r with { X = width - r.Right } : r;

        var tab = RailTab(height);
        var max = Math.Max(0, count * step - gap - (tab.Height - padV * 2));
        _railScroll = Math.Clamp(_railScroll, 0, max);
        Action<int>? wheel = max > 0 ? n => { _railScroll = Math.Clamp(_railScroll - n * step, 0, max); Invalidate(); } : null;

        // The tab's background is the widget's own shape inside the rail column; its border is the widget's outline
        var fillState = g.Save();
        g.SetClip(Box(new RectangleF(0, 0, rail, height)), CombineMode.Intersect);
        using (var shape = WidgetShape(width, height))
        using (var brush = new SolidBrush(PillColor))
            g.FillPath(brush, shape);
        g.Restore(fillState);

        // The wheel scrolls the rail when the icons do not fit; elsewhere the rail moves like the background – or is dragged to dock
        _hits.Add(new Hit(Box(tab),
            L.T("Drag to dock the shortcuts on the other side or drop them between the sections", "Træk for at docke genvejene i den anden side eller slippe dem mellem sektionerne"),
            null, wheel));

        var clip = Box(RectangleF.Inflate(tab, 0, -U(2)));
        var state = g.Save();
        g.SetClip(clip, CombineMode.Intersect);
        for (var i = 0; i < count; i++)
        {
            var rect = Box(new RectangleF(tab.X + U(RailPadOut), tab.Y + padV + i * step - _railScroll, tile, tile));
            if (rect.Bottom < clip.Top || rect.Top > clip.Bottom) continue;
            DrawLaunchTile(g, rect, i, clip, wheel, _p.Track);
        }
        g.Restore(state);

        if (max > 0)
        {
            // A soft fade inside the tab where more icons are hidden
            var fade = U(14);
            var inner = Box(new RectangleF(tab.X + U(1), tab.Y + U(1), tab.Width - U(2), tab.Height - U(2)));
            if (_railScroll > 0)
            {
                using var brush = new LinearGradientBrush(new RectangleF(inner.X, inner.Y - 1, inner.Width, fade + 1), PillColor, Color.FromArgb(0, PillColor), LinearGradientMode.Vertical);
                g.FillRectangle(brush, inner.X, inner.Y, inner.Width, fade);
            }
            if (_railScroll < max)
            {
                var y = inner.Bottom - fade;
                using var brush = new LinearGradientBrush(new RectangleF(inner.X, y - 1, inner.Width, fade + 1), Color.FromArgb(0, PillColor), PillColor, LinearGradientMode.Vertical);
                g.FillRectangle(brush, inner.X, y, inner.Width, fade);
            }
        }
    }

    /// <summary>One shortcut: its icon (or letter) on a tile that highlights under the mouse and opens the shortcut when clicked.</summary>
    private void DrawLaunchTile(Graphics g, RectangleF rect, int index, RectangleF clip, Action<int>? wheel, Color hover)
    {
        var item = _settings.LaunchItems[index];
        if (rect.Contains(_mouse) && _drag == null && _railDrag == null && clip.Contains(_mouse)) FillRound(g, rect, hover, rect.Width * 0.2f);

        var size = rect.Width * 0.68f;
        var icon = new RectangleF(rect.X + (rect.Width - size) / 2, rect.Y + (rect.Height - size) / 2, size, size);
        if (LaunchIcons.Get(item) is { } image)
        {
            // Keep the aspect ratio – some sites only offer a wide logo
            var scale = Math.Min(size / image.Width, size / image.Height);
            var w = image.Width * scale;
            var h = image.Height * scale;
            var mode = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, icon.X + (size - w) / 2, icon.Y + (size - h) / 2, w, h);
            g.InterpolationMode = mode;
        }
        else
        {
            LaunchIcons.DrawLetter(g, item.DisplayName, icon, rect.Width >= U(40) ? _f.BodyBold : _f.SmallBold);
        }

        var visible = RectangleF.Intersect(rect, clip);
        if (visible.Width <= 0 || visible.Height <= 0) return;
        _hits.Add(new Hit(visible, item.DisplayName + "\n" + (LaunchItem.WebUri(item.Target)?.Host ?? item.Target), () => LaunchRailItem(index), wheel));
    }

    /// <summary>The shortcuts as a section: a header and the icons in centred rows.</summary>
    internal float DrawShortcutsSection(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var count = s.LaunchItems.Count;
        y = Header(g, "", L.T("SHORTCUTS", "GENVEJE"),
            s.CollapsedShortcuts ? L.T(count == 1 ? "1 shortcut" : $"{count} shortcuts", count == 1 ? "1 genvej" : $"{count} genveje") : null,
            null, x, y, w, s.CollapsedShortcuts, () => ToggleCollapsed(() => s.CollapsedShortcuts, v => s.CollapsedShortcuts = v));
        if (s.CollapsedShortcuts) return y - U(6);
        return DrawShortcutGrid(g, x, y + U(2), w, U(RailTile), U(RailGap));
    }

    /// <summary>In the compact view the shortcuts are a row of smaller icons.</summary>
    private float DrawShortcutsCompact(Graphics g, float x, float y, float w) => DrawShortcutGrid(g, x, y, w, U(34), U(4));

    private float DrawShortcutGrid(Graphics g, float x, float y, float w, float tile, float gap)
    {
        var count = _settings.LaunchItems.Count;
        var perRow = Math.Max(1, (int)((w + gap) / (tile + gap)));
        for (var start = 0; start < count; start += perRow)
        {
            var inRow = Math.Min(perRow, count - start);
            var rowX = x + (w - (inRow * tile + (inRow - 1) * gap)) / 2;
            for (var i = 0; i < inRow; i++)
            {
                var rect = new RectangleF(rowX + i * (tile + gap), y, tile, tile);
                DrawLaunchTile(g, rect, start + i, rect, null, _p.HoverBg);
            }
            y += tile + gap;
        }
        return y - gap;
    }

    private void LaunchRailItem(int index)
    {
        if (index >= _settings.LaunchItems.Count) return;
        var item = _settings.LaunchItems[index];
        try
        {
            Process.Start(item.StartInfo())?.Dispose();
            Logger.Info($"Rail: opened {item.DisplayName}.");
        }
        catch (Exception ex)
        {
            ShowTipAtMouse(L.T($"Could not open {item.DisplayName}", $"Kunne ikke åbne {item.DisplayName}"));
            Logger.Error($"Rail: could not open {item.Target}: {ex.Message}");
        }
    }

    // ---------- Placement ----------

    private static string PlacementText(RailPlacement placement) => placement switch
    {
        RailPlacement.Left => L.T("Dock on the left", "Dock i venstre side"),
        RailPlacement.Right => L.T("Dock on the right", "Dock i højre side"),
        _ => L.T("Show as a section", "Vis som sektion")
    };

    /// <summary>
    /// Docks the shortcuts or makes them a section. As a section they go in front of <paramref name="before"/>, or after the last
    /// section in the scrolling middle when it is null.
    /// </summary>
    private void SetRailPlacement(RailPlacement placement, string? before = null, bool reorder = false)
    {
        _settings.LaunchRailPlacement = placement;
        if (placement == RailPlacement.Section && reorder)
        {
            var order = SectionOrder().Where(k => k != ShortcutsKey).ToList();
            var index = before != null ? order.IndexOf(before) : -1;
            if (index < 0)
            {
                var last = VisibleSections().LastOrDefault(s => s.Key != ShortcutsKey && PinOf(s.Key) == SectionPin.None);
                index = last != null ? order.IndexOf(last.Key) + 1 : order.Count;
            }
            order.Insert(index, ShortcutsKey);
            _settings.SectionOrder = order.ToArray();
            _settings.SectionPins.Remove(ShortcutsKey);
            _settings.CollapsedShortcuts = false;
        }
        _railScroll = 0;
        _hoverKey = null;
        _saveSettings();
        FitSize();
        Invalidate();
        Logger.Info($"Widget: shortcuts placed {placement}" + (placement == RailPlacement.Section && reorder ? $" before {before ?? "the end"}." : "."));
    }

    private void AddRailPlacementItems(ContextMenuStrip menu)
    {
        foreach (var placement in new[] { RailPlacement.Left, RailPlacement.Right, RailPlacement.Section })
        {
            var item = new ToolStripMenuItem(PlacementText(placement)) { Checked = _settings.LaunchRailPlacement == placement };
            item.Click += (_, _) => SetRailPlacement(placement, reorder: placement == RailPlacement.Section && _settings.LaunchRailPlacement != RailPlacement.Section);
            menu.Items.Add(item);
        }
    }

    private void ShowRailMenu(Point point)
    {
        _sectionMenu?.Dispose(); // see ShowSectionMenu
        var menu = _sectionMenu = new ContextMenuStrip();
        AddRailPlacementItems(menu);
        menu.Show(this, point);
    }

    // ---------- Dragging the rail ----------

    private void StartRailDrag(Point mouse)
    {
        _railDrag = new RailDrag();
        _tip.HideTip();
        _hoverKey = null;
        Capture = true;
        Cursor = Cursors.SizeAll;
        MoveRailDrag(mouse);
    }

    /// <summary>Near an edge the rail docks on that side; anywhere else it becomes a section where the line shows.</summary>
    private void MoveRailDrag(Point mouse)
    {
        var d = _railDrag!;
        d.Mouse = mouse;
        var width = ClientSize.Width;
        var edge = Math.Max(U(64), width * 0.2f);
        d.Target = mouse.X < edge ? RailPlacement.Left : mouse.X > width - edge ? RailPlacement.Right : RailPlacement.Section;
        if (d.Target == RailPlacement.Section)
        {
            var others = _sectionBounds.Where(b => b.Key != ShortcutsKey && PinOf(b.Key) == SectionPin.None).ToList();
            var next = others.FirstOrDefault(b => mouse.Y < (b.Top + b.Bottom) / 2);
            d.Before = next?.Key;
            d.LineY = next != null ? next.Top - U(11) : others.Count > 0 ? others[^1].Bottom + U(11) : _layout.MiddleTop + U(4);
            d.LineY = Math.Clamp(d.LineY, _layout.MiddleTop + U(2), Math.Max(_layout.MiddleTop + U(2), _layout.BottomTop - U(2)));
        }
        Invalidate();
    }

    private void DropRailDrag()
    {
        var d = _railDrag;
        if (d == null) return;
        _railDrag = null; // first: releasing the capture below calls this again
        Capture = false;
        Cursor = Cursors.Default;
        var area = ClientRectangle;
        area.Inflate((int)U(40), (int)U(40));
        if (!area.Contains(d.Mouse)) { Invalidate(); return; } // dropped far outside the widget: nothing changes
        SetRailPlacement(d.Target, d.Before, reorder: d.Target == RailPlacement.Section);
    }

    /// <summary>Where the shortcuts land: a dashed column at the side or a line between the sections, and a small card at the mouse.</summary>
    private void DrawRailDrop(Graphics g, float width, float height)
    {
        var d = _railDrag;
        if (d == null) return;
        var blue = _p.Blue;
        if (d.Target != RailPlacement.Section)
        {
            var rail = U(RailLogicalWidth);
            var rect = new RectangleF(d.Target == RailPlacement.Left ? U(4) : width - rail + U(4), U(8), rail - U(8), height - U(16));
            FillRound(g, rect, Color.FromArgb(_p.IsDark ? 40 : 28, blue), U(12));
            using var path = WidgetIcon.RoundedRect(rect, U(12));
            using var dash = new Pen(Color.FromArgb(150, blue), Math.Max(1f, DpiScale)) { DashStyle = DashStyle.Dash };
            g.DrawPath(dash, path);
        }
        else
        {
            float x1 = ContentLeft + U(Pad), x2 = ContentLeft + U(LayoutWidth) - U(Pad);
            using var pen = new Pen(blue, Math.Max(2f, U(3))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, x1, d.LineY, x2, d.LineY);
            using var dot = new SolidBrush(blue);
            g.FillEllipse(dot, x1 - U(4), d.LineY - U(4), U(8), U(8));
            g.FillEllipse(dot, x2 - U(4), d.LineY - U(4), U(8), U(8));
        }

        // The card: the first icons, and where they will go
        var items = _settings.LaunchItems;
        var shown = Math.Min(3, items.Count);
        var tile = U(26);
        var label = PlacementText(d.Target);
        var labelSize = Measure(g, label, _f.Small);
        var card = new RectangleF(d.Mouse.X + U(14), d.Mouse.Y + U(10), U(12) + shown * (tile + U(4)) + labelSize.Width + U(6), tile + U(12));
        card.X = Math.Clamp(card.X, U(4), Math.Max(U(4), width - card.Width - U(4)));
        card.Y = Math.Clamp(card.Y, U(4), Math.Max(U(4), height - card.Height - U(4)));
        for (var i = 3; i >= 1; i--)
            FillRound(g, RectangleF.Inflate(card, U(i * 1.5f), U(i * 1.5f)) with { Y = card.Y - U(i * 1.5f) + U(3) }, Color.FromArgb(_p.IsDark ? 30 : 16, Color.Black), U(10 + i));
        FillRound(g, card, PillColor, U(10));
        using (var path = WidgetIcon.RoundedRect(card, U(10)))
        using (var border = new Pen(Color.FromArgb(160, blue), Math.Max(1f, U(1.2f))))
            g.DrawPath(border, path);
        var ix = card.X + U(6);
        for (var i = 0; i < shown; i++)
        {
            var icon = new RectangleF(ix, card.Y + U(6), tile, tile);
            if (LaunchIcons.Get(items[i]) is { } image) g.DrawImage(image, icon);
            else LaunchIcons.DrawLetter(g, items[i].DisplayName, icon, _f.SmallBold);
            ix += tile + U(4);
        }
        DrawText(g, label, _f.Small, _p.TextPrimary, ix + U(2), card.Y + (card.Height - labelSize.Height) / 2);
    }
}

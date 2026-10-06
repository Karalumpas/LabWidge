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

/// <summary>The rail on the left of the widget with the user's shortcuts. Scrolls with the wheel when they do not fit.</summary>
internal sealed partial class DashboardForm
{
    private const float RailLogicalWidth = 58;
    private const float RailTile = 44;
    private const float RailGap = 6;

    private float _railScroll;

    /// <summary>How wide the rail is right now – 0 when it is off, empty or the widget is compact.</summary>
    private float RailWidth => _settings.ShowLaunchRail && _settings.LaunchItems.Count > 0 && !_settings.CompactMode ? U(RailLogicalWidth) : 0;

    private void OnLaunchIconsUpdated() => OnDataUpdated();

    private void DrawRail(Graphics g, float height, float top)
    {
        var rail = RailWidth;
        if (rail <= 0) return;
        var items = _settings.LaunchItems;
        var area = new RectangleF(0, 0, rail, height);
        using (var line = new Pen(_p.Line, Math.Max(1, DpiScale)))
            g.DrawLine(line, rail - U(0.5f), top - U(2), rail - U(0.5f), height - top + U(2));

        var tile = U(RailTile);
        var step = tile + U(RailGap);
        var view = height - top * 2;
        var content = items.Count * step - U(RailGap);
        var max = Math.Max(0, content - view);
        _railScroll = Math.Clamp(_railScroll, 0, max);
        // The wheel scrolls the rail when the icons do not fit; otherwise the rail just moves the widget like the background
        _hits.Add(new Hit(area, null, null, max > 0 ? n => { _railScroll = Math.Clamp(_railScroll - n * step, 0, max); Invalidate(); } : null));

        var state = g.Save();
        g.SetClip(new RectangleF(0, top - U(4), rail, view + U(8)), CombineMode.Intersect);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var x = (rail - tile) / 2;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var rect = new RectangleF(x, top + i * step - _railScroll, tile, tile);
            if (rect.Bottom < top - U(4) || rect.Top > top + view + U(4)) continue;
            var hovered = rect.Contains(_mouse) && _drag == null;
            if (hovered) FillRound(g, rect, _p.HoverBg, U(9));

            var size = U(30);
            var icon = new RectangleF(rect.X + (tile - size) / 2, rect.Y + (tile - size) / 2, size, size);
            if (LaunchIcons.Get(item) is { } image)
            {
                // Keep the aspect ratio – some sites only offer a wide logo
                var scale = Math.Min(size / image.Width, size / image.Height);
                var w = image.Width * scale; var h = image.Height * scale;
                g.DrawImage(image, icon.X + (size - w) / 2, icon.Y + (size - h) / 2, w, h);
            }
            else
            {
                LaunchIcons.DrawLetter(g, item.DisplayName, icon, _f.BodyBold);
            }

            var visible = RectangleF.Intersect(rect, new RectangleF(0, top - U(4), rail, view + U(8)));
            var index = i;
            _hits.Add(new Hit(visible, item.DisplayName + "\n" + (LaunchItem.WebUri(item.Target)?.Host ?? item.Target), () => LaunchRailItem(index),
                max > 0 ? n => { _railScroll = Math.Clamp(_railScroll - n * step, 0, max); Invalidate(); } : null));
        }
        g.Restore(state);

        if (max > 0)
        {
            // A soft fade where more icons are hidden
            var fade = U(16);
            if (_railScroll > 0)
            {
                using var brush = new LinearGradientBrush(new RectangleF(0, top - U(4), rail, fade + 1), _p.Bg, Color.FromArgb(0, _p.Bg), LinearGradientMode.Vertical);
                g.FillRectangle(brush, 0, top - U(4), rail - U(1), fade);
            }
            if (_railScroll < max)
            {
                var y = top + view + U(4) - fade;
                using var brush = new LinearGradientBrush(new RectangleF(0, y - 1, rail, fade + 1), Color.FromArgb(0, _p.Bg), _p.Bg, LinearGradientMode.Vertical);
                g.FillRectangle(brush, 0, y, rail - U(1), fade);
            }
        }
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
}

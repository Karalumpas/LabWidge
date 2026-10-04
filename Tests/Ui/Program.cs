using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class UiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string Output = Path.GetFullPath(Path.Combine("dist", "ui-preview"));

    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(Output);
        using var fixture = new Fixture();
        var widget = fixture.Widget;
        var settings = fixture.Settings;
        Call(widget, "FitSize");
        using var first = Frame(widget, "pinned-dark.png");
        var layout = Get<WidgetLayout>(widget, "_layout");
        Check(layout.ScrollMax > 0, "Fixture must contain scrollable middle content.");
        Call(widget, "ScrollTo", 140f);
        using var scrolled = Frame(widget, "pinned-scrolled.png");
        Check(Equal(first, scrolled, 0, (int)layout.MiddleTop), "Top pin moved during scrolling.");
        Check(Equal(first, scrolled, (int)Math.Ceiling(layout.BottomTop), first.Height), "Bottom pin moved during scrolling.");
        Check(!Equal(first, scrolled, (int)Math.Ceiling(layout.MiddleTop), (int)layout.BottomTop), "Middle content did not move.");
        foreach (var hit in Get<IEnumerable>(widget, "_hits"))
        {
            var hitRect = (RectangleF)hit!.GetType().GetProperty("Rect")!.GetValue(hit)!;
            Check(hitRect.Bottom <= layout.MiddleTop || hitRect.Top >= layout.BottomTop
                || (hitRect.Top >= layout.MiddleTop && hitRect.Bottom <= layout.BottomTop), "Hit target crossed a pinned boundary.");
        }
        Console.WriteLine("PASS UI: pinned pixels stay fixed; middle pixels and hit targets scroll within their viewport");

        Call(widget, "ToggleHeader", "price", (Action)(() => { }));
        using var expanded = Frame(widget, "pinned-price-expanded.png");
        Check(Get<WidgetLayout>(widget, "_layout").TopHeight > layout.TopHeight, "Pinned summary did not expand.");
        Call(widget, "ToggleHeader", "price", (Action)(() => { }));

        settings.WidgetWidth = 500; settings.WidgetHeight = 420; settings.Theme = WidgetTheme.Light;
        widget.ApplySettings(settings); Call(widget, "FitSize");
        using var wide = Frame(widget, "pinned-wide-light.png");
        Check(wide.Width > first.Width && wide.Height < first.Height, "Saved dimensions were ignored.");
        Console.WriteLine("PASS UI: pinned summary expands and saved width/height change the viewport");

        var corner = widget.PointToScreen(new Point(widget.ClientSize.Width - 1, widget.ClientSize.Height - 1));
        var hitMessage = Message.Create(widget.Handle, 0x0084, IntPtr.Zero,
            (IntPtr)((long)(ushort)corner.X | ((long)(ushort)corner.Y << 16)));
        var hitArgs = new object[] { hitMessage };
        widget.GetType().GetMethod("WndProc", Private)!.Invoke(widget, hitArgs);
        Check(((Message)hitArgs[0]).Result == (IntPtr)17, "Bottom-right edge did not request resizing.");
        var rect = new NativeRect { Left = 10, Top = 10, Right = 260, Bottom = 130 };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
        try
        {
            Marshal.StructureToPtr(rect, memory, false);
            var sizing = new object[] { Message.Create(widget.Handle, 0x0214, (IntPtr)8, memory) };
            widget.GetType().GetMethod("WndProc", Private)!.Invoke(widget, sizing);
            rect = Marshal.PtrToStructure<NativeRect>(memory);
            Check(rect.Right - rect.Left >= 300 && rect.Bottom - rect.Top >= 180, "Native resizing ignored minimum dimensions.");
            widget.ClientSize = new Size(500, 420);
            Check(settings.WidgetWidth == 500 && settings.WidgetHeight == 420, "Resizing did not persist dimensions.");
            var finished = new object[] { Message.Create(widget.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero) };
            widget.GetType().GetMethod("WndProc", Private)!.Invoke(widget, finished);
        }
        finally { Marshal.FreeHGlobal(memory); }
        Console.WriteLine("PASS UI: native resize hit zones, size constraints and remembered dimensions");

        settings.WidgetWidth = 300; settings.WidgetHeight = 180;
        settings.SectionPins["system"] = SectionPin.Top;
        settings.SectionPins["network"] = SectionPin.Bottom;
        settings.SectionPinSummaries = Array.Empty<string>();
        widget.ApplySettings(settings); Call(widget, "FitSize");
        using var dense = Frame(widget, "pinned-small-light.png");
        Check(Get<WidgetLayout>(widget, "_layout").MiddleHeight >= 79, "Pinned content consumed the middle viewport.");
        Check(Get<HashSet<string>>(widget, "_autoSummaries").Count > 0, "Oversized pins were not compacted.");
        var bounds = Get<IEnumerable>(widget, "_sectionBounds").Cast<object>().ToList();
        var price = bounds.First(b => (string)b.GetType().GetProperty("Key")!.GetValue(b)! == "price");
        var top = (float)price.GetType().GetProperty("Top")!.GetValue(price)!;
        Call(widget, "StartSectionDrag", "price", new Point(16, (int)top + 6));
        Call(widget, "MoveSectionDrag", 1000);
        using var dragging = Frame(widget, "pinned-dragging-light.png");
        var denseLayout = Get<WidgetLayout>(widget, "_layout");
        Check(Equal(dense, dragging, (int)Math.Ceiling(denseLayout.BottomTop), dense.Height),
            "Dragging a top pin painted over the bottom pins.");
        Call(widget, "FinishSectionDrag");
        Check(Array.IndexOf(settings.SectionOrder!, "system") < Array.IndexOf(settings.SectionOrder!, "price"), "Pinned sections could not be reordered.");
        Check(settings.SectionPins["audio"] == SectionPin.Bottom, "Reorder changed another section's pin.");
        Console.WriteLine("PASS UI: crowded pins compact safely and can be reordered within their fixed area");

        settings.SectionPins.Clear(); settings.WidgetWidth = null; settings.WidgetHeight = null;
        widget.ApplySettings(settings); Call(widget, "FitSize");
        using var automatic = Frame(widget, "automatic-light.png");
        Check(Get<WidgetLayout>(widget, "_layout").TopHeight == 0, "Released pins remained fixed.");
        Console.WriteLine("PASS UI: automatic size and unpinned scrolling remain available");

        // A tall expanded section must pass a short one in both directions – also at the ends of the list
        settings.SectionOrder = new[] { "ha", "cloudflare", "system", "proxmox", "network", "audio", "price" };
        settings.CollapsedSystem = settings.CollapsedPrice = false;
        widget.ApplySettings(settings); Call(widget, "FitSize");
        DragSection(widget, "system", -100_000);
        Check(settings.SectionOrder![0] == "system", "A tall section could not be dragged above shorter ones.");
        DragSection(widget, "system", 100_000);
        Check(settings.SectionOrder![^1] == "system", "A tall section could not be dragged below shorter ones.");
        Console.WriteLine("PASS UI: tall sections can be dragged past short ones to both ends");
        ContextMenuStrip? previousMenu = null;
        foreach (var index in new[] { 1, 0, 4, 2, 1, 2 })
        {
            Call(widget, "ShowSectionMenu", "price", new Point(20, 20));
            var menu = Get<ContextMenuStrip>(widget, "_sectionMenu");
            Check(previousMenu == null || previousMenu.IsDisposed, "Previous section menu was leaked on reopening.");
            var item = menu.Items[index];
            Check(item.Enabled, "Menu action should be available.");
            item.Select();
            var point = new Point(item.Bounds.Left + 5, item.Bounds.Top + item.Bounds.Height / 2);
            var mouse = new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0);
            // Exercise ToolStrip's complete mouse-click/auto-close path, not just PerformClick.
            typeof(ToolStrip).GetMethod("OnMouseDown", Private)!.Invoke(menu, new object[] { mouse });
            typeof(ToolStrip).GetMethod("OnMouseUp", Private)!.Invoke(menu, new object[] { mouse });
            Check(!menu.Visible && !menu.IsDisposed, "Closed dropdown was disposed before WinForms finished its click.");
            if (index == 0) Check(settings.SectionPins["price"] == SectionPin.Top, "Top menu action failed.");
            if (index == 1) Check(settings.SectionPins["price"] == SectionPin.Bottom, "Bottom menu action failed.");
            if (index == 2) Check(!settings.SectionPins.ContainsKey("price"), "Unpin menu action failed.");
            if (index == 4) Check(settings.SectionPinSummaries.Contains("price"), "Summary menu action failed.");
            previousMenu = menu;
        }
        widget.Dispose();
        Check(previousMenu!.IsDisposed, "Widget disposal leaked its section menu.");
        Console.WriteLine("PASS UI: real section-menu mouse clicks pin, unpin and select summaries without premature disposal");
        Console.WriteLine("10 UI integration checks passed; previews: " + Output);
        Showcase();
    }

    /// <summary>
    /// Renders the screenshots for the README into dist/ui-preview/showcase – with made-up data,
    /// so they never show a real IP address, device or home. Copy them to docs/images when the look changes.
    /// </summary>
    private static void Showcase()
    {
        using var fixture = new Fixture();
        var s = fixture.Settings;
        var widget = fixture.Widget;
        s.SectionPins.Clear();
        s.SectionPinSummaries = Array.Empty<string>();
        s.SectionOrder = new[] { "price", "ha", "system", "audio", "proxmox", "network", "cloudflare" };
        s.CollapsedNetwork = s.CollapsedCloudflare = s.CollapsedProxmox = true;
        s.WidgetWidth = 344;
        s.WidgetHeight = null;

        // A PC that is doing something, and a Danish total price with grid tariff and taxes
        s.PriceShowTotal = true;
        s.NetTariffOwner = "Example Net";
        s.NetTariffCodes = new[] { "C" };
        var price = Get<ElectricityPriceService>(widget, "_el");
        var net = Enumerable.Range(0, 24).Select(h => h is >= 17 and < 21 ? 110.0 : h is >= 6 and < 17 or >= 21 ? 45.0 : 20.0).ToArray();
        Set(price, "NetTariffs", new List<TariffRow> { new("C", "Nettarif C", DateTime.Today.AddYears(-1), null, net) });
        Set(price, "StateCharges", new List<TariffRow> { new("40000", "Transmission", DateTime.Today.AddYears(-1), null, Enumerable.Repeat(15.0, 24).ToArray()) });
        var sys = Get<SystemMonitor>(widget, "_sys");
        Set(sys, "CpuPercent", 23.0);
        for (var i = 0; i < 60; i++) sys.CpuHistory.Add(18 + 14 * Math.Abs(Math.Sin(i / 5.0)) + (i % 7 == 0 ? 25 : 0));
        Set(sys, "RamUsed", 13_400UL << 20);
        Set(sys, "RamTotal", 32UL << 30);
        Set(sys, "Disks", new List<DiskInfo> { new("C:", "System", 412L << 30, 931L << 30), new("D:", "Data", 1_210L << 30, 3_725L << 30) });
        Set(sys.Gpu, "Available", true);
        Set(sys.Gpu, "Name", "GeForce RTX 4070");
        Set(sys.Gpu, "Percent", 38.0);
        Set(sys.Gpu, "VramUsed", 4_300UL << 20);
        Set(sys.Gpu, "VramTotal", 12UL << 30);
        Set(sys.Gpu, "TempC", (int?)54);
        Set(sys.Gpu, "PowerW", (double?)96);
        Set(sys.Gpu, "PowerLimitW", (double?)200);
        Set(sys.Gpu, "FanPercent", (int?)32);
        var net2 = Get<NetworkMonitor>(widget, "_net");
        Set(net2, "PingMs", (long?)12);
        Set(net2, "DownBps", 4_200_000.0);
        Set(net2, "UpBps", 650_000.0);
        for (var i = 0; i < 120; i++) net2.PingHistory.Add(i == 70 ? -1 : 10 + 4 * Math.Abs(Math.Sin(i / 6.0)));
        for (var i = 0; i < 300; i++)
        {
            net2.DownHistory.Add(3_000_000 + 2_500_000 * Math.Abs(Math.Sin(i / 17.0)));
            net2.UpHistory.Add(400_000 + 300_000 * Math.Abs(Math.Cos(i / 11.0)));
            sys.RamHistory.Add(40 + 3 * Math.Sin(i / 40.0));
            sys.Gpu.History.Add(30 + 25 * Math.Abs(Math.Sin(i / 23.0)));
        }
        Set(net2, "Adapters", new List<AdapterInfo>
        {
            new("Ethernet", "Ethernet", "192.168.1.20", 24, "192.168.1.1", new[] { "192.168.1.1", "1.1.1.1" }, 2_500_000_000, true),
            new("vEthernet (WSL)", NetworkMonitor.VirtualKind, "172.24.48.1", 20, null, Array.Empty<string>(), 10_000_000_000, false)
        });
        Set(fixture.Audio, "Microphones", new List<AudioDevice> { new("mic", "Microphone", "Webcam Microphone", AudioKind.Other, null) });
        Set(fixture.Audio, "DefaultMicId", "mic");
        Set(fixture.Audio, "Volumes", new Dictionary<string, VolumeState>
        {
            ["speaker"] = new(.45f, false), ["headset"] = new(.7f, false), ["display"] = new(.3f, true), ["mic"] = new(.84f, false)
        });

        var shots = new List<Bitmap>();
        foreach (var theme in new[] { WidgetTheme.Dark, WidgetTheme.Light })
        {
            s.Theme = theme;
            widget.ApplySettings(s);
            Call(widget, "FitSize");
            shots.Add(Frame(widget, $"showcase/widget-{theme.ToString().ToLowerInvariant()}.png"));
        }

        s.Theme = WidgetTheme.Dark;
        widget.ApplySettings(s);
        widget.SetCompact(true);
        Call(widget, "FitSize");
        Frame(widget, "showcase/compact-dark.png").Dispose();
        widget.SetCompact(false);

        // The hero: both themes side by side on a soft background
        const int pad = 48, gap = 36;
        var height = shots.Max(b => b.Height);
        using var hero = new Bitmap(pad * 2 + shots.Sum(b => b.Width) + gap, pad * 2 + height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(hero))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, hero.Width, hero.Height),
                Color.FromArgb(31, 58, 104), Color.FromArgb(14, 20, 32), 60f);
            g.FillRectangle(bg, 0, 0, hero.Width, hero.Height);
            var x = pad;
            foreach (var shot in shots)
            {
                var rect = new Rectangle(x, pad + (height - shot.Height) / 2, shot.Width, shot.Height);
                for (var i = 1; i <= 12; i++)
                {
                    using var shadow = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
                    using var path = Rounded(new Rectangle(rect.X - i + 4, rect.Y - i + 10, rect.Width + i * 2 - 8, rect.Height + i * 2 - 8), 10 + i);
                    g.FillPath(shadow, path);
                }
                using (var clip = Rounded(rect, 8))
                {
                    g.SetClip(clip);
                    g.DrawImage(shot, rect);
                    g.ResetClip();
                }
                x += shot.Width + gap;
            }
        }
        hero.Save(Path.Combine(Output, "showcase", "hero.png"), ImageFormat.Png);
        foreach (var shot in shots) shot.Dispose();
        Console.WriteLine("Showcase screenshots: " + Path.Combine(Output, "showcase"));

        SectionWindowChecks(widget, s);
    }

    /// <summary>
    /// Opens every section window, renders it, and checks pinning, restoring at start and dragging a section out of the widget.
    /// </summary>
    private static void SectionWindowChecks(DashboardForm widget, AppSettings s)
    {
        s.Theme = WidgetTheme.Dark;
        widget.ApplySettings(s);
        foreach (var key in SectionWindows.Keys)
        {
            SectionWindows.Toggle(key, widget.Bounds);
            Application.DoEvents();
            Check(SectionWindows.IsOpen(key), $"The {key} window did not open.");
            var window = OpenWindow(key);
            Check(window.Width >= 260 && window.Height >= 140, $"The {key} window has no size.");
            using (var bitmap = new Bitmap(window.Width, window.Height))
            {
                window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
                bitmap.Save(Path.Combine(Output, "showcase", $"window-{key}.png"), ImageFormat.Png);
            }
            SectionWindows.Close(key);
            Application.DoEvents();
            Check(!SectionWindows.IsOpen(key), $"The {key} window did not close.");
        }
        Console.WriteLine("PASS UI: every section window opens, draws and closes");

        // Pinned windows stay, are remembered and come back at the next start
        SectionWindows.Toggle("price", widget.Bounds);
        Application.DoEvents();
        OpenWindow("price").GetType().BaseType!.GetMethod("TogglePin", Private)!.Invoke(OpenWindow("price"), null);
        Check(s.SectionWindows["price"].Pinned && s.SectionWindows["price"].Open, "Pinning did not remember the window.");
        SectionWindows.CloseAllForShutdown();
        Application.DoEvents();
        Check(s.SectionWindows["price"].Open, "A pinned window forgot that it was open at exit.");
        SectionWindows.RestorePinned(s);
        Application.DoEvents();
        Check(SectionWindows.IsOpen("price"), "A pinned window was not restored at start.");
        SectionWindows.Close("price");
        Application.DoEvents();
        Check(!s.SectionWindows["price"].Open, "Closing a pinned window did not forget it.");
        Console.WriteLine("PASS UI: pinned windows are remembered, restored and forgotten when closed");

        // A section dragged out of the widget becomes a pinned window; the order is unchanged
        s.SectionWindows.Clear();
        Call(widget, "FitSize");
        var orderBefore = s.SectionOrder!.ToArray();
        var bounds = Get<IEnumerable>(widget, "_sectionBounds").Cast<object>()
            .First(b => (string)b.GetType().GetProperty("Key")!.GetValue(b)! == "system");
        var top = (float)bounds.GetType().GetProperty("Top")!.GetValue(bounds)!;
        Call(widget, "StartSectionDrag", "system", new Point(16, (int)top + 6));
        Cursor.Position = new Point(widget.Left > 500 ? widget.Left - 300 : widget.Right + 300, widget.Top + 100);
        Check((bool)Call(widget, "UpdateTearOff")!, "Dragging a section out of the widget was not noticed.");
        Call(widget, "DropSectionDrag");
        Application.DoEvents();
        Check(SectionWindows.IsOpen("system") && s.SectionWindows["system"].Pinned, "A section dropped outside did not open a pinned window.");
        Check(s.SectionOrder!.SequenceEqual(orderBefore), "Dragging a section out changed the order.");
        SectionWindows.CloseAllForShutdown();
        Console.WriteLine("PASS UI: a section dragged out of the widget opens as a pinned window");
    }

    private static PopupPanel OpenWindow(string key)
    {
        var open = (IDictionary)typeof(SectionWindows).GetField("Open", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return (PopupPanel)open[key]!;
    }

    private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Lifts a section by its header, moves the mouse to <paramref name="mouseY"/> (clamped by the widget) and drops it.</summary>
    private static void DragSection(DashboardForm widget, string key, int mouseY)
    {
        var bounds = Get<IEnumerable>(widget, "_sectionBounds").Cast<object>()
            .First(b => (string)b.GetType().GetProperty("Key")!.GetValue(b)! == key);
        var top = (float)bounds.GetType().GetProperty("Top")!.GetValue(bounds)!;
        Call(widget, "StartSectionDrag", key, new Point(16, (int)top + 6));
        Call(widget, "MoveSectionDrag", mouseY);
        Call(widget, "FinishSectionDrag");
    }

    private static object? Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Private)!.GetValue(target)!;
    private static void Set(object target, string property, object value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    private static Bitmap Frame(DashboardForm widget, string name)
    {
        // GDI TextRenderer writes RGB pixels; an opaque surface matches the window DC
        // and avoids undefined alpha values in PNG previews.
        var bitmap = new Bitmap(widget.ClientSize.Width, widget.ClientSize.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        // ClearType targets a display DC; grayscale smoothing is stable on a memory DC.
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(widget.BackColor);
        Call(widget, "Render", graphics);
        var file = Path.Combine(Output, name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        bitmap.Save(file, ImageFormat.Png);
        return bitmap;
    }

    private static bool Equal(Bitmap first, Bitmap second, int top, int bottom)
    {
        for (var y = top; y < Math.Min(bottom, first.Height); y++)
            for (var x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
        return true;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient Http = new(new OfflineHandler());
        private readonly ContextMenuStrip Menu = new();
        public AppSettings Settings { get; } = new()
        {
            Theme = WidgetTheme.Dark, WidgetWidth = 344, WidgetHeight = 600, Country = "DK",
            SectionPins = new() { ["price"] = SectionPin.Top, ["audio"] = SectionPin.Bottom },
            SectionPinSummaries = new[] { "price" }, HomeAssistantEnabled = true,
            HomeAssistantUrl = "http://example.test", HomeAssistantEntities = new[] { "light.room" },
            CloudflareEnabled = true, ZoneId = "test", ProxmoxEnabled = true,
            ProxmoxUrl = "https://example.test", ProxmoxTokenId = "test@pve!widget",
            AudioDeviceIds = new[] { "speaker", "headset", "display" }, PriceShowTotal = false
        };
        public DashboardForm Widget { get; }
        public AudioService Audio { get; }

        public Fixture()
        {
            var price = new ElectricityPriceService(Http);
            Set(price, "Prices", Enumerable.Range(0, 192).Select(i => new PricePoint(DateTime.Today.AddMinutes(i * 15), 65 + 35 * Math.Sin(i / 8.0))).ToList());
            Set(price, "LastFetch", DateTime.Now);
            Set(price, "LastSuccessfulFetch", DateTime.Now);
            var audio = Audio = new AudioService();
            Set(audio, "Devices", new List<AudioDevice>
            {
                new("speaker", "Speakers", "Speakers", AudioKind.Speakers, null),
                new("headset", "Headset", "Headset", AudioKind.Headset, Guid.NewGuid()),
                new("display", "Monitor", "Monitor", AudioKind.Display, null)
            });
            Set(audio, "DefaultId", "speaker");
            Set(audio, "Volumes", new Dictionary<string, VolumeState> { ["speaker"] = new(.45f, false) });
            var ha = new HomeAssistantService(Http);
            Set(ha, "Entities", new List<HaEntity> { new("light.room", "Living room", "on", 180, null) });
            Set(ha, "LastFetch", DateTime.Now);
            var cf = new CloudflareService(Http);
            Set(cf, "LastFetch", DateTime.Now);
            Set(cf, "LastSuccessfulDnsFetch", DateTime.Now);
            Set(cf, "ARecords", new List<CloudflareRecord> { new() { Name = "home.example.test", Content = "203.0.113.10" } });
            Set(cf, "Tunnels", new List<CfTunnel> { new("tunnel", "Home network", "healthy", 4, Array.Empty<string>(), null, null, null,
                new List<CfRoute> { new("home.example.test", "http://localhost:8123") }) });
            var pve = new ProxmoxService();
            Set(pve, "LastFetch", DateTime.Now);
            Set(pve, "Nodes", new List<PveNode> { new("Server", true, .24, 8, 8L << 30, 32L << 30, 0, 0, TimeSpan.FromDays(3)) });
            Set(pve, "Guests", new List<PveGuest> { new(100, "Home Assistant", "qemu", "Server", "running", .1, 2, 2L << 30, 4L << 30, TimeSpan.FromDays(3)) });
            var network = new NetworkMonitor();
            Widget = new DashboardForm(price, new SystemMonitor(), network, audio, ha, cf, new ServiceMonitor(), pve,
                Settings, () => { }, () => "203.0.113.10", () => "ajour", () => Task.CompletedTask, Menu,
                () => (DateTime.Now, null));
        }
        public void Dispose() { Widget.Dispose(); Menu.Dispose(); Http.Dispose(); }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
}

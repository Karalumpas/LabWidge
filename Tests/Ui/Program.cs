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
        Console.WriteLine("6 UI integration checks passed; previews: " + Output);
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
        bitmap.Save(Path.Combine(Output, name), ImageFormat.Png);
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

        public Fixture()
        {
            var price = new ElectricityPriceService(Http);
            Set(price, "Prices", Enumerable.Range(0, 192).Select(i => new PricePoint(DateTime.Today.AddMinutes(i * 15), 65 + 35 * Math.Sin(i / 8.0))).ToList());
            Set(price, "LastFetch", DateTime.Now);
            Set(price, "LastSuccessfulFetch", DateTime.Now);
            var audio = new AudioService();
            Set(audio, "Devices", new List<AudioDevice>
            {
                new("speaker", "Speakers", "Speakers", AudioKind.Speakers, null),
                new("headset", "Headset", "Headset", AudioKind.Headset, Guid.NewGuid()),
                new("display", "Monitor", "Monitor", AudioKind.Display, null)
            });
            Set(audio, "DefaultId", "speaker");
            Set(audio, "Volumes", new Dictionary<string, VolumeState> { ["speaker"] = new(.45f, false) });
            var ha = new HomeAssistantService(Http);
            Set(ha, "Entities", new List<HaEntity> { new("light.room", "Stue", "on", 180, null) });
            Set(ha, "LastFetch", DateTime.Now);
            var cf = new CloudflareService(Http);
            Set(cf, "LastFetch", DateTime.Now);
            Set(cf, "LastSuccessfulDnsFetch", DateTime.Now);
            Set(cf, "ARecords", new List<CloudflareRecord> { new() { Name = "home.example.test", Content = "203.0.113.10" } });
            Set(cf, "Tunnels", new List<CfTunnel> { new("tunnel", "Hjemmenet", "healthy", 4, Array.Empty<string>(), null, null, null,
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

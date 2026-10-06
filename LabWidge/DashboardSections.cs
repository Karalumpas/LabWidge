using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// Section order and drag and drop: grab a header, drag, and the section lifts like a card,
/// the others slide aside, and on release it clicks into place with a small springy motion.
/// The widget itself also snaps magnetically to the screen edges when it is moved.
/// </summary>
internal sealed partial class DashboardForm
{
    /// <summary>The default order – and the order new sections are inserted in.</summary>
    public static readonly string[] DefaultSectionOrder = WidgetPlugins.All.Select(p => p.Key).Append(ShortcutsKey).ToArray();

    private const float SectionGap = 22;      // space between sections; the line sits 10 px down
    private const float EdgeSnap = 20;        // how close to the edge the widget snaps in
    private const float EdgeMargin = 12;      // distance to the edge while snapped

    private sealed record Section(string Key, Func<Graphics, float, float, float, float> Draw);
    private sealed record SectionBounds(string Key, float Top, float Bottom)
    {
        public float Height => Bottom - Top;
    }

    private readonly List<SectionBounds> _sectionBounds = new();
    private readonly System.Windows.Forms.Timer _dragTimer = new() { Interval = 15 };
    private SectionDrag? _drag;
    private string? _downSection;

    private sealed class SectionDrag
    {
        public required string Key;
        public required Bitmap Snapshot;          // the section as it looked when it was lifted
        public required float SnapshotTop;        // where in the image the section starts
        public required float Grab;               // distance from the section's top to the mouse
        public required float Start;              // y of the first section
        public required Dictionary<string, float> Heights;
        public required List<string> Order;       // the current order, with the dragged section in its new place
        public required List<string> Original;    // the order when the card was lifted
        public bool TornOff;                      // dragged out of the widget – becomes a window when dropped
        public readonly Dictionary<string, float> Anim = new();
        public float MouseY;
        public bool Settling;
        public float SettleFrom;
        public DateTime SettleStart;
    }

    private List<Section> VisibleSections()
    {
        var sections = new List<Section>();
        foreach (var key in SectionOrder())
        {
            if (key == ShortcutsKey)
            {
                if (ShowsShortcuts && _settings.LaunchRailPlacement == RailPlacement.Section) sections.Add(new Section(key, DrawShortcutsSection));
            }
            else if (WidgetPlugins.Find(key) is { } p && _settings.IsPluginEnabled(p.Key) && p.IsVisible(this, _settings))
            {
                sections.Add(new Section(p.Key, (g, x, y, w) => p.RenderExpanded(this, g, x, y, w)));
            }
        }
        return sections;
    }

    /// <summary>The saved order, plus sections added since.</summary>
    private IEnumerable<string> SectionOrder()
    {
        var saved = (_settings.SectionOrder ?? Array.Empty<string>()).Where(DefaultSectionOrder.Contains).Distinct().ToList();
        foreach (var key in DefaultSectionOrder.Where(k => !saved.Contains(k)))
        {
            // A new section is inserted after the section it follows in the default order
            var before = Array.IndexOf(DefaultSectionOrder, key) - 1;
            var anchor = before >= 0 ? saved.IndexOf(DefaultSectionOrder[before]) : -1;
            saved.Insert(anchor + 1, key);
        }
        return saved;
    }

    private float Separator(Graphics g, float x, float y, float w)
    {
        using var pen = new Pen(_p.Line, Math.Max(1f, DpiScale));
        g.DrawLine(pen, x, y + U(10), x + w, y + U(10));
        return y + U(SectionGap);
    }

    /// <summary>The section whose header is under the point.</summary>
    private string? SectionAtHeader(Point p) => _settings.CompactMode || p.X < ContentLeft || p.X >= ContentLeft + U(LayoutWidth) ? null
        : _sectionBounds.FirstOrDefault(b => p.Y >= b.Top - U(4) && p.Y <= b.Top + U(20)
            && (PinOf(b.Key) != SectionPin.None || (p.Y >= _layout.MiddleTop && p.Y < _layout.BottomTop)))?.Key;

    private string? SectionHeaderAt(Point p) => p.X >= ContentLeft + U(Pad) - U(6) && p.X <= ContentLeft + U(Pad) + U(6)
        ? SectionAtHeader(p) : null;

    // ---------- Drag ----------

    private void StartSectionDrag(string key, Point mouse)
    {
        var bounds = _sectionBounds.First(b => b.Key == key);

        // A snapshot of the section becomes the card that is dragged around
        var pad = U(8);
        var snapshot = new Bitmap(Math.Max(1, ClientSize.Width - (int)RailWidth), (int)Math.Ceiling(bounds.Height + pad * 2));
        using (var g = Graphics.FromImage(snapshot))
        {
            g.Clear(_p.Bg);
            var saved = _mouse;
            _mouse = new Point(-1, -1);    // no hover highlight on the card
            DrawSection(g, VisibleSections().First(s => s.Key == key), U(Pad), pad, U(LayoutWidth) - U(Pad) * 2);
            _mouse = saved;
        }
        var offset = PinOf(key) == SectionPin.None ? _scroll : 0;
        var group = _sectionBounds.Where(b => PinOf(b.Key) == PinOf(key)).ToList();

        _drag = new SectionDrag
        {
            Key = key,
            Snapshot = snapshot,
            SnapshotTop = pad,
            Grab = mouse.Y - bounds.Top,
            Start = group[0].Top + offset,
            Heights = group.ToDictionary(b => b.Key, b => b.Height),
            Order = group.Select(b => b.Key).ToList(),
            Original = group.Select(b => b.Key).ToList(),
            MouseY = mouse.Y + offset
        };
        foreach (var b in group) _drag.Anim[b.Key] = b.Top + offset;

        _tip.HideTip();
        _hoverKey = null;
        Capture = true;
        Cursor = Cursors.SizeNS;
        _dragTimer.Start();
        Invalidate();
    }

    private DragGhost? _ghost;

    /// <summary>The window for a section, or null when the section is not set up (e.g. no Home Assistant).</summary>
    private PopupPanel? CreateWindow(string key)
    {
        if (!_settings.IsPluginEnabled(key)) return null;
        return WidgetPlugins.Find(key)?.CreateWindow(this, _settings, _saveSettings);
    }

    /// <summary>
    /// While a card is dragged: outside the widget (more than a short distance) it becomes a see-through card that follows
    /// the mouse, and dropping it opens the section in a window. Returns true while the card is outside.
    /// </summary>
    private bool UpdateTearOff()
    {
        var d = _drag!;
        if (d.Settling || !SectionWindows.Supports(d.Key)) return false;
        var area = Bounds;
        area.Inflate((int)U(36), (int)U(36));
        var cursor = System.Windows.Forms.Cursor.Position;
        var outside = !area.Contains(cursor);
        if (outside != d.TornOff)
        {
            d.TornOff = outside;
            if (outside)
            {
                _ghost ??= new DragGhost();
                _ghost.Show(d.Snapshot);
            }
            else
            {
                _ghost?.Hide();
            }
            Cursor = outside ? Cursors.Hand : Cursors.SizeNS;
            Invalidate();
        }
        if (outside) _ghost!.Location = new Point(cursor.X - (int)U(40), cursor.Y - (int)(d.Grab + d.SnapshotTop));
        return outside;
    }

    /// <summary>The see-through card that follows the mouse while a section is dragged out of the widget.</summary>
    private sealed class DragGhost : Form
    {
        public DragGhost()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Opacity = 0.82;
            BackgroundImageLayout = ImageLayout.None;
        }

        public void Show(Bitmap snapshot)
        {
            BackgroundImage = snapshot;
            ClientSize = snapshot.Size;
            if (!Visible) Show();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x20; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT
                return cp;
            }
        }
    }

    private void MoveSectionDrag(int mouseY)
    {
        var d = _drag!;
        if (d.Settling) return;

        var total = Targets(d).Values.Max() + d.Heights[d.Order[^1]];
        d.MouseY = Math.Clamp(mouseY, d.Start + d.Grab - U(12), total - d.Heights[d.Key] + d.Grab + U(12));

        // The card's leading edge decides: a section above the card moves below it when the card's top passes its
        // middle, and a section below moves above it when the card's bottom passes its middle. Comparing middles
        // instead would never let a tall card pass a short section at the end of the list, because the card cannot
        // be dragged beyond the ends of its area.
        var top = d.MouseY - d.Grab;
        var bottom = top + d.Heights[d.Key];
        var targets = Targets(d);
        var before = d.Order.TakeWhile(k => k != d.Key).ToHashSet();
        var others = d.Order.Where(k => k != d.Key).ToList();
        var index = others.Count(k => targets[k] + d.Heights[k] / 2 < (before.Contains(k) ? top : bottom));
        others.Insert(index, d.Key);
        if (!others.SequenceEqual(d.Order))
        {
            d.Order.Clear();
            d.Order.AddRange(others);
        }
    }

    private void DropSectionDrag()
    {
        var d = _drag;
        if (d == null || d.Settling) return;
        if (d.TornOff)
        {
            // Dropped on the desktop: the order stays as it was, and the section opens in a pinned window there.
            // Settling first: releasing the mouse capture below calls DropSectionDrag again.
            d.Settling = true;
            _ghost?.Hide();
            d.Order.Clear();
            d.Order.AddRange(d.Original);
            Capture = false;
            Cursor = Cursors.Default;
            FinishSectionDrag();
            SectionWindows.OpenPinnedAt(d.Key, System.Windows.Forms.Cursor.Position);
            return;
        }
        d.Settling = true;
        d.SettleFrom = d.MouseY - d.Grab;
        d.SettleStart = DateTime.Now;
        Capture = false;
        Cursor = Cursors.Default;
    }

    /// <summary>Where each section should be with the current order.</summary>
    private Dictionary<string, float> Targets(SectionDrag d)
    {
        var result = new Dictionary<string, float>();
        var y = d.Start;
        foreach (var key in d.Order)
        {
            result[key] = y;
            y += d.Heights[key] + U(SectionGap);
        }
        return result;
    }

    private const double SettleMs = 260;

    private void OnDragTick()
    {
        var d = _drag;
        if (d == null)
        {
            _dragTimer.Stop();
            return;
        }

        // The other sections slide smoothly towards their new place
        var targets = Targets(d);
        foreach (var key in d.Order)
        {
            var current = d.Anim[key];
            d.Anim[key] = Math.Abs(targets[key] - current) < 0.5f ? targets[key] : current + (targets[key] - current) * 0.3f;
        }

        if (d.Settling && (DateTime.Now - d.SettleStart).TotalMilliseconds >= SettleMs)
        {
            FinishSectionDrag();
            return;
        }
        Invalidate();
    }

    private void FinishSectionDrag()
    {
        var d = _drag!;
        _dragTimer.Stop();

        // Hidden sections keep their place; the visible ones follow the new order
        var full = SectionOrder().ToList();
        var visible = new Queue<string>(d.Order);
        var order = full.Select(k => d.Order.Contains(k) ? visible.Dequeue() : k).ToArray();
        var changed = !order.SequenceEqual(full);

        _drag = null;
        d.Snapshot.Dispose();
        if (changed)
        {
            _settings.SectionOrder = order;
            _saveSettings();
            Logger.Info("Widget: section order changed to " + string.Join(", ", d.Order) + ".");
        }
        FitSize();
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_railDrag != null && !Capture) DropRailDrag();
        if (_iconDrag != null && !Capture) DropIconDrag();
        if (_drag != null && !Capture) DropSectionDrag();
    }

    // ---------- Drawing while dragging ----------

    private float DrawDragging(Graphics g, List<Section> sections, float x, float w)
    {
        var d = _drag!;
        var byKey = sections.ToDictionary(s => s.Key);
        _hits.Clear();

        for (var i = 0; i < d.Order.Count; i++)
        {
            var key = d.Order[i];
            var top = d.Anim[key];
            if (i > 0 && key != d.Key && d.Order[i - 1] != d.Key)
            {
                using var pen = new Pen(_p.Line, Math.Max(1f, DpiScale));
                g.DrawLine(pen, x, top - U(12), x + w, top - U(12));
            }

            if (key == d.Key)
            {
                // The slot where the section lands
                var slot = new RectangleF(x - U(6), top - U(6), w + U(12), d.Heights[key] + U(12));
                FillRound(g, slot, Color.FromArgb(_p.IsDark ? 26 : 18, _p.Blue), U(8));
                using var path = WidgetIcon.RoundedRect(slot, U(8));
                using var dash = new Pen(Color.FromArgb(110, _p.Blue), Math.Max(1f, DpiScale)) { DashStyle = DashStyle.Dash };
                g.DrawPath(dash, path);
            }
            else if (byKey.TryGetValue(key, out var section))
            {
                DrawSection(g, section, x, top, w);
            }
        }
        _hits.Clear(); // nothing can be clicked while dragging

        // The card: follows the mouse and lands with a small springy motion on release
        float cardTop, lift;
        if (d.Settling)
        {
            var t = Math.Clamp((DateTime.Now - d.SettleStart).TotalMilliseconds / SettleMs, 0, 1);
            var to = Targets(d)[d.Key];
            cardTop = d.SettleFrom + (to - d.SettleFrom) * (float)EaseOutBack(t);
            lift = 1 - (float)t;
        }
        else
        {
            cardTop = d.MouseY - d.Grab;
            lift = 1;
        }
        DrawCard(g, d, cardTop, lift);

        var last = d.Order[^1];
        return Targets(d)[last] + d.Heights[last];
    }

    private void DrawCard(Graphics g, SectionDrag d, float top, float lift)
    {
        var rect = new RectangleF(ContentLeft, top - d.SnapshotTop, d.Snapshot.Width, d.Snapshot.Height);
        var radius = U(9);

        // Shadow in several layers – softer and further away the higher the card is lifted
        for (var i = 5; i >= 1; i--)
        {
            var spread = U(i * 2.2f) * lift;
            var shadow = new RectangleF(rect.X - spread, rect.Y - spread + U(5) * lift, rect.Width + spread * 2, rect.Height + spread * 2);
            FillRound(g, shadow, Color.FromArgb((int)((_p.IsDark ? 34 : 18) * lift), Color.Black), radius + spread);
        }

        using (var path = WidgetIcon.RoundedRect(rect, radius))
        {
            var state = g.Save();
            g.SetClip(path, System.Drawing.Drawing2D.CombineMode.Intersect);
            g.DrawImage(d.Snapshot, rect.X, rect.Y, rect.Width, rect.Height);
            // A touch lighter, so the card seems to lie above the rest
            using (var sheen = new SolidBrush(Color.FromArgb((int)((_p.IsDark ? 14 : 10) * lift), Color.White)))
                g.FillPath(sheen, path);
            g.Restore(state);

            using var border = new Pen(Color.FromArgb((int)(40 + 120 * lift), _p.Blue), Math.Max(1f, U(1.2f)));
            g.DrawPath(border, path);
        }
    }

    /// <summary>Overshoots the target and comes back – the small "click" when the card lands.</summary>
    private static double EaseOutBack(double t)
    {
        const double c1 = 1.2, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    // ---------- Magnetic edges ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>The mouse's distance to the window corner when the move began.</summary>
    private Point _moveGrab;

    /// <summary>
    /// While the widget is moved, it snaps to the screen edges (and the taskbar) when close.
    /// The position is computed from the mouse rather than the rectangle Windows suggests: Windows adds each
    /// movement on top of the last (snapped) position, so a slow movement would otherwise never break free of the edge.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        const int WmEnterSizeMove = 0x0231;
        const int WmMoving = 0x0216;
        const int WmSizing = 0x0214;
        const int WmExitSizeMove = 0x0232;
        if (m.Msg == 0x007B /* WM_CONTEXTMENU */)
        {
            var point = m.LParam.ToInt64() == -1 ? PointToClient(Cursor.Position)
                : PointToClient(new Point(unchecked((short)m.LParam.ToInt64()), unchecked((short)(m.LParam.ToInt64() >> 16))));
            if (RailColumn().Contains(point))
            {
                ShowRailMenu(point);
                m.Result = IntPtr.Zero;
                return;
            }
            if (SectionAtHeader(point) is { } key)
            {
                ShowSectionMenu(key, point);
                m.Result = IntPtr.Zero;
                return;
            }
        }
        if (m.Msg == 0x0084 /* WM_NCHITTEST */ && !_settings.CompactMode && _drag == null && _railDrag == null && _iconDrag == null)
        {
            var point = PointToClient(new Point(unchecked((short)m.LParam.ToInt64()), unchecked((short)(m.LParam.ToInt64() >> 16))));
            if (ClientRectangle.Contains(point))
            {
                // The edges of the content – the transparent part of the rail column is not part of the widget
                var edge = U(6);
                var contentLeft = ContentLeft; var contentRight = contentLeft + ClientSize.Width - RailWidth;
                var left = point.X >= contentLeft && point.X < contentLeft + edge;
                var right = point.X >= contentRight - edge && point.X < contentRight;
                var top = point.Y < edge; var bottom = point.Y >= ClientSize.Height - edge;
                var hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
                if (hit != 0) { m.Result = (IntPtr)hit; return; }
            }
        }
        if (m.Msg == WmSizing && m.LParam != IntPtr.Zero)
        {
            _resizing = true;
            var r = Marshal.PtrToStructure<RECT>(m.LParam);
            var wa = Screen.FromRectangle(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)).WorkingArea;
            var rail = (int)RailWidth;
            var width = Math.Clamp(r.Right - r.Left, Math.Min((int)U(300) + rail, wa.Width), Math.Min((int)U(640) + rail, wa.Width));
            var height = Math.Clamp(r.Bottom - r.Top, Math.Min((int)MinimumViewHeight, wa.Height), wa.Height);
            var edge = m.WParam.ToInt32();
            if (edge is 1 or 4 or 7) r.Left = r.Right - width; else r.Right = r.Left + width;
            if (edge is 3 or 4 or 5) r.Top = r.Bottom - height; else r.Bottom = r.Top + height;
            Marshal.StructureToPtr(r, m.LParam, false);
            m.Result = (IntPtr)1;
            return;
        }
        if (m.Msg == WmExitSizeMove && _resizing)
        {
            _resizing = false;
            RememberPosition(save: true);
            FitSize();
        }
        if (m.Msg == WmEnterSizeMove)
        {
            var cursor = Cursor.Position;
            _moveGrab = new Point(cursor.X - Left, cursor.Y - Top);
        }
        else if (m.Msg == WmMoving && m.LParam != IntPtr.Zero)
        {
            var r = Marshal.PtrToStructure<RECT>(m.LParam);
            var width = r.Right - r.Left;
            var height = r.Bottom - r.Top;
            var cursor = Cursor.Position;
            r = new RECT { Left = cursor.X - _moveGrab.X, Top = cursor.Y - _moveGrab.Y };
            r.Right = r.Left + width;
            r.Bottom = r.Top + height;
            var wa = Screen.FromRectangle(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)).WorkingArea;
            int snap = (int)U(EdgeSnap), margin = (int)U(EdgeMargin);

            // The nearest edge within reach wins; the widget settles with a small gap to the edge
            static int Nearest(int value, int snap, params int[] targets) =>
                targets.Select(t => (Target: t, Distance: Math.Abs(value - t)))
                       .Where(c => c.Distance <= snap)
                       .OrderBy(c => c.Distance)
                       .Select(c => (int?)c.Target)
                       .FirstOrDefault() ?? value;

            var left = Nearest(r.Left, snap, wa.Left + margin, wa.Right - margin - width);
            var top = Nearest(r.Top, snap, wa.Top + margin, wa.Bottom - margin - height);

            r = new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
            Marshal.StructureToPtr(r, m.LParam, false);
            m.Result = (IntPtr)1;
        }
        base.WndProc(ref m);
    }
}

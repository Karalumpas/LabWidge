using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// The widget is a layered window that sets its own pixels with UpdateLayeredWindow, so it can have any shape: the rounded
/// widget plus the rail's tab around the icons. The rest of the rail column is fully transparent – and clicks go through it.
/// Everything is still drawn by <see cref="Render"/>; this only turns the frame into the window's pixels.
/// </summary>
internal sealed partial class DashboardForm
{
    private const float CornerRadius = 8;

    private double _opacity = 1;
    private bool _presentQueued;
    private Bitmap? _surfaceFrame;
    private bool _surfaceDirty = true;
    private Point _paintedMouse = new(-1, -1);
    private SizeF _surfaceContentSize;
    private bool _interactionInvalidation;
    private bool _reuseSectionMeasurements;
    private readonly Dictionary<(string Key, bool Summary), float> _sectionMeasurements = new();
    private byte[] _frameBytes = Array.Empty<byte>(), _maskBytes = Array.Empty<byte>(), _outBytes = Array.Empty<byte>();
    private Bitmap? _mask;
    private string? _maskKey;

    protected override void OnInvalidated(InvalidateEventArgs e)
    {
        base.OnInvalidated(e);
        // Data/settings/size changes invalidate measurements; pointer movement does not.
        if (!_interactionInvalidation) { _sectionMeasurements.Clear(); _surfaceDirty = true; }
        QueuePresent();
    }

    private void InvalidateInteraction()
    {
        _interactionInvalidation = true;
        try { Invalidate(); }
        finally { _interactionInvalidation = false; }
    }

    /// <summary>Redraws once for any number of Invalidate calls in a row.</summary>
    private void QueuePresent()
    {
        if (_presentQueued || !IsHandleCreated || IsDisposed) return;
        _presentQueued = true;
        BeginInvoke(new Action(Present));
    }

    /// <summary>Renders the widget and hands the pixels, with the shape as their transparency, to Windows.</summary>
    private void Present()
    {
        _presentQueued = false;
        if (IsDisposed || !IsHandleCreated || !Visible) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            var frame = PaintSurface();
            var size = _surfaceContentSize;
            var height = ViewHeight(size.Height);
            if (Math.Abs(h - height) > 1 || Math.Abs(w - size.Width) > 1)
            {
                SetSize(size); // the content changed size: draw again at the new size
                continue;
            }
            if (_scroll > MaxScroll)
            {
                _scroll = MaxScroll;
                continue;
            }
            PresentFrame(frame);
            return;
        }
    }

    /// <summary>Retained RGB frame. Only interaction damage is repainted until data/layout changes.</summary>
    internal Bitmap PaintSurface(bool forceFull = false)
    {
        var size = ClientSize;
        if (_surfaceFrame == null || _surfaceFrame.Size != size)
        {
            _surfaceFrame?.Dispose();
            _surfaceFrame = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format24bppRgb);
            _surfaceDirty = true;
        }
        using var g = Graphics.FromImage(_surfaceFrame);
        if (!forceFull && !_surfaceDirty && _drag == null && _railDrag == null && _iconDrag == null)
        {
            using var damage = InteractionDamage();
            g.SetClip(damage, CombineMode.Replace);
        }
        // Graphics.Clear ignores the clip. Filling preserves all undamaged pixels.
        using (var background = new SolidBrush(_p.Bg)) g.FillRectangle(background, ClientRectangle);
        _reuseSectionMeasurements = true;
        try { _surfaceContentSize = Render(g); }
        finally { _reuseSectionMeasurements = false; }
        _surfaceDirty = false;
        _paintedMouse = _mouse;
        return _surfaceFrame; // owned and disposed by the widget
    }

    private Region InteractionDamage()
    {
        var damage = new Region();
        damage.MakeEmpty();
        if (RailDocked)
        {
            var column = RailColumn();
            column.Inflate(U(3), 0); // include the join with the widget outline
            damage.Union(Rectangle.Ceiling(column));
        }
        var left = ContentLeft;
        var width = ClientSize.Width - RailWidth;
        // The scroll thumb appears/disappears when the mouse enters/leaves.
        damage.Union(Rectangle.Ceiling(new RectangleF(left + width - U(8), 0, U(8), ClientSize.Height)));
        void IncludeHover(Point mouse)
        {
            if (mouse.X < 0 || (RailDocked && RailColumn().Contains(mouse))) return;
            foreach (var hit in _hits.Where(hit => hit.Rect.Contains(mouse)))
            {
                var top = (int)Math.Floor(hit.Rect.Top - U(6));
                var bottom = (int)Math.Ceiling(hit.Rect.Bottom + U(6));
                // Whole row: header icons and truncated values can change outside the hit itself.
                damage.Union(new Rectangle((int)Math.Floor(left), top, (int)Math.Ceiling(width), bottom - top));
            }
        }
        IncludeHover(_paintedMouse);
        IncludeHover(_mouse);
        return damage;
    }

    /// <summary>The rounded content: everything but the rail column.</summary>
    private RectangleF ContentRect(float width, float height) => new(ContentLeft, 0, width - RailWidth, height);

    /// <summary>The window's shape: the rounded content, and the drawer when the rail is docked. Everything else is transparent.</summary>
    private GraphicsPath WidgetShape(float width, float height)
    {
        var path = new GraphicsPath(FillMode.Winding);
        path.AddPath(WidgetIcon.RoundedRect(ContentRect(width, height), U(CornerRadius)), false);
        if (RailDocked)
        {
            using var tab = RailTabPath(RailLayout(height).Tab, width);
            tab.CloseFigure();
            path.AddPath(tab, false);
        }
        return path;
    }

    /// <summary>
    /// The border: the drawer's first, then the widget's on top – so the drawer looks like it comes out from behind the widget.
    /// Both lie half a pixel inside the shape, so they stay crisp.
    /// </summary>
    private void DrawOutline(Graphics g, float width, float height)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(_p.Line, Math.Max(1f, DpiScale));
        var half = pen.Width / 2;
        if (RailDocked)
        {
            var tab = RailLayout(height).Tab;
            using var outline = RailTabPath(new RectangleF(tab.X + half, tab.Y + half, tab.Width - half, tab.Height - half * 2), width);
            g.DrawPath(pen, outline);
        }
        using var content = WidgetIcon.RoundedRect(RectangleF.Inflate(ContentRect(width, height), -half, -half), U(CornerRadius) - half);
        g.DrawPath(pen, content);
        g.SmoothingMode = mode;
    }

    /// <summary>The frame with the widget's shape as its transparency: what the window shows.</summary>
    internal Bitmap Compose(Bitmap frame)
    {
        int w = frame.Width, h = frame.Height;
        var key = $"{w}x{h}|{RailDocked}|{_settings.LaunchRailPlacement}|{(RailDocked ? RailLayout(h).Tab : RectangleF.Empty)}|{DeviceDpi}";
        if (_mask == null || _maskKey != key)
        {
            _mask?.Dispose();
            _mask = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(_mask);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = WidgetShape(w, h);
            g.FillPath(Brushes.White, shape);
            _maskKey = key;
        }

        var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, w, h);
        var f = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var m = _mask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var o = output.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (_frameBytes.Length < f.Stride * h) _frameBytes = new byte[f.Stride * h];
            if (_maskBytes.Length < m.Stride * h) _maskBytes = new byte[m.Stride * h];
            if (_outBytes.Length < o.Stride * h) _outBytes = new byte[o.Stride * h];
            Marshal.Copy(f.Scan0, _frameBytes, 0, f.Stride * h);
            Marshal.Copy(m.Scan0, _maskBytes, 0, m.Stride * h);
            for (var y = 0; y < h; y++)
            {
                int fi = y * f.Stride, mi = y * m.Stride, oi = y * o.Stride;
                for (var x = 0; x < w; x++, fi += 3, mi += 4, oi += 4)
                {
                    _outBytes[oi] = _frameBytes[fi];
                    _outBytes[oi + 1] = _frameBytes[fi + 1];
                    _outBytes[oi + 2] = _frameBytes[fi + 2];
                    _outBytes[oi + 3] = _maskBytes[mi + 3];
                }
            }
            Marshal.Copy(_outBytes, 0, o.Scan0, o.Stride * h);
        }
        finally
        {
            frame.UnlockBits(f);
            _mask.UnlockBits(m);
            output.UnlockBits(o);
        }

        return output;
    }

    private void PresentFrame(Bitmap frame)
    {
        using var output = Compose(frame);
        LayeredWindowSurface.Present(Handle, output, _opacity);
    }
}

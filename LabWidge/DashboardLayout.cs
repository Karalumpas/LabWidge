using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed partial class DashboardForm
{
    private float LayoutWidth => Math.Clamp(_settings.WidgetWidth is int width ? width : LogicalWidth, 300, 640);
    private WidgetLayout _layout;
    private readonly HashSet<string> _autoSummaries = new();
    private readonly HashSet<string> _expandedSummaries = new();
    private string? _drawingSection;
    private bool _drawingSummary;
    private bool _resizing;
    private bool _settingSize;
    private readonly Func<(DateTime Time, string? Error)> _networkState;
    private ContextMenuStrip? _sectionMenu;

    private void DrawHeaderGrip(Graphics g, float x, float y, string? key)
    {
        using var brush = new SolidBrush(_p.TextDim);
        for (var col = 0; col < 2; col++)
            for (var row = 0; row < 3; row++)
                g.FillEllipse(brush, x - U(2) + U(col * 4), y + U(4 + row * 4), U(2), U(2));
        if (key == null) return;

        // The pin is always shown: a click pins the section where it belongs, another click unpins it
        var pinned = PinOf(key) != SectionPin.None;
        var rect = new RectangleF(x + U(7), y - U(2), U(16), U(20));
        var hovered = rect.Contains(_mouse);
        if (hovered) FillRound(g, rect, _p.Track, U(4));
        DrawText(g, pinned ? "" : "", _f.IconSmall, pinned ? _p.Blue : hovered ? _p.TextSecondary : _p.TextDim, x + U(10), y + U(3));
        var side = PinSideFor(key, y - _paintScroll);
        _hits.Add(new Hit(rect, pinned
                ? (PinOf(key) == SectionPin.Top ? L.T("Pinned to the top", "Fastgjort øverst") : L.T("Pinned to the bottom", "Fastgjort nederst"))
                  + L.T("\nClick to unpin", "\nKlik for at frigøre")
                : (side == SectionPin.Top ? L.T("Click to pin to the top", "Klik for at fastgøre øverst") : L.T("Click to pin to the bottom", "Klik for at fastgøre nederst"))
                  + L.T("\nRight-click for more options", "\nHøjreklik for flere valg"),
            () => SetSectionPin(key, pinned ? SectionPin.None : side)));
    }

    /// <summary>
    /// The side a click on the pin uses: the side the section was last pinned to, otherwise the nearest edge –
    /// so the section stays where the user is looking instead of jumping to the other end of the widget.
    /// </summary>
    private SectionPin PinSideFor(string key, float headerY)
    {
        if (_settings.SectionLastPins.GetValueOrDefault(key) is var last && last != SectionPin.None) return last;
        return headerY < ClientSize.Height / 2f ? SectionPin.Top : SectionPin.Bottom;
    }

    /// <summary>The scroll offset of the group being painted – turns content positions into window positions.</summary>
    private float _paintScroll;

    private void SetSectionPin(string key, SectionPin pin)
    {
        // Remember the side on unpin too, for sections pinned before the side was remembered
        if (PinOf(key) != SectionPin.None) _settings.SectionLastPins[key] = PinOf(key);
        if (pin == SectionPin.None) _settings.SectionPins.Remove(key);
        else _settings.SectionPins[key] = _settings.SectionLastPins[key] = pin;
        _expandedSummaries.Remove(key); _scroll = 0;
        _hoverKey = null;
        _saveSettings(); FitSize(); Invalidate();
    }

    private DataFreshness? StatusFor(string? key) => key switch
    {
        "price" => _el.TariffError != null && _settings.PriceShowTotal
            ? new DataFreshness(DataHealth.Stale, L.T("Price incomplete", "Pris ufuldstændig"), _el.TariffError)
            : DataFreshness.Describe(_el.LastSuccessfulFetch, _el.LastError, TimeSpan.FromMinutes(90), DateTime.Now),
        "ha" => DataFreshness.Describe(_ha.LastFetch, _ha.LastError, TimeSpan.FromMinutes(1), DateTime.Now),
        "cloudflare" => DataFreshness.Describe(_cf.LastSuccessfulDnsFetch, _cf.DnsError, TimeSpan.FromMinutes(5), DateTime.Now),
        "proxmox" => DataFreshness.Describe(_pve.LastFetch, _pve.LastError, TimeSpan.FromMinutes(1), DateTime.Now),
        "network" => DataFreshness.Describe(_networkState().Time, _networkState().Error, TimeSpan.FromMinutes(10), DateTime.Now),
        _ => null
    };

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_resizing || _settingSize || _settings.CompactMode) return;
        _settings.WidgetWidth = (int)Math.Round((ClientSize.Width - RailWidth) / DpiScale);
        _settings.WidgetHeight = (int)Math.Round(ClientSize.Height / DpiScale);
        Invalidate();
    }

    private SectionPin PinOf(string key) => _settings.SectionPins.GetValueOrDefault(key);
    private float MinimumViewHeight => U(Math.Max(180,
        VisibleSections().Count(s => PinOf(s.Key) != SectionPin.None) * 40 + 108));
    private float ViewHeight(float natural) => _settings.CompactMode ? natural
        : Math.Min(MaxViewHeight(), _settings.WidgetHeight is int height
            ? Math.Max(MinimumViewHeight, U(height)) : natural);

    private bool Collapsed(string key) => key switch
    {
        "price" => _settings.CollapsedPrice, "system" => _settings.CollapsedSystem,
        "network" => _settings.CollapsedNetwork, "audio" => _settings.CollapsedAudio,
        "ha" => _settings.CollapsedHomeAssistant,
        "cloudflare" => _settings.CollapsedCloudflare, "proxmox" => _settings.CollapsedProxmox,
        ShortcutsKey => _settings.CollapsedShortcuts,
        _ => true
    };

    private void SetCollapsed(string key, bool value)
    {
        switch (key)
        {
            case "price": _settings.CollapsedPrice = value; break;
            case "system": _settings.CollapsedSystem = value; break;
            case "network": _settings.CollapsedNetwork = value; break;
            case "audio": _settings.CollapsedAudio = value; break;
            case "ha": _settings.CollapsedHomeAssistant = value; break;
            case "cloudflare": _settings.CollapsedCloudflare = value; break;
            case "proxmox": _settings.CollapsedProxmox = value; break;
            case ShortcutsKey: _settings.CollapsedShortcuts = value; break;
        }
    }

    private float DrawSection(Graphics g, Section section, float x, float y, float w)
    {
        var savedSection = _drawingSection;
        var savedSummary = _drawingSummary;
        var collapsed = Collapsed(section.Key);
        _drawingSection = section.Key;
        _drawingSummary = _autoSummaries.Contains(section.Key) ||
            (PinOf(section.Key) != SectionPin.None && _settings.SectionPinSummaries.Contains(section.Key)
             && !_expandedSummaries.Contains(section.Key));
        if (_drawingSummary) SetCollapsed(section.Key, true);
        try { return section.Draw(g, x, y, w); }
        finally
        {
            SetCollapsed(section.Key, collapsed);
            _drawingSection = savedSection;
            _drawingSummary = savedSummary;
        }
    }

    private float MeasureGroup(Graphics g, List<Section> sections, float x, float w)
    {
        var state = g.Save();
        g.SetClip(Rectangle.Empty);
        float height = 0;
        foreach (var section in sections)
        {
            if (height > 0) height += U(SectionGap);
            height += DrawSection(g, section, x, 0, w);
        }
        g.Restore(state);
        return height;
    }

    private void PaintGroup(Graphics g, List<Section> sections, float x, float y, float w,
        RectangleF clip, float scroll, SectionPin zone)
    {
        if (sections.Count == 0 || clip.Height <= 0) return;
        var state = g.Save();
        g.SetClip(clip, CombineMode.Intersect);
        g.TranslateTransform(0, -scroll);
        var mouse = _mouse;
        _paintScroll = scroll;
        _mouse = new Point(mouse.X, mouse.Y + (int)Math.Round(scroll));
        var hitStart = _hits.Count;
        if (_drag is { } drag && PinOf(drag.Key) == zone)
        {
            DrawDragging(g, sections, x, w);
            foreach (var key in drag.Order)
                _sectionBounds.Add(new SectionBounds(key, drag.Anim[key] - scroll,
                    drag.Anim[key] + drag.Heights[key] - scroll));
        }
        else
        {
            foreach (var section in sections)
            {
                if (section != sections[0]) y = Separator(g, x, y, w);
                var top = y;
                y = DrawSection(g, section, x, y, w);
                _sectionBounds.Add(new SectionBounds(section.Key, top - scroll, y - scroll));
            }
        }
        for (var i = _hits.Count - 1; i >= hitStart; i--)
        {
            var hit = _hits[i];
            var rect = hit.Rect;
            rect.Offset(0, -scroll);
            rect.Intersect(clip);
            if (rect.Width <= 0 || rect.Height <= 0) _hits.RemoveAt(i);
            else _hits[i] = hit with { Rect = rect };
        }
        _mouse = mouse;
        _paintScroll = 0;
        g.Restore(state);
    }

    private SizeF RenderPinned(Graphics g)
    {
        float rail = ContentLeft, width = RailWidth + U(LayoutWidth), x = rail + U(Pad), w = U(LayoutWidth) - U(Pad) * 2, padding = U(14);
        var contentRight = rail + U(LayoutWidth);
        var sections = VisibleSections();
        var top = sections.Where(s => PinOf(s.Key) == SectionPin.Top).ToList();
        var middle = sections.Where(s => PinOf(s.Key) == SectionPin.None).ToList();
        var bottom = sections.Where(s => PinOf(s.Key) == SectionPin.Bottom).ToList();
        _autoSummaries.Clear();
        var topH = MeasureGroup(g, top, x, w);
        var bottomH = MeasureGroup(g, bottom, x, w);
        var middleH = MeasureGroup(g, middle, x, w);
        var gaps = U(SectionGap);
        var bottomGap = bottom.Count > 0 && (middle.Count > 0 || top.Count > 0) ? gaps : 0;
        if (top.Count > 0 && middle.Count > 0) topH += gaps;
        bottomH += bottomGap;
        var natural = topH + middleH + bottomH + padding * 2;
        var height = ViewHeight(Math.Max(natural, U(62)));
        if (topH + bottomH > height - padding * 2 - (middle.Count > 0 ? U(80) : 0))
        {
            foreach (var s in top.Concat(bottom).Where(s => s.Key != "ha")) _autoSummaries.Add(s.Key);
            topH = MeasureGroup(g, top, x, w) + (top.Count > 0 && middle.Count > 0 ? gaps : 0);
            bottomH = MeasureGroup(g, bottom, x, w) + bottomGap;
        }
        _layout = WidgetLayout.Calculate(height, topH, bottomH, middleH, padding);
        _scroll = Math.Clamp(_scroll, 0, _layout.ScrollMax);
        _hits.Clear();
        _sectionBounds.Clear();
        if (sections.Count == 0)
            DrawText(g, L.T("All sections are turned off – see Settings", "Alle sektioner er slået fra – se Indstillinger"), _f.Small, _p.TextSecondary, x, padding);

        // The middle is drawn first. All hit areas are clipped to the same window as the pixels.
        PaintGroup(g, middle, x, _layout.MiddleTop, w,
            new RectangleF(rail, _layout.MiddleTop, contentRight - rail, _layout.MiddleHeight), _scroll, SectionPin.None);
        DrawScrollHints(g);
        using var bg = new SolidBrush(_p.Bg);
        if (top.Count > 0)
        {
            g.FillRectangle(bg, rail, 0, contentRight - rail, _layout.MiddleTop);
            PaintGroup(g, top, x, padding, w, new RectangleF(rail, 0, contentRight - rail, _layout.MiddleTop), 0, SectionPin.Top);
            if (middle.Count > 0)
            {
                using var line = new Pen(_p.Line, Math.Max(1, DpiScale));
                g.DrawLine(line, x, _layout.MiddleTop - U(8), x + w, _layout.MiddleTop - U(8));
            }
        }
        if (bottom.Count > 0)
        {
            g.FillRectangle(bg, rail, _layout.BottomTop, contentRight - rail, height - _layout.BottomTop);
            var start = _layout.BottomTop + bottomGap;
            PaintGroup(g, bottom, x, start, w, new RectangleF(rail, _layout.BottomTop, contentRight - rail,
                height - _layout.BottomTop), 0, SectionPin.Bottom);
            if (bottomGap > 0)
            {
                using var line = new Pen(_p.Line, Math.Max(1, DpiScale));
                g.DrawLine(line, x, _layout.BottomTop + U(8), x + w, _layout.BottomTop + U(8));
            }
        }
        DrawRail(g, width, height, padding);
        if (_drag != null || _railDrag != null) _hits.Clear(); // nothing can be clicked while something is dragged
        if (_layout.MiddleHeight > 0 && middle.Count > 0)
        {
            var shadowHeight = Math.Min(U(7), _layout.MiddleHeight);
            if (top.Count > 0)
            {
                var rect = new RectangleF(rail, _layout.MiddleTop, contentRight - rail, shadowHeight);
                using var shadow = new LinearGradientBrush(rect, Color.FromArgb(28, Color.Black), Color.Transparent, 90f);
                g.FillRectangle(shadow, rect);
            }
            if (bottom.Count > 0)
            {
                var rect = new RectangleF(rail, _layout.BottomTop - shadowHeight, contentRight - rail, shadowHeight);
                using var shadow = new LinearGradientBrush(rect, Color.Transparent, Color.FromArgb(28, Color.Black), 90f);
                g.FillRectangle(shadow, rect);
            }
        }
        DrawOutline(g, width, height);
        DrawRailDrop(g, width, height);
        // A visible size grip; all four edges and corners can be resized.
        using var grip = new Pen(_p.TextDim, Math.Max(1, DpiScale));
        for (var i = 0; i < 3; i++)
            g.DrawLine(grip, contentRight - U(5 + i * 4), height - U(4), contentRight - U(4), height - U(5 + i * 4));
        return new SizeF(width, height);
    }

    private void ToggleHeader(string key, Action original)
    {
        if (_settings.CompactMode)
        {
            SetCollapsed(key, false);
            SetCompact(false);
            return;
        }
        if (_autoSummaries.Contains(key))
        {
            ShowTip(L.T("Make the widget taller or pin fewer sections to show the details.", "Gør widgetten højere eller fastgør færre sektioner for at vise detaljerne."), 4000);
            return;
        }
        if (PinOf(key) != SectionPin.None && _settings.SectionPinSummaries.Contains(key))
        {
            if (!_expandedSummaries.Add(key)) _expandedSummaries.Remove(key);
            FitSize(); Invalidate(); return;
        }
        original();
    }

    private void ShowSectionMenu(string key, Point point)
    {
        // WinForms still uses a dropdown after its Closed event while handling
        // the item click. Keep it alive until the next opening or form disposal.
        _sectionMenu?.Dispose();
        var menu = _sectionMenu = new ContextMenuStrip();
        if (key == ShortcutsKey)
        {
            AddRailPlacementItems(menu);
            menu.Items.Add(new ToolStripSeparator());
        }
        if (SectionWindows.Supports(key))
        {
            menu.Items.Add(new ToolStripMenuItem(L.T("Open in a window", "Åbn i et vindue"), null, (_, _) => SectionWindows.Toggle(key, Bounds))
            {
                Font = new Font(menu.Font, FontStyle.Bold),
                ToolTipText = L.T("You can also drag the section out of the widget", "Du kan også trække sektionen ud af widgetten")
            });
            menu.Items.Add(new ToolStripSeparator());
        }
        foreach (var (label, pin) in new[]
                 {
                     (L.T("Pin to the top", "Fastgør øverst"), SectionPin.Top),
                     (L.T("Pin to the bottom", "Fastgør nederst"), SectionPin.Bottom),
                     (L.T("Unpin the section", "Frigør sektionen"), SectionPin.None)
                 })
        {
            var item = new ToolStripMenuItem(label) { Checked = PinOf(key) == pin };
            item.Click += (_, _) => SetSectionPin(key, pin);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        var summary = new ToolStripMenuItem(L.T("Pin the summary only", "Fastgør kun resumé"))
        {
            Checked = _settings.SectionPinSummaries.Contains(key), Enabled = PinOf(key) != SectionPin.None
        };
        summary.Click += (_, _) =>
        {
            _settings.SectionPinSummaries = summary.Checked
                ? _settings.SectionPinSummaries.Where(k => k != key).ToArray()
                : _settings.SectionPinSummaries.Append(key).Distinct().ToArray();
            _expandedSummaries.Remove(key); _saveSettings(); FitSize(); Invalidate();
        };
        menu.Items.Add(summary);
        menu.Items.Add(L.T("Automatic size", "Automatisk størrelse"), null, (_, _) =>
        {
            _settings.WidgetWidth = _settings.WidgetHeight = null;
            _saveSettings(); FitSize(); Invalidate();
        });
        menu.Show(this, point);
    }

    private Color FreshnessColor(DataFreshness status) => status.Health switch
    {
        DataHealth.Offline => _p.Red, DataHealth.Stale => _p.Amber, _ => _p.TextDim
    };
}

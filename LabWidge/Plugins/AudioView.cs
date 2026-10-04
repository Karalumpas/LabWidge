using System.Drawing;

// Plugin rendering uses the shared widget drawing context.
internal sealed partial class DashboardForm
{
    internal float DrawAudio(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var devices = VisibleAudioDevices();
        var active = _audio.Devices.FirstOrDefault(d => d.Id == _audio.DefaultId);
        var right = active == null ? L.T("none", "ingen") : devices.Contains(active) ? AudioName(active) : Shorten(active.FullName, 28);
        y = Header(g, "", L.T("AUDIO", "LYD"), right, s.CollapsedAudio ? _p.TextSecondary : null, x, y, w,
                   s.CollapsedAudio, () => ToggleCollapsed(() => s.CollapsedAudio, v => s.CollapsedAudio = v));
        if (s.CollapsedAudio) return y - U(6);

        var gap = U(8);
        var perRow = Math.Max(1, (int)((w + gap) / (U(64) + gap)));
        var cols = Math.Min(devices.Count, perRow);
        var tileW = (w - gap * (cols - 1)) / cols;
        var tileH = U(54);
        y += U(2);

        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var col = i % perRow;
            if (i > 0 && col == 0) y += tileH + gap;
            var tile = new RectangleF(x + col * (tileW + gap), y, tileW, tileH);
            var isActive = d.Id == _audio.DefaultId;
            var hover = tile.Contains(_mouse);

            var battery = _audio.Batteries.GetValueOrDefault(d.Id);
            if (isActive)
            {
                FillRound(g, tile, Color.FromArgb(_p.IsDark ? 55 : 35, _p.Blue), U(7));
                if (battery != null) DrawBatteryLevel(g, battery, tile);
            }
            else
            {
                FillRound(g, tile, hover ? _p.HoverBg : _p.Bg, U(7));
                if (battery != null) DrawBatteryLevel(g, battery, tile);
                using var path = WidgetIcon.RoundedRect(tile, U(7));
                using var pen = new Pen(_p.Line, Math.Max(1f, DpiScale));
                g.DrawPath(pen, path);
            }

            var volume = _audio.Volumes.GetValueOrDefault(d.Id);
            var showVolume = s.AudioShowVolume && volume != null;
            if (showVolume) DrawVolumeBar(g, volume!, tile, isActive ? _p.Blue : _p.TextSecondary);

            var color = isActive ? _p.Blue : hover ? _p.TextPrimary : _p.TextSecondary;
            var glyph = AudioGlyph(d.Kind);
            var gs = Measure(g, glyph, _f.IconLarge);
            DrawText(g, glyph, _f.IconLarge, color, tile.X + (tile.Width - gs.Width) / 2, tile.Y + U(7));

            var name = AudioName(d);
            var label = name;
            while (label.Length > 2 && Measure(g, label, _f.Tiny).Width > tile.Width - U(8)) label = label[..^2];
            if (label != name) label = label.TrimEnd() + "…";
            var ls = Measure(g, label, _f.Tiny);
            DrawText(g, label, _f.Tiny, isActive ? _p.Blue : _p.TextSecondary, tile.X + (tile.Width - ls.Width) / 2, tile.Bottom - U(showVolume ? 21 : 19));

            var isStandard = d.Id == s.AudioStandardId;
            if (isStandard) DrawText(g, "", _f.IconSmall, _p.Amber, tile.Right - U(15), tile.Y + U(5));

            if (battery != null) DrawBattery(g, battery, tile, tile.X + (tile.Width - gs.Width) / 2, isActive ? _p.Blue : _p.TextSecondary);

            var tip = d.FullName + "\n" + (isActive ? L.T("Active audio device", "Aktiv lydenhed") : L.T("Click to switch to it", "Klik for at skifte hertil"))
                    + (isStandard ? L.T("\nDefault device", "\nStandardenhed") : "")
                    + (battery == null ? "" : L.T($"\nBattery: {battery.Percent} %", $"\nBatteri: {battery.Percent} %") + (battery.Charging ? L.T(" – charging", " – lader") : ""))
                    + (showVolume ? L.T($"\nVolume: {VolumeText(volume)} · scroll to adjust", $"\nLydstyrke: {VolumeText(volume)} · scroll for at justere") : "");
            var id = d.Id;
            _hits.Add(new Hit(tile, tip, isActive ? null : () => SwitchAudio(id), showVolume ? n => ChangeVolume(id, n) : null));
        }
        y += tileH;
        if (s.AudioShowMic) y = DrawMicrophone(g, x, y + gap, w);
        return y;
    }
}

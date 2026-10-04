using System.Drawing;

/// <summary>
/// The audio window: every output and microphone with its volume, mute and battery. Click a name to make it the
/// Windows default, click the volume bar to set the volume, and click the speaker or microphone icon to mute.
/// </summary>
internal sealed class AudioPanel : PopupPanel
{
    private readonly AudioService _audio;
    private readonly Action<string> _switchOutput;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    public AudioPanel(AudioService audio, Action<string> switchOutput, AppSettings settings, Action saveSettings)
        : base("audio", "", L.T("AUDIO", "LYD"), settings, saveSettings, 440)
    {
        _audio = audio;
        _switchOutput = switchOutput;
        // The tray app reads the devices every few seconds; the window just follows
        _tick.Tick += (_, _) => Invalidate();
        _tick.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }

    private string DeviceName(AudioDevice d) =>
        Settings.AudioNames.TryGetValue(d.Id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : d.Name;

    protected override string? HeaderStatus =>
        _audio.Devices.FirstOrDefault(d => d.Id == _audio.DefaultId) is { } d ? DeviceName(d) : L.T("no output", "ingen udgang");

    protected override float RenderContent(Graphics g, float x, float y, float w)
    {
        y = Subheading(g, L.T("OUTPUTS  ·  click a name to switch", "UDGANGE  ·  klik på et navn for at skifte"), x, y);
        if (_audio.Devices.Count == 0) y = Wrapped(g, L.T("No audio outputs found.", "Ingen lydudgange fundet."), F.Body, P.TextSecondary, x, y, w);
        foreach (var d in _audio.Devices) y = DeviceRow(g, d, false, x, y, w);

        y += U(6);
        y = Subheading(g, L.T("MICROPHONES", "MIKROFONER"), x, y);
        if (_audio.Microphones.Count == 0) y = Wrapped(g, L.T("No microphones found.", "Ingen mikrofoner fundet."), F.Body, P.TextSecondary, x, y, w);
        foreach (var m in _audio.Microphones) y = DeviceRow(g, m, true, x, y, w);

        y += U(4);
        return Wrapped(g, L.T("Names and which outputs the widget shows are set under Settings → Audio.",
                              "Navne og hvilke udgange widgetten viser, indstilles under Indstillinger → Lyd."), F.Tiny, P.TextDim, x, y, w);
    }

    private float DeviceRow(Graphics g, AudioDevice d, bool mic, float x, float y, float w)
    {
        var isDefault = mic ? d.Id == _audio.DefaultMicId : d.Id == _audio.DefaultId;
        var volume = _audio.Volumes.GetValueOrDefault(d.Id) ?? new VolumeState(0, false);
        var block = new RectangleF(x - U(8), y - U(5), w + U(16), U(50));
        if (isDefault) FillRound(g, block, Color.FromArgb(P.IsDark ? 34 : 26, P.Blue), U(7));

        // Icon – also the mute button
        var icon = new RectangleF(x - U(2), y - U(2), U(28), U(26));
        var iconHover = icon.Contains(Mouse);
        if (iconHover) FillRound(g, icon, P.HoverBg, U(5));
        var glyph = mic ? (volume.Muted ? "" : "") : volume.Muted ? "" : DashboardForm.AudioGlyph(d.Kind);
        DrawText(g, glyph, F.Icon, volume.Muted ? P.Red : isDefault ? P.Blue : P.TextSecondary, icon.X + U(6), icon.Y + U(5));
        var id = d.Id;
        AddHit(icon, volume.Muted ? L.T("Muted – click to unmute", "Lydløs – klik for at slå lyd til") : L.T("Click to mute", "Klik for at gøre lydløs"),
            () => { _audio.SetMute(id, !volume.Muted); Invalidate(); });

        // Name – click to make it the default
        var nameX = x + U(32);
        var battery = !mic && _audio.Batteries.TryGetValue(d.Id, out var b) ? b : null;
        var right = (isDefault ? L.T("default", "standard") : "") + (battery != null ? (isDefault ? "  ·  " : "") + $"{battery.Percent} %{(battery.Charging ? " ⚡" : "")}" : "");
        var rightW = right.Length > 0 ? DrawText(g, right, F.Small, battery is { Percent: <= 15 } ? P.Amber : isDefault ? P.Blue : P.TextDim, x + w, y + U(1), right: true) : 0;
        var nameRect = new RectangleF(nameX - U(4), y - U(2), w - (nameX - x) - rightW - U(6), U(22));
        var nameHover = !isDefault && nameRect.Contains(Mouse);
        DrawText(g, Fit(g, DeviceName(d), F.BodyBold, nameRect.Width - U(8)), F.BodyBold, nameHover ? P.Blue : P.TextPrimary, nameX, y);
        if (!isDefault)
        {
            AddHit(nameRect, (mic ? L.T("Click to use this microphone", "Klik for at bruge denne mikrofon") : L.T("Click to play sound here", "Klik for at afspille lyd her")) + "\n" + d.FullName,
                () =>
                {
                    if (mic) _audio.SetDefault(id, capture: true);
                    else _switchOutput(id);
                    _audio.Refresh();
                    Invalidate();
                });
        }
        else
        {
            AddHit(nameRect, d.FullName);
        }

        // Volume – click on the bar to set it
        var bar = new RectangleF(nameX, y + U(27), w - (nameX - x) - U(48), U(6));
        Bar(g, bar, volume.Level, volume.Muted ? P.TextDim : isDefault ? P.Blue : P.TextSecondary);
        DrawText(g, $"{volume.Level * 100:0} %", F.Small, P.TextSecondary, x + w, y + U(22), right: true);
        var barHit = new RectangleF(bar.X - U(4), bar.Y - U(7), bar.Width + U(8), bar.Height + U(14));
        AddHit(barHit, L.T("Click to set the volume", "Klik for at indstille lydstyrken"), () =>
        {
            var level = Math.Clamp((Mouse.X - bar.X) / bar.Width, 0f, 1f);
            _audio.SetVolume(id, level);
            if (volume.Muted) _audio.SetMute(id, false);
            Invalidate();
        });
        return y + U(56);
    }
}

using System.Drawing;

/// <summary>
/// The window behind the Docker section: every container on every Portainer host, grouped by host, with its state.
/// Clicking a container opens it in Portainer. Read-only: nothing is started or stopped from here.
/// </summary>
internal sealed class DockerPanel : PopupPanel
{
    private readonly PortainerService _docker;

    public DockerPanel(PortainerService docker, AppSettings settings, Action saveSettings)
        : base("docker", "", "DOCKER", settings, saveSettings, 400)
    {
        _docker = docker;
        _docker.Updated += OnDataUpdated;
    }

    private void OnDataUpdated() => RequestRedraw();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _docker.Updated -= OnDataUpdated;
        base.Dispose(disposing);
    }

    protected override string HeaderStatus =>
        _docker.LastFetch == DateTime.MinValue ? L.T("fetching…", "henter…") : L.T("updated ", "opdateret ") + _docker.LastFetch.ToString("HH:mm:ss");

    protected override float RenderContent(Graphics g, float x, float y, float w)
    {
        if (_docker.LastError != null) y = Wrapped(g, "⚠ " + _docker.LastError, F.Small, P.Amber, x, y, w);

        var first = true;
        foreach (var env in _docker.Environments)
        {
            if (!first) { y += U(4); y = Divider(g, x, y, w); }
            first = false;
            Dot(g, !env.Up ? P.Red : env.Attention > 0 ? P.Amber : P.Green, x + U(1), y + U(5), 9);
            DrawText(g, env.Name, F.BodyBold, P.TextPrimary, x + U(18), y);
            var summary = !env.Up ? "offline"
                : L.T($"{env.Running} running", $"{env.Running} kører") + (env.Stopped > 0 ? L.T($" · {env.Stopped} stopped", $" · {env.Stopped} stoppet") : "");
            DrawText(g, summary, F.Small, env.Up ? P.TextSecondary : P.Red, x + w, y + U(1), right: true);
            AddHit(new RectangleF(x, y - U(2), w, U(20)), env.Name + L.T("\nClick to open it in Portainer", "\nKlik for at åbne den i Portainer"),
                () => Open(_docker.WebUrl(env)));
            y += U(22);

            if (!env.Up)
            {
                y = Wrapped(g, env.Error ?? L.T("Does not answer", "Svarer ikke"), F.Small, P.TextDim, x + U(18), y, w - U(18));
                continue;
            }
            if (env.Containers.Count == 0)
            {
                DrawText(g, L.T("No containers", "Ingen containere"), F.Small, P.TextDim, x + U(18), y);
                y += U(20);
                continue;
            }
            foreach (var container in env.Containers) y = DrawContainer(g, env, container, x, y, w);
        }

        y += U(4);
        y = Divider(g, x, y, w);
        return LinkRow(g, L.T("Open Portainer", "Åbn Portainer"), _docker.WebUrl(), x, y, w);
    }

    private float DrawContainer(Graphics g, DockerEnvironment env, DockerContainer c, float x, float y, float w)
    {
        var row = new RectangleF(x - U(6), y - U(3), w + U(12), U(22));
        var hovered = row.Contains(Mouse);
        if (hovered) FillRound(g, row, P.HoverBg, U(5));

        var color = c.NeedsAttention ? (c.IsRunning || c.State == "restarting" ? P.Amber : P.Red)
            : c.Health == ContainerHealth.Starting ? P.Amber
            : c.IsRunning ? P.Green : P.Track;
        Dot(g, color, x + U(19), y + U(6), 7);

        var right = Describe(c);
        var rw = DrawText(g, Fit(g, right, F.Small, w * 0.45f), F.Small, c.NeedsAttention ? color : P.TextSecondary, x + w, y + U(1), right: true);
        var nx = x + U(34);
        DrawText(g, Fit(g, c.Name, F.Body, x + w - rw - U(10) - nx), F.Body, hovered ? P.Blue : c.IsRunning ? P.TextPrimary : P.TextSecondary, nx, y);

        var tip = c.Name + "\n" + c.Image
                  + (c.Stack != null ? L.T($"\nStack: {c.Stack}", $"\nStak: {c.Stack}") : "")
                  + "\n" + c.Status
                  + L.T("\nClick to open it in Portainer", "\nKlik for at åbne den i Portainer");
        AddHit(row, tip, () => Open($"{_docker.WebUrl(env)}/{c.Id}"));
        return y + U(22);
    }

    /// <summary>A short state: "running", "unhealthy", "stopped (exit 137)", "restarting".</summary>
    internal static string Describe(DockerContainer c) => c switch
    {
        { Health: ContainerHealth.Unhealthy } => L.T("unhealthy", "usund"),
        { Health: ContainerHealth.Starting } => L.T("starting", "starter"),
        { State: "restarting" } => L.T("restarting", "genstarter"),
        { State: "dead" } => L.T("dead", "død"),
        { State: "paused" } => L.T("paused", "sat på pause"),
        { State: "created" } => L.T("created", "oprettet"),
        { IsRunning: true } => c.Health == ContainerHealth.Healthy ? L.T("running · healthy", "kører · sund") : L.T("running", "kører"),
        { ExitCode: int code } when code != 0 => L.T($"stopped (exit {code})", $"stoppet (kode {code})"),
        _ => L.T("stopped", "stoppet")
    };
}

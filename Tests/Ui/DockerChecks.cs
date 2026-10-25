using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;

internal static partial class UiChecks
{
    /// <summary>Simulated Portainer server: answers by path, records what was asked, and can hold an answer back.</summary>
    internal sealed class FakePortainer : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Answers { get; } = new();
        public List<string> Requests { get; } = new();
        public List<string?> Keys { get; } = new();
        public TaskCompletionSource? Hold { get; set; }
        /// <summary>Sent with a 3xx answer, like a login page in front of Portainer.</summary>
        public Uri? Location { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            lock (Requests)
            {
                Requests.Add(request.RequestUri.GetLeftPart(UriPartial.Authority) + "/" + path);
                Keys.Add(request.Headers.TryGetValues("X-API-Key", out var key) ? key.Single() : null);
            }
            if (Hold is { } hold) await hold.Task.WaitAsync(cancel);
            var (status, body) = Answers.TryGetValue(path, out var answer) ? answer : (HttpStatusCode.NotFound, "{}");
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if ((int)status is >= 300 and < 400) response.Headers.Location = Location;
            return response;
        }
    }

    private const string FourHosts = """
        [ { "Id": 1, "Name": "mgserver", "Type": 2, "Status": 1 },
          { "Id": 2, "Name": "web", "Type": 2, "Status": 2 },
          { "Id": 3, "Name": "ubserver", "Type": 2, "Status": 1 },
          { "Id": 4, "Name": "Dumbles", "Type": 1, "Status": 1 } ]
        """;

    private static void SeedPortainer(FakePortainer fake)
    {
        fake.Answers.Clear();
        fake.Answers["api/endpoints"] = (HttpStatusCode.OK, FourHosts);
        fake.Answers["api/endpoints/1/docker/containers/json?all=1"] = (HttpStatusCode.OK, """
            [ { "Id": "a1", "Names": ["/home-assistant"], "Image": "ghcr.io/home-assistant/home-assistant", "State": "running", "Status": "Up 3 days (healthy)", "Labels": { "com.docker.compose.project": "home" } },
              { "Id": "a2", "Names": ["/zigbee2mqtt"], "Image": "koenkk/zigbee2mqtt", "State": "running", "Status": "Up 3 days" },
              { "Id": "a3", "Names": ["/postgres"], "Image": "postgres:16", "State": "running", "Status": "Up 2 minutes (unhealthy)" },
              { "Id": "a4", "Names": ["/backup"], "Image": "restic/restic", "State": "exited", "Status": "Exited (1) 2 hours ago" } ]
            """);
        // ubserver is "up" in Portainer, but its agent fails – that host alone shows the error
        fake.Answers["api/endpoints/3/docker/containers/json?all=1"] = (HttpStatusCode.InternalServerError, "{}");
        fake.Answers["api/endpoints/4/docker/containers/json?all=1"] = (HttpStatusCode.OK, """
            [ { "Id": "d1", "Names": ["/portainer_agent"], "Image": "portainer/agent", "State": "running", "Status": "Up 5 hours" },
              { "Id": "d2", "Names": ["/old-test"], "Image": "nginx", "State": "exited", "Status": "Exited (0) 9 days ago" } ]
            """);
    }

    private static void RunRefresh(PortainerService docker, AppSettings settings, CancellationToken cancel = default)
    {
        var task = docker.RefreshAsync(settings, cancel);
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void DockerChecks()
    {
        using var fixture = new Fixture();
        var s = fixture.Settings;
        var widget = fixture.Widget;
        var fake = fixture.Portainer;
        foreach (var plugin in WidgetPlugins.All) s.SetPluginEnabled(plugin.Key, false);
        s.SetPluginEnabled("docker", true);
        s.CollapsedDocker = false;
        Check(s.HasDocker && s.DockerEnabled, "Turning the Docker plugin on did not switch on its connection.");

        // A fresh service: four hosts, one offline, one whose agent fails
        SeedPortainer(fake);
        var docker = widget.Services.Docker;
        var updates = 0;
        docker.Updated += () => updates++;
        RunRefresh(docker, s);
        var envs = docker.Environments;
        Check(docker.LastError == null && envs.Count == 4, "Not every Portainer host was listed.");
        Check(envs[0].Name == "mgserver" && envs[0].Up && envs[0].Containers.Count == 4 && envs[0].Attention == 2,
            "mgserver's containers or problems were read wrongly.");
        Check(!envs[1].Up && envs[1].Error != null, "An offline host was not shown as offline.");
        Check(!envs[2].Up && envs[2].Error!.Contains("HTTP 500") && envs[2].Error!.Contains(L.T("agent", "agenten")) && envs[3].Up && envs[3].Containers.Count == 2,
            "One failing host must not stop the others.");
        Check(!fake.Requests.Any(r => r.Contains("endpoints/2/")), "Containers were asked for on a host Portainer reports as down.");
        Check(fake.Requests.All(r => r.StartsWith("https://example.test:9443/")) && fake.Keys.All(k => k == "test-token"),
            "The requests did not go to the Portainer server with the access token.");
        Check(updates == 1, "One refresh should redraw once.");
        Console.WriteLine("PASS Docker: all Portainer hosts with their containers; an offline or failing host does not stop the others");

        // The widget: one line per host, problems named, header warns about the offline host
        widget.ApplySettings(s);
        using (var frame = Frame(widget, "docker/widget.png")) { }
        var tips = Hits(widget).Select(h => h.Tip ?? "").ToList();
        Check(new[] { "mgserver", "web", "ubserver", "Dumbles" }.All(name => tips.Any(t => t.StartsWith(name + "\n"))), "A host has no line in the widget.");
        Check(tips.Any(t => t.StartsWith("postgres\n")) && tips.Any(t => t.StartsWith("backup\n")), "Containers that need attention are not named in the widget.");
        Check(!tips.Any(t => t.StartsWith("old-test\n")), "A cleanly stopped container was shown as a problem.");
        var expanded = DockerHeight(widget);
        s.CollapsedDocker = true;
        Check(DockerHeight(widget) < expanded, "The collapsed Docker section is not shorter.");
        using (var bitmap = new Bitmap(640, 400))
        using (var g = Graphics.FromImage(bitmap))
            Check(WidgetPlugins.Find("docker")!.RenderCompact(widget, g, 16, 0, 400) < expanded, "The compact Docker view is not shorter than the expanded one.");
        s.CollapsedDocker = false;
        Console.WriteLine("PASS Docker: the widget lists every host, names the containers that need attention, and collapses");

        // The window: every container by host, both themes
        foreach (var theme in new[] { WidgetTheme.Dark, WidgetTheme.Light })
        {
            s.Theme = theme;
            widget.ApplySettings(s);
            SectionWindows.OpenPinnedAt("docker", Screen.PrimaryScreen!.WorkingArea.Location + new Size(60, 60));
            Application.DoEvents();
            Check(SectionWindows.IsOpen("docker"), "The Docker window did not open.");
            var window = OpenWindow("docker");
            Check(window is DockerPanel, "The Docker section opened the wrong window.");
            using var bitmap = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
            bitmap.Save(Path.Combine(Output, "docker", $"window-{theme.ToString().ToLowerInvariant()}.png"), ImageFormat.Png);
            SectionWindows.Close("docker");
            Application.DoEvents();
        }
        s.SectionWindows.Remove("docker");
        s.Theme = WidgetTheme.Dark;
        Check(DockerPanel.Describe(envs[0].Containers.Single(c => c.Name == "backup")).Contains('1')
              && DockerPanel.Describe(envs[3].Containers.Single(c => c.Name == "old-test")) == L.T("stopped", "stoppet"),
            "Stopped containers do not say whether they failed.");
        Console.WriteLine("PASS Docker: the window shows every container by host in both themes");

        // A refused token keeps the last good lists but reports the error; a fresh service shows it in the widget
        fake.Answers["api/endpoints"] = (HttpStatusCode.Unauthorized, "{}");
        RunRefresh(docker, s);
        Check(docker.LastError == L.T("The access token was refused", "Adgangstokenet blev afvist") && docker.Environments.Count == 4,
            "A refused token lost the last lists or gave no clear error.");
        var fresh = new FakePortainer();
        fresh.Answers["api/endpoints"] = (HttpStatusCode.Unauthorized, "{}");
        var unauthorized = new PortainerService(fresh, () => "wrong");
        RunRefresh(unauthorized, s);
        Check(unauthorized.Environments.Count == 0 && unauthorized.LastError != null, "A refused token on first use produced data.");
        var noToken = new PortainerService(new FakePortainer(), () => null);
        RunRefresh(noToken, s);
        Check(noToken.LastError == L.T("No access token saved", "Intet adgangstoken gemt"), "A missing token gave no clear error.");
        var notPortainer = new FakePortainer();
        notPortainer.Answers["api/endpoints"] = (HttpStatusCode.OK, "<html>login</html>");
        var wrongAddress = new PortainerService(notPortainer, () => "token");
        RunRefresh(wrongAddress, s);
        Check(wrongAddress.LastError != null && wrongAddress.LastError.Contains("Portainer"), "An address that is not Portainer gave no useful error.");
        // Portainer behind Cloudflare Access: the API call is sent to a login page, which must be named, not parsed
        var behindAccess = new FakePortainer { Location = new Uri("https://example.cloudflareaccess.com/cdn-cgi/access/login/portainer.example.test") };
        behindAccess.Answers["api/endpoints"] = (HttpStatusCode.Found, "<html>302 Found</html>");
        var accessService = new PortainerService(behindAccess, () => "token");
        RunRefresh(accessService, s);
        Check(accessService.LastError != null && accessService.LastError.Contains("Cloudflare Access") && accessService.LastError.Contains("localhost"),
            "Cloudflare Access in front of Portainer was not explained.");
        var otherLogin = new FakePortainer { Location = new Uri("https://login.example.test/") };
        otherLogin.Answers["api/endpoints"] = (HttpStatusCode.Redirect, "");
        var loginService = new PortainerService(otherLogin, () => "token");
        RunRefresh(loginService, s);
        Check(loginService.LastError != null && loginService.LastError.Contains(L.T("login page", "login-side")), "A redirect to a login page was not explained.");
        Console.WriteLine("PASS Docker: refused or missing token, a wrong address and Cloudflare Access give clear errors and keep the last good lists");

        // Cancellation: a refresh in flight stops, reports nothing and leaves the data alone
        SeedPortainer(fake);
        fake.Hold = new TaskCompletionSource();
        using (var cancel = new CancellationTokenSource())
        {
            var before = updates;
            var errorBefore = docker.LastError;
            var task = docker.RefreshAsync(s, cancel.Token);
            cancel.Cancel();
            PumpUntil(() => task.IsCompleted);
            Check(task.IsCanceled || task.Exception?.InnerException is OperationCanceledException, "Cancelling did not stop the refresh.");
            Check(updates == before && docker.LastError == errorBefore, "A cancelled refresh redrew or reported an error.");
        }
        fake.Hold = null;
        RunRefresh(docker, s);
        Check(docker.LastError == null, "The service did not recover after a cancelled refresh.");

        // Switched off: the lists are cleared and nothing is asked for
        s.SetPluginEnabled("docker", false);
        fake.Requests.Clear();
        RunRefresh(docker, s);
        Check(docker.Environments.Count == 0 && fake.Requests.Count == 0, "A disabled Docker plugin kept its data or contacted Portainer.");
        using (var runtime = new PluginRuntime(new IWidgetPlugin[] { WidgetPlugins.Find("docker")! }))
        {
            runtime.ApplySettings(widget.Services, s);
            Check(!Get<IEnumerable<object>>(runtime, "_workers").Any(), "A disabled Docker plugin started background work.");
            var on = s.Clone();
            on.SetPluginEnabled("docker", true);
            runtime.ApplySettings(widget.Services, on);
            Check(Get<IEnumerable<object>>(runtime, "_workers").Count() == 1, "An enabled Docker plugin has no background job.");
        }
        Console.WriteLine("PASS Docker: cancellation stops a refresh quietly, and a disabled plugin clears its data and asks for nothing");

        // Settings: the page fits, validates, and never needs a token just to be switched off
        s.SetPluginEnabled("docker", true);
        var page = new DockerPage();
        page.UsePluginActivation();
        page.LoadFrom(s);
        var draft = s.Clone();
        SetText(page, "_url", "");
        Check(page.SaveTo(draft) != null, "An empty Portainer address was accepted.");
        SetText(page, "_url", "mgserver");
        var saved = page.SaveTo(draft);
        if (string.IsNullOrWhiteSpace(CredentialStore.ReadPortainerToken()))
            Check(saved != null, "Docker was saved without any access token.");
        else
            Check(saved == null && draft.PortainerUrl == "https://mgserver:9443", "A valid address was not normalised.");
        var off = new DockerPage();
        off.LoadFrom(new AppSettings());
        Check(off.SaveTo(new AppSettings()) == null, "A switched-off Docker page asked for an address or token.");
        using (var window = new SettingsWindow(s))
        {
            window.Show();
            Application.DoEvents();
            var pages = Get<SettingsPage[]>(window, "_pages");
            var dockerPage = pages.OfType<DockerPage>().SingleOrDefault();
            Check(dockerPage != null, "The Docker settings page is missing when the plugin is on.");
            var task = (Task)Call(window, "ShowPageAsync", Array.IndexOf(pages, dockerPage!))!;
            PumpUntil(() => task.IsCompleted);
            Application.DoEvents();
            LayoutProblems.Clear();
            SettingsPageLayout(dockerPage!, "DockerPage");
            Check(LayoutProblems.Count == 0, "Docker settings layout:\n" + string.Join("\n", LayoutProblems.Distinct()));
            using var bitmap = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
            bitmap.Save(Path.Combine(Output, "docker", "settings.png"), ImageFormat.Png);
            window.Close();
        }
        Console.WriteLine("PASS Docker: the settings page fits, validates the address and token, and a switched-off page needs nothing");
    }

    private static float DockerHeight(DashboardForm widget)
    {
        using var bitmap = new Bitmap(640, 1200);
        using var g = Graphics.FromImage(bitmap);
        return WidgetPlugins.Find("docker")!.RenderExpanded(widget, g, 16, 0, 400);
    }

    private static void SetText(Control root, string field, string text) => Get<TextBox>(root, field).Text = text;
}

using System.Diagnostics;
using System.Text.RegularExpressions;

/// <summary>A shortcut in the widget's left rail: a website, a program, a shortcut (.lnk) or any address Windows can open.</summary>
internal sealed class LaunchItem
{
    public string Name { get; set; } = "";
    /// <summary>E.g. https://youtube.com, C:\…\Discord.lnk, notepad.exe, discord:// or shell:AppsFolder\….</summary>
    public string Target { get; set; } = "";
    /// <summary>Command-line arguments for a program. Empty for websites.</summary>
    public string? Arguments { get; set; }
    /// <summary>An image (.png, .ico, .jpg) or a program whose icon is used instead of the automatic one.</summary>
    public string? IconPath { get; set; }

    /// <summary>A website: an address with http(s), or something that looks like a domain ("youtube.com").</summary>
    public static bool IsWeb(string target) => WebUri(target) != null;

    public static Uri? WebUri(string target)
    {
        target = target.Trim();
        if (target.Length == 0) return null;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            return uri.Scheme is "http" or "https" ? uri : null;
        // "youtube.com" or "www.chatgpt.com/foo" – a dot in the host, no spaces and no path separators of a file
        if (target.Contains(' ') || target.Contains('\\') || !Regex.IsMatch(target, @"^[\w-]+(\.[\w-]+)+(:\d+)?(/.*)?$")) return null;
        if (Regex.IsMatch(target, @"\.(exe|lnk|bat|cmd|msc|url)$", RegexOptions.IgnoreCase)) return null;
        return Uri.TryCreate("https://" + target, UriKind.Absolute, out uri) ? uri : null;
    }

    /// <summary>What is started: a domain gets https:// in front, environment variables are expanded.</summary>
    public string LaunchTarget() => WebUri(Target)?.ToString() ?? Environment.ExpandEnvironmentVariables(Target.Trim().Trim('"'));

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name.Trim()
        : WebUri(Target) is { } uri ? uri.Host.Replace("www.", "")
        : Path.GetFileNameWithoutExtension(Target.Trim().Trim('"'));

    /// <summary>How Windows starts it: through the shell, so websites open in the default browser and shortcuts work.</summary>
    public ProcessStartInfo StartInfo()
    {
        var target = LaunchTarget();
        var info = new ProcessStartInfo { FileName = target, UseShellExecute = true };
        if (!IsWeb(Target))
        {
            if (!string.IsNullOrWhiteSpace(Arguments)) info.Arguments = Environment.ExpandEnvironmentVariables(Arguments);
            if (Path.IsPathFullyQualified(target) && Path.GetDirectoryName(target) is { } dir && Directory.Exists(dir))
                info.WorkingDirectory = dir;
        }
        return info;
    }

    /// <summary>Suggestions for an empty rail. Discord uses the installed app when there is one.</summary>
    public static List<LaunchItem> Examples()
    {
        var discord = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Discord Inc", "Discord.lnk");
        return new List<LaunchItem>
        {
            new() { Name = "YouTube", Target = "https://www.youtube.com" },
            new() { Name = "Instagram", Target = "https://www.instagram.com" },
            new() { Name = "ChatGPT", Target = "https://chatgpt.com" },
            new() { Name = "Discord", Target = File.Exists(discord) ? discord : "https://discord.com/app" }
        };
    }
}

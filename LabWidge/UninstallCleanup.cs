using System.Diagnostics;
using System.Text;

internal static class UninstallCleanup
{
    public static ProcessStartInfo StartInfo(string installDir, int processId)
    {
        var expected = Path.GetFullPath(SelfInstaller.InstallDir);
        var target = Path.GetFullPath(installDir);
        if (!string.Equals(target, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid uninstall folder.");

        return new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "
                        + Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildScript(target, processId))),
            CreateNoWindow = true,
            UseShellExecute = false
        };
    }

    internal static string BuildScript(string installDir, int processId)
    {
        // Wait for the process itself, even if the user leaves the last dialog open.
        var literal = Path.GetFullPath(installDir).Replace("'", "''");
        return $@"
$widgetProcess = Get-Process -Id {processId} -ErrorAction SilentlyContinue
if ($widgetProcess) {{ $widgetProcess.WaitForExit() }}
$target = [IO.Path]::GetFullPath('{literal}')
$expected = '{literal}'
if ($target -ne [IO.Path]::GetFullPath($expected)) {{ exit 1 }}
for ($attempt = 0; $attempt -lt 10; $attempt++) {{
    try {{
        if (Test-Path -LiteralPath $target) {{ Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop }}
        exit 0
    }} catch {{ Start-Sleep -Milliseconds 500 }}
}}
exit 1";
    }
}

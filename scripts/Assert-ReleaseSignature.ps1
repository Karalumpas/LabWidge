param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
foreach ($file in $Path) {
    $resolved = (Resolve-Path -LiteralPath $file).Path
    $signature = Get-AuthenticodeSignature -LiteralPath $resolved
    if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate) {
        throw "Release blocked: $file has no valid trusted Authenticode signature ($($signature.Status))."
    }
    if (-not $signature.TimeStamperCertificate) {
        throw "Release blocked: $file has no trusted signing timestamp."
    }
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($resolved)
    if ($metadata.ProductName -ne 'LabWidge' -or $metadata.ProductVersion.Split('+')[0] -ne $Version -or
        $metadata.FileVersion -ne "$Version.0") {
        throw "Release blocked: $file has unexpected product/version metadata."
    }
    Write-Host "Verified $([IO.Path]::GetFileName($resolved)): $($signature.SignerCertificate.Subject)"
}

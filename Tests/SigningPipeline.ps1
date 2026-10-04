# Offline guard tests. Mock signatures exercise validation; they do not issue or trust a certificate.
param([Parameter(Mandatory)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$appPath = (Resolve-Path -LiteralPath $AppDirectory).Path
$version = ([xml](Get-Content "$repoRoot/LabWidge/LabWidge.csproj")).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
$exe = Join-Path $appPath 'LabWidge.exe'
$dll = Join-Path $appPath 'LabWidge.dll'

function Assert-Rejected([scriptblock]$Operation, [string]$Expected) {
    $message = $null
    try { & $Operation } catch { $message = $_.Exception.Message }
    if (-not $message -or $message -notlike $Expected) { throw "Expected rejection '$Expected', got '$message'." }
}

# Use the real Windows verifier before introducing mocks.
Assert-Rejected { & "$repoRoot/scripts/Assert-ReleaseSignature.ps1" -Path $exe -Version $version } '*no valid trusted Authenticode signature*'
Assert-Rejected { & "$repoRoot/scripts/Build-SetupFromSignedApp.ps1" -AppDirectory $appPath -Version $version } '*no valid trusted Authenticode signature*'

$global:labWidgeSignatureTestStatus = 'Valid'
$global:labWidgeSignatureTestTimestamp = [pscustomobject]@{ Subject = 'Mock timestamp' }
function Get-AuthenticodeSignature {
    param([string]$LiteralPath)
    [pscustomobject]@{
        Status = $global:labWidgeSignatureTestStatus
        SignerCertificate = [pscustomobject]@{ Subject = 'Mock publisher, not a trusted test certificate' }
        TimeStamperCertificate = $global:labWidgeSignatureTestTimestamp
    }
}

try {
    & "$repoRoot/scripts/Assert-ReleaseSignature.ps1" -Path @($exe, $dll) -Version $version
    Assert-Rejected { & "$repoRoot/scripts/Assert-ReleaseSignature.ps1" -Path $exe -Version '0.0.0' } '*unexpected product/version metadata*'
    Assert-Rejected { & "$repoRoot/scripts/Assert-ReleaseSignature.ps1" -Path (Join-Path $appPath 'missing.exe') -Version $version } '*does not exist*'
    $global:labWidgeSignatureTestTimestamp = $null
    Assert-Rejected { & "$repoRoot/scripts/Assert-ReleaseSignature.ps1" -Path $exe -Version $version } '*no trusted signing timestamp*'
    $global:labWidgeSignatureTestTimestamp = [pscustomobject]@{ Subject = 'Mock timestamp' }
    foreach ($status in @('NotSigned', 'HashMismatch', 'NotTrusted', 'UnknownError')) {
        $global:labWidgeSignatureTestStatus = $status
        Assert-Rejected { & "$repoRoot/scripts/Assert-ReleaseSignature.ps1" -Path $exe -Version $version } '*no valid trusted Authenticode signature*'
    }
} finally {
    Remove-Item Function:Get-AuthenticodeSignature
    Remove-Variable labWidgeSignatureTestStatus,labWidgeSignatureTestTimestamp -Scope Global
}
Write-Host 'PASS: signing guards reject unsigned/untrusted/tampered files, absent timestamps, missing files and mismatched versions.'

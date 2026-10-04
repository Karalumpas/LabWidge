param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$signedApp = (Resolve-Path -LiteralPath $AppDirectory).Path
# Sign the app before embedding it: signing Setup alone leaves Windows blocking the installed app.
& "$PSScriptRoot/Assert-ReleaseSignature.ps1" -Path @(
    (Join-Path $signedApp 'LabWidge.exe'), (Join-Path $signedApp 'LabWidge.dll')
) -Version $Version

$package = Join-Path $repoRoot 'dist/signed-app.zip'
if (Test-Path -LiteralPath $package) { throw "Package already exists: $package. Use a fresh build workspace." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($signedApp, $package, [IO.Compression.CompressionLevel]::Optimal, $false)
$setupOutput = Join-Path $repoRoot 'dist/unsigned-setup'
dotnet build (Join-Path $repoRoot 'Setup/LabWidge.Setup.csproj') -c Release -o $setupOutput "-p:AppPackagePath=$package" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Building Setup with the signed app failed.' }

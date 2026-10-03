# Builds LabWidge and its installer.
#   dist\LabWidge-Setup.exe - the installer: checks/installs .NET 8 Desktop Runtime and installs the app
# Run from the repository root:  powershell -ExecutionPolicy Bypass -File .\install.ps1
# Build only (e.g. to share Setup):  powershell -ExecutionPolicy Bypass -File .\install.ps1 -BuildOnly

param([switch]$BuildOnly)

$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$dist  = Join-Path $root 'dist'
$app   = Join-Path $dist 'app'
$zip   = Join-Path $dist 'app.zip'
$setup = Join-Path $dist 'LabWidge-Setup.exe'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

Write-Host 'Building LabWidge (without runtime)...'
dotnet publish (Join-Path $root 'LabWidge\LabWidge.csproj') -c Release -o $app --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish of the app failed' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host 'Building LabWidge-Setup.exe...'
$setupOut = Join-Path $dist 'setup-build'
dotnet build (Join-Path $root 'Setup\LabWidge.Setup.csproj') -c Release -o $setupOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Building Setup failed' }
Move-Item (Join-Path $setupOut 'LabWidge-Setup.exe') $setup
Remove-Item $setupOut, $app, $zip -Recurse -Force

"LabWidge-Setup.exe  {0:N1} MB" -f ((Get-Item $setup).Length / 1MB)

if ($BuildOnly) { return }

Write-Host 'Starting the installation...'
Start-Process $setup

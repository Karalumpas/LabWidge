# Builds the Microsoft Store package (MSIX) – unsigned, because the Store signs it after certification.
# The identity values are in Partner Center under Product management → Product identity. Without them the package
# gets placeholder values: it then shows that packaging works, but the Store will not accept it.
param(
    [string]$IdentityName = 'LabWidge.Dev',
    [string]$Publisher = 'CN=LabWidge-Dev',
    [string]$PublisherDisplayName = 'LabWidge',
    [string]$Output = 'dist/msix'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'LabWidge/LabWidge.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected version in LabWidge.csproj: $version" }
# The Store requires the fourth part of the version to be 0
$packageVersion = "$version.0"

$outputDir = Join-Path $repoRoot $Output
$layout = Join-Path $outputDir 'layout'
if (Test-Path -LiteralPath $layout) { Remove-Item -LiteralPath $layout -Recurse -Force }
New-Item -ItemType Directory -Path $layout -Force | Out-Null

# Self-contained: the Store cannot install the .NET Desktop Runtime the way Setup does
dotnet publish $project -c Release -r win-x64 --self-contained true -o $layout --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Building the app failed.' }

$assets = Join-Path $layout 'PackageAssets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$exe = Join-Path $layout 'LabWidge.exe'
# The package's logos, plus a 300 px one to upload as the Store listing's logo
$logos = @(
    @{ Path = (Join-Path $assets 'StoreLogo.png'); Size = 50 },
    @{ Path = (Join-Path $assets 'Square44x44Logo.png'); Size = 44 },
    @{ Path = (Join-Path $assets 'Square150x150Logo.png'); Size = 150 },
    @{ Path = (Join-Path $outputDir 'StoreListingLogo-300.png'); Size = 300 }
)
foreach ($logo in $logos) {
    # LabWidge is a Windows app, so PowerShell would not wait for it by itself
    $process = Start-Process -FilePath $exe -ArgumentList @('--write-png', $logo.Size, "`"$($logo.Path)`"") -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $logo.Path)) { throw "The app did not write $($logo.Path)." }
}

$manifest = Get-Content (Join-Path $repoRoot 'Packaging/AppxManifest.xml') -Raw
foreach ($value in @{ IdentityName = $IdentityName; Publisher = $Publisher; PublisherDisplayName = $PublisherDisplayName; Version = $packageVersion }.GetEnumerator()) {
    $manifest = $manifest.Replace("{$($value.Key)}", [Security.SecurityElement]::Escape($value.Value))
}
if ($manifest -match '\{[A-Za-z]+\}') { throw "The manifest still has an unfilled value: $($Matches[0])" }
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$makeAppx = Get-ChildItem $kits -Filter makeappx.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (-not $makeAppx) { throw 'makeappx.exe was not found. Install the Windows SDK.' }

$msix = Join-Path $outputDir "LabWidge-$version.msix"
& $makeAppx.FullName pack /d $layout /p $msix /o /h SHA256 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx could not pack the package.' }
Write-Host "Package $msix (version $packageVersion, identity $IdentityName)"

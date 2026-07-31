<#
.SYNOPSIS
    Builds Termyn's release artefacts: the installer and the portable zip.

.DESCRIPTION
    Publishes the app framework-dependent with ReadyToRun, then packages it two ways. Both need the
    .NET 10 Desktop Runtime on the target machine; neither bundles it, which is what keeps the
    download small and lets the runtime be serviced independently.

    The version comes from Directory.Build.props, so there is one place to change it and the
    installer, the executable and the release tag cannot disagree.

.PARAMETER SkipTests
    Package without running the test suite first. For iterating on the packaging itself.

.PARAMETER RegenerateIcon
    Redraw assets/termyn.ico from BrandIcon and exit, packaging nothing. Run this after changing the
    mark; the test that compares the committed file against the drawing will tell you when.

.EXAMPLE
    ./packaging/build.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$RegenerateIcon
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$app = Join-Path $root 'src/Termyn.App.Windows/Termyn.App.Windows.csproj'

if ($RegenerateIcon) {
    # Through the built library rather than a copy of the drawing code, so what lands in the file is
    # by construction what the tray draws.
    $platform = Join-Path $root 'src/Termyn.Platform.Windows/Termyn.Platform.Windows.csproj'
    dotnet build $platform -c Release --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not build Termyn.Platform.Windows.' }

    $dll = Join-Path $root 'src/Termyn.Platform.Windows/bin/Release/net10.0-windows/Termyn.Platform.Windows.dll'
    $icon = Join-Path $root 'assets/termyn.ico'
    Add-Type -Path $dll
    [Termyn.Platform.Windows.BrandIcon]::WriteIcoFile($icon, $null)

    Write-Host "Wrote $icon"
    return
}

function Get-ProductVersion {
    # Asked of MSBuild rather than parsed out of the props file, so this is the version that actually
    # stamped the assembly even if a project overrides it.
    $version = (dotnet msbuild $app -getProperty:Version -nologo).Trim()
    if (-not $version) { throw 'Could not read <Version> from the project.' }

    # Caught here rather than by the installer compiler, which rejects a pre-release VersionInfoVersion
    # with an error pointing at a line in the .iss — after the whole test suite has already run.
    if ($version -notmatch '^\d+(\.\d+){1,3}$') {
        throw "Version '$version' is not plain numbers; the installer cannot stamp it."
    }
    return $version
}

function Find-InnoSetup {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

$version = Get-ProductVersion
Write-Host "Termyn $version" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    dotnet test $root --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not packaging.' }
}

# A stale publish directory silently ships whatever the last build left in it.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Path $publish -Force | Out-Null

Write-Host 'Publishing...' -ForegroundColor Cyan
dotnet publish $app -c Release -o $publish --nologo --verbosity quiet -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$zip = Join-Path $artifacts "Termyn-$version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
Write-Host "  portable  $zip" -ForegroundColor Green

$iscc = Find-InnoSetup
if (-not $iscc) {
    Write-Warning 'Inno Setup 6 not found, so only the portable zip was built.'
    Write-Warning 'Install it with:  winget install JRSoftware.InnoSetup'
    return
}

Write-Host 'Building the installer...' -ForegroundColor Cyan
# Both facts passed in, so the script has no second home for either. What it publishes and
# what it packages cannot drift apart.
& $iscc "/DAppVersion=$version" "/DPublishDir=$publish" (Join-Path $PSScriptRoot 'Termyn.iss') | Out-String -Stream |
    Where-Object { $_ -match 'error|warning' } | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

$setup = Join-Path $artifacts "Termyn-$version-setup.exe"
Write-Host "  installer $setup" -ForegroundColor Green

Get-Item $zip, $setup | ForEach-Object {
    '{0,-40} {1,8:N0} KB' -f $_.Name, ($_.Length / 1KB)
}

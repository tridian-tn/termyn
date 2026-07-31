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

.EXAMPLE
    ./packaging/build.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$app = Join-Path $root 'src/Termyn.App.Windows/Termyn.App.Windows.csproj'

function Get-ProductVersion {
    # SelectNodes rather than property access: the file has several PropertyGroups, so the dotted
    # form returns an array whose Version member StrictMode refuses to resolve.
    $props = Join-Path $root 'Directory.Build.props'
    $xml = [xml](Get-Content $props)
    $node = $xml.SelectSingleNode('/Project/PropertyGroup/Version')
    if (-not $node) { throw "No <Version> in $props" }
    return $node.InnerText.Trim()
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
dotnet publish $app -c Release -o $publish --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# Symbols are for debugging a local build, not for a release download.
Get-ChildItem $publish -Filter *.pdb | Remove-Item -Force

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
& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot 'Termyn.iss') | Out-String -Stream |
    Where-Object { $_ -match 'error|warning' } | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

$setup = Join-Path $artifacts "Termyn-$version-setup.exe"
Write-Host "  installer $setup" -ForegroundColor Green

Get-Item $zip, $setup | ForEach-Object {
    '{0,-40} {1,8:N0} KB' -f $_.Name, ($_.Length / 1KB)
}

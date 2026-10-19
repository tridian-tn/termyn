<#
.SYNOPSIS
    Renders the repository's social preview, social-preview.png, from card.html.

.DESCRIPTION
    Takes a 1280x640 screenshot of the card with a headless Chromium browser — Edge by default,
    since every Windows machine has it. The card is a page rather than an image so the words and
    the mock window can be changed as text, and this turns it back into the picture GitHub wants.

    The result goes next to this script, overwriting the one that's there. Upload it by hand under
    the repository's Settings → General → Social preview: GitHub has no API for that.

.PARAMETER Browser
    The Chromium-based browser to render with. Left off, Edge from its usual install folder.

.EXAMPLE
    ./assets/social-preview/render.ps1
#>
[CmdletBinding()]
param(
    [string]$Browser
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$card = Join-Path $PSScriptRoot 'card.html'
$png = Join-Path $PSScriptRoot 'social-preview.png'

if (-not $Browser) {
    $Browser = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $Browser) { throw 'Edge is not where it usually is. Pass -Browser with the path to a Chromium browser.' }
}

# A profile of its own, so a browser already open with the user's profile doesn't take the request
# over and open a window instead of rendering.
$profileDir = Join-Path ([IO.Path]::GetTempPath()) "termyn-social-preview-$PID"

try {
    # Gone first, so the check below can't be answered by the last run's picture.
    Remove-Item $png -ErrorAction SilentlyContinue

    # Scale factor 1, or a high-DPI screen renders the card at twice the size GitHub asks for.
    & $Browser --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
        --window-size=1280,640 --user-data-dir="$profileDir" --screenshot="$png" ([Uri]$card).AbsoluteUri 2>$null | Out-Null

    if (-not (Test-Path $png)) { throw "The browser didn't write $png." }
    Write-Host "Wrote $png"
}
finally {
    Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
}

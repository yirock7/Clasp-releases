# SPDX-License-Identifier: GPL-3.0-or-later
# Renders one state of a Clasp artwork SVG to a PNG with headless Edge, using preview.html.
# Usage: render.ps1 -Svg <path> -Theta <deg> -Out <png> [-Colorway id] [-Dark]
param(
    [Parameter(Mandatory = $true)][string]$Svg,
    [Parameter(Mandatory = $true)][double]$Theta,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Colorway = '',
    [switch]$Dark
)
$ErrorActionPreference = 'Stop'

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge was not found.' }

$template = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'preview.html')
$svgText = Get-Content -Raw -LiteralPath $Svg
$block = '<script type="text/plain" id="art">' + $svgText + '</script>'
$html = $template.Replace('<!--ART-->', $block)

$work = Join-Path $env:TEMP ('clasp-artwork-preview-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $page = Join-Path $work 'preview.html'
    [IO.File]::WriteAllText($page, $html, (New-Object Text.UTF8Encoding($false)))
    $query = 'inline=1&clean=1&theta=' + $Theta.ToString([Globalization.CultureInfo]::InvariantCulture)
    if ($Colorway) { $query += '&colorway=' + [uri]::EscapeDataString($Colorway) }
    if ($Dark) { $query += '&bg=dark' }
    $url = ([uri]$page).AbsoluteUri + '?' + $query
    $outFull = [IO.Path]::GetFullPath($Out)
    $outDir = Split-Path -Parent $outFull
    if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
    if (Test-Path $outFull) { Remove-Item -LiteralPath $outFull }

    $edgeArgs = @(
        '--headless=new', '--disable-gpu', '--hide-scrollbars', '--window-size=460,400',
        "--user-data-dir=$(Join-Path $work 'profile')", '--no-first-run',
        "--screenshot=$outFull", $url
    )
    $p = Start-Process -FilePath $edge -ArgumentList $edgeArgs -PassThru -Wait -WindowStyle Hidden
    if (-not (Test-Path $outFull)) { throw "Edge did not write $outFull (exit code $($p.ExitCode))." }
    Write-Output $outFull
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
}

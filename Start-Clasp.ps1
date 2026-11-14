param(
    [switch]$Settings
)

$exePath = Join-Path $PSScriptRoot 'windows\hub\src\AppleDeviceHub.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\AppleDeviceHub.exe'

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    Write-Error @"
Clasp's Release build was not found at:
  $exePath

Build it first with:
  dotnet build `"$PSScriptRoot\windows\hub\src\AppleDeviceHub.App\AppleDeviceHub.App.csproj`" --configuration Release -p:Platform=x64
"@
    exit 1
}

$existing = Get-Process -Name 'AppleDeviceHub' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host 'Clasp is already running. The existing process was left untouched.'
    exit 0
}

$show = if ($Settings) { '--show=settings' } else { '--show=flyout' }
Start-Process -FilePath $exePath `
    -ArgumentList @('--transport=ble', $show) `
    -WorkingDirectory (Split-Path -Parent $exePath)

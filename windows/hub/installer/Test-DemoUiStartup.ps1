[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [switch]$AllowHeadless,
    [ValidateRange(5, 60)]
    [int]$TimeoutSeconds = 20,
    [ValidateRange(1, 10)]
    [int]$StableWindowSeconds = 2
)

$ErrorActionPreference = 'Stop'

if (-not ('ClaspStartupSmokeNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ClaspStartupSmokeNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
'@
}

function Get-FileHashOrMissing {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-LogTextAppendedAfter {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$StartOffset
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return ''
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $offset = if ($StartOffset -le $stream.Length) { $StartOffset } else { 0 }
        $stream.Position = $offset
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

$exe = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Demo UI smoke executable was not found: $exe"
}

$exeDirectory = Split-Path -Parent $exe
$appData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)
$settingsPath = Join-Path $appData 'Clasp\settings.json'
$logPath = Join-Path $appData 'Clasp\hub.log'
# On a machine that still has the folder from before the rename, Clasp moves it on first start. Take the baseline from the
# old settings file in that case, so the check still compares the same bytes before and after.
$legacySettingsPath = Join-Path $appData 'AppleDeviceHub\settings.json'
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf) -and (Test-Path -LiteralPath $legacySettingsPath -PathType Leaf)) {
    $settingsHashBefore = Get-FileHashOrMissing -Path $legacySettingsPath
}
else {
    $settingsHashBefore = Get-FileHashOrMissing -Path $settingsPath
}
$logStartOffset = if (Test-Path -LiteralPath $logPath -PathType Leaf) { (Get-Item -LiteralPath $logPath).Length } else { 0L }

$hostQualified = $false
$hostQualification = 'UNVERIFIED: could not read the Windows client/build/architecture details.'
try {
    $productType = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\ProductOptions' -Name ProductType -ErrorAction Stop).ProductType
    $buildNumber = [int](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name CurrentBuildNumber -ErrorAction Stop).CurrentBuildNumber
    $osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    $hostQualified = ($productType -eq 'WinNT' -and $buildNumber -ge 22000 -and $osArchitecture -eq 'X64')
    $hostQualification = if ($hostQualified) {
        'Windows 11 x64 client (build 22000 or later).'
    }
    else {
        'UNVERIFIED: this host is not a qualified Windows 11 x64 client (build 22000 or later).'
    }
}
catch {
    $hostQualified = $false
}

$existingProcesses = @(Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue)
if ($existingProcesses.Count -gt 0) {
    throw 'An Apple Device Hub process is already running. Close Clasp before running the offline startup smoke.'
}

$startedProcess = $null
$testFailure = $null
$cleanupFailure = $null
$windowVisible = $false
$windowWidth = 0
$windowHeight = 0
$status = 'FAIL'
$runtimeModulesAppLocal = $false
$loadedRuntimeModuleNames = @()

try {
    Write-Host 'Starting the published app in Demo transport (Bluetooth disabled)...'
    $startedProcess = Start-Process -FilePath $exe -ArgumentList @('--transport=demo', '--show=flyout') -WorkingDirectory $exeDirectory -WindowStyle Normal -PassThru

    $expectedExe = [System.IO.Path]::GetFullPath($exe)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $visibleSince = $null

    do {
        Start-Sleep -Milliseconds 250
        $process = Get-Process -Id $startedProcess.Id -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            $startedProcess.Refresh()
            throw "Demo UI process exited before opening a stable window (exit code $($startedProcess.ExitCode))."
        }

        if ([string]::IsNullOrWhiteSpace($process.Path) -or
            [System.IO.Path]::GetFullPath($process.Path) -ine $expectedExe) {
            throw 'The started process no longer points to the expected publish executable.'
        }

        $handle = $process.MainWindowHandle
        $candidateVisible = $false
        if ($handle -ne 0 -and [ClaspStartupSmokeNative]::IsWindowVisible($handle) -and
            -not [ClaspStartupSmokeNative]::IsIconic($handle)) {
            $rect = New-Object ClaspStartupSmokeNative+RECT
            if ([ClaspStartupSmokeNative]::GetWindowRect($handle, [ref]$rect)) {
                $windowWidth = $rect.Right - $rect.Left
                $windowHeight = $rect.Bottom - $rect.Top
                $candidateVisible = ($windowWidth -ge 200 -and $windowHeight -ge 200)
            }
        }

        if ($candidateVisible) {
            if ($null -eq $visibleSince) { $visibleSince = Get-Date }
            if (((Get-Date) - $visibleSince).TotalSeconds -ge $StableWindowSeconds) {
                $windowVisible = $true
                break
            }
        }
        else {
            $visibleSince = $null
        }
    } while ((Get-Date) -lt $deadline)

    $runLogText = Get-LogTextAppendedAfter -Path $logPath -StartOffset $logStartOffset
    if ($runLogText -match '(?im)Unhandled:|XamlParseException|Cannot locate resource') {
        throw 'The Demo UI startup log contains a new unhandled or XAML-resource error.'
    }

    if ($windowVisible) {
        $process = Get-Process -Id $startedProcess.Id -ErrorAction Stop
        $processModules = @(Get-Process -Id $startedProcess.Id -Module)
        $requiredRuntimeModuleNames = @('Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll')
        foreach ($moduleName in $requiredRuntimeModuleNames) {
            $module = $processModules | Where-Object { $_.ModuleName -ieq $moduleName } | Select-Object -First 1
            if ($null -eq $module -or [string]::IsNullOrWhiteSpace($module.FileName)) {
                throw "The running app did not expose its expected runtime module '$moduleName'."
            }
            $modulePath = [System.IO.Path]::GetFullPath($module.FileName)
            $modulePrefix = [System.IO.Path]::GetFullPath($exeDirectory).TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
            if (-not $modulePath.StartsWith($modulePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "The running app loaded '$moduleName' from outside its publish directory."
            }
            $loadedRuntimeModuleNames += $module.ModuleName
        }
        $runtimeModulesAppLocal = $true

        $status = if ($hostQualified -and $null -ne $settingsHashBefore) { 'PASS' } else { 'UNVERIFIED' }
        Write-Host "Visible Demo window held steady for $StableWindowSeconds seconds ($windowWidth x $windowHeight)."
        Write-Host "Loaded from publish directory: $($loadedRuntimeModuleNames -join ', ')."
    }
    elseif ($AllowHeadless -and (Get-Process -Id $startedProcess.Id -ErrorAction SilentlyContinue)) {
        $status = 'UNVERIFIED'
        Write-Warning 'The Demo process stayed alive with no new XAML error, but this environment did not expose a visible desktop window.'
    }
    else {
        throw 'Demo process stayed alive but no visible, non-minimized Clasp window appeared before the timeout.'
    }
}
catch {
    $testFailure = $_.Exception.Message
}
finally {
    if ($null -ne $startedProcess) {
        $process = Get-Process -Id $startedProcess.Id -ErrorAction SilentlyContinue
        if ($null -ne $process) {
            $expectedExe = [System.IO.Path]::GetFullPath($exe)
            if ([string]::IsNullOrWhiteSpace($process.Path) -or
                [System.IO.Path]::GetFullPath($process.Path) -ine $expectedExe) {
                $cleanupFailure = 'The owned process path changed; it was left untouched.'
            }
            else {
                [void]$process.CloseMainWindow()
                $process.WaitForExit(2000) | Out-Null
                $process = Get-Process -Id $startedProcess.Id -ErrorAction SilentlyContinue
                if ($null -ne $process) {
                    $actualExe = [System.IO.Path]::GetFullPath($process.Path)
                    if ($actualExe -ine $expectedExe) {
                        $cleanupFailure = 'The owned process path changed during cleanup; it was left untouched.'
                    }
                    else {
                        Stop-Process -Id $startedProcess.Id -ErrorAction Stop
                    }
                }
            }
        }
    }
}

if ($null -ne $cleanupFailure) {
    throw $cleanupFailure
}

$settingsHashAfter = Get-FileHashOrMissing -Path $settingsPath
$settingsBaselineMissing = $null -eq $settingsHashBefore
$settingsFileCreated = $settingsBaselineMissing -and $null -ne $settingsHashAfter
$settingsUnchanged = if ($settingsBaselineMissing) {
    -not $settingsFileCreated
}
else {
    [string]::Equals($settingsHashBefore, $settingsHashAfter, [System.StringComparison]::OrdinalIgnoreCase)
}
$settingsComparison = if ($settingsBaselineMissing -and $settingsFileCreated) {
    'No baseline settings file; first-run settings were created.'
}
elseif ($settingsBaselineMissing) {
    'No saved settings file existed before or after the smoke.'
}
else {
    'Existing settings file hash unchanged.'
}
if (-not $settingsUnchanged -and -not $settingsFileCreated) {
    throw 'The offline Demo UI smoke changed the saved settings file.'
}
if ($settingsFileCreated) {
    $status = 'UNVERIFIED'
}

$runLogText = Get-LogTextAppendedAfter -Path $logPath -StartOffset $logStartOffset
$newErrorCount = [regex]::Matches($runLogText, '(?im)Unhandled:|XamlParseException|Cannot locate resource').Count
if ($newErrorCount -gt 0) {
    throw 'The Demo UI startup log contains a new unhandled or XAML-resource error.'
}
if ($null -ne $testFailure) {
    throw $testFailure
}

[pscustomobject]@{
    Status = $status
    Transport = 'Demo (offline; Bluetooth disabled)'
    Host = $hostQualification
    HostQualified = $hostQualified
    WindowVisible = $windowVisible
    WindowSize = if ($windowVisible) { "${windowWidth}x${windowHeight}" } else { 'unavailable' }
    RuntimeModulesAppLocal = $runtimeModulesAppLocal
    LoadedRuntimeModules = $loadedRuntimeModuleNames -join ', '
    SettingsUnchanged = $settingsUnchanged
    SettingsComparison = $settingsComparison
    NewStartupErrorCount = $newErrorCount
}

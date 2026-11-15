[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [string]$OutputDirectory = '',
    [string]$CompilerPath = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$appProject = Join-Path $repoRoot 'windows\hub\src\AppleDeviceHub.App\AppleDeviceHub.App.csproj'
$installerScript = Join-Path $PSScriptRoot 'Clasp.iss'
$payloadValidator = Join-Path $PSScriptRoot 'Test-PublishedAppPayload.ps1'
$payloadGuardTests = Join-Path $PSScriptRoot 'tests\Test-PublishPayloadValidator.ps1'
$demoSmoke = Join-Path $PSScriptRoot 'Test-DemoUiStartup.ps1'
$numericVersion = ($Version -split '-', 2)[0]
$assemblyVersion = "$numericVersion.0"

foreach ($requiredScript in @($appProject, $installerScript, $payloadValidator, $payloadGuardTests, $demoSmoke)) {
    if (-not (Test-Path -LiteralPath $requiredScript -PathType Leaf)) {
        throw "Required installer build input was not found: '$requiredScript'."
    }
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $compilerCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($compilerCommand) {
        $CompilerPath = $compilerCommand.Source
    } else {
        $compilerCandidates = @()
        $programFilesX86 = [System.Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
        if ($programFilesX86) {
            $compilerCandidates += Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe'
        }
        if ($env:ProgramFiles) {
            $compilerCandidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
        }
        $CompilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Inno Setup 6 compiler not found. Pass -CompilerPath with the full path to ISCC.exe.'
}
$CompilerPath = [System.IO.Path]::GetFullPath($CompilerPath)

$dotnetCommand = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
if (-not $dotnetCommand) {
    throw 'The .NET 10 SDK was not found on PATH. Install the SDK listed in docs/TOOLCHAIN.md.'
}

# Native AOT links with the Visual Studio C++ tools. Their environment script runs vswhere.exe by bare name, which
# fails ("'vswhere.exe' is not recognized") when the Visual Studio Installer folder is not on PATH.
$visualStudioInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (-not (Get-Command 'vswhere.exe' -ErrorAction SilentlyContinue) -and
    (Test-Path -LiteralPath (Join-Path $visualStudioInstaller 'vswhere.exe') -PathType Leaf)) {
    $env:PATH = "$visualStudioInstaller;$env:PATH"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'artifacts'
}
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$buildRoot = Join-Path $tempRoot "clasp-installer-$([guid]::NewGuid().ToString('N'))"
$buildRoot = [System.IO.Path]::GetFullPath($buildRoot)
if (-not $buildRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Temporary build path '$buildRoot' is outside the system temporary folder."
}

$publishPath = Join-Path $buildRoot 'publish'
$compilerOutputPath = Join-Path $buildRoot 'compiler-output'
$installerName = "Clasp-$Version-win-x64-setup.exe"
$stagedInstaller = Join-Path $compilerOutputPath $installerName
$stagedPayloadManifest = "$stagedInstaller.payload.txt"
$finalInstaller = Join-Path $outputPath $installerName
$checksumPath = "$finalInstaller.sha256"
$payloadManifestPath = "$finalInstaller.payload.txt"
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)

try {
    [System.IO.Directory]::CreateDirectory($publishPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($compilerOutputPath) | Out-Null

    Write-Host 'Publishing Apple Device Hub as a self-contained x64 app...'
    $publishArguments = @(
        'publish', $appProject,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '-p:Platform=x64',
        '-p:WindowsAppSDKSelfContained=true',
        '-p:PublishSingleFile=false',
        ('-p:Version=' + $Version),
        ('-p:AssemblyVersion=' + $assemblyVersion),
        ('-p:FileVersion=' + $assemblyVersion),
        ('-p:InformationalVersion=' + $Version),
        '-o', $publishPath
    )
    & $dotnetCommand.Source @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'Clasp.exe') -PathType Leaf)) {
        throw "Publish completed without Clasp.exe in '$publishPath'."
    }

    Write-Host 'Checking the self-contained publish contract and required XAML resources...'
    $payloadReport = & $payloadValidator `
        -PublishDirectory $publishPath `
        -AppProjectPath $appProject `
        -InstallerScriptPath $installerScript `
        -OutputManifestPath $stagedPayloadManifest
    if ($payloadReport.Status -ne 'PASS') {
        throw 'Published app payload did not pass its installer contract.'
    }

    Write-Host 'Running positive and negative payload-guard fixtures...'
    & $payloadGuardTests -SourcePublishDirectory $publishPath

    Write-Host 'Running the offline Demo-mode startup smoke...'
    $smokeArguments = @{ ExePath = (Join-Path $publishPath 'Clasp.exe') }
    if ($env:GITHUB_ACTIONS -eq 'true') {
        # GitHub's hosted Windows runner is not a qualified Windows 11 client desktop.
        # Still launch Demo and fail on crashes/XAML errors; a missing visible desktop is UNVERIFIED.
        $smokeArguments.AllowHeadless = $true
    }
    $smokeReport = & $demoSmoke @smokeArguments
    $initialSettingsCreation = $smokeReport.Status -eq 'UNVERIFIED' -and
        $smokeReport.SettingsComparison -eq 'No baseline settings file; first-run settings were created.'
    if (($smokeReport.SettingsUnchanged -ne $true -and -not $initialSettingsCreation) -or
        $smokeReport.NewStartupErrorCount -ne 0) {
        throw 'The offline Demo-mode startup smoke changed settings or logged a startup error.'
    }
    if ($smokeReport.RuntimeModulesAppLocal -ne $true -and $smokeReport.Status -ne 'UNVERIFIED') {
        throw 'The offline Demo-mode startup smoke did not confirm app-local .NET and Windows App SDK modules.'
    }

    if ($env:GITHUB_OUTPUT) {
        "ui_smoke=$($smokeReport.Status)" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    }
    if ($env:GITHUB_STEP_SUMMARY) {
        $summary = @"
### Clasp Demo-mode startup smoke

- Status: $($smokeReport.Status)
- Host: $($smokeReport.Host)
- Visible window: $($smokeReport.WindowVisible) ($($smokeReport.WindowSize))
- Runtime modules loaded from publish folder: $($smokeReport.RuntimeModulesAppLocal)
- Existing settings unchanged: $($smokeReport.SettingsUnchanged)
- New startup/XAML errors: $($smokeReport.NewStartupErrorCount)

This smoke uses Demo transport with Bluetooth disabled. It is not a clean-machine or physical Bluetooth acceptance test.
"@
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8
    }
    Write-Host "Demo UI smoke status: $($smokeReport.Status); host: $($smokeReport.Host)"
    if ($smokeReport.Status -eq 'UNVERIFIED') {
        Write-Warning 'The app started without a new XAML error, but this host did not qualify for visible Windows 11 client UI acceptance.'
    }


    Write-Host 'Compiling the per-user installer with Inno Setup...'
    $versionDefine = '/DAppVersion=' + $Version
    $sourceDefine = '/DAppSourceDir=' + $publishPath
    $compilerArguments = @(
        ('/O' + $compilerOutputPath),
        $versionDefine,
        $sourceDefine,
        $installerScript
    )
    $compilerOutput = @(& $CompilerPath @compilerArguments 2>&1)
    $compilerExitCode = $LASTEXITCODE
    $compilerOutputLines = @($compilerOutput | ForEach-Object { $_.ToString() })
    $compilerLogPath = Join-Path $compilerOutputPath 'ISCC.log'
    [System.IO.File]::WriteAllLines($compilerLogPath, $compilerOutputLines, $utf8WithoutBom)
    if ($compilerExitCode -ne 0) {
        $compilerTail = ($compilerOutputLines | Select-Object -Last 40) -join [System.Environment]::NewLine
        throw "Inno Setup compilation failed with exit code $compilerExitCode.`n$compilerTail"
    }
    if (-not ($compilerOutputLines -match '(?i)Successful compile')) {
        throw 'Inno Setup did not report a successful compile.'
    }

    $criticalPayloadFiles = @(
        Get-ChildItem -LiteralPath $publishPath -Recurse -File -Filter '*.xbf'
        Get-Item -LiteralPath (Join-Path $publishPath 'Clasp.pri')
    )
    $compressLines = @($compilerOutputLines | Where-Object { $_ -match '^\s*Compressing:\s+' })
    foreach ($criticalFile in $criticalPayloadFiles) {
        $expectedSourcePath = [System.IO.Path]::GetFullPath($criticalFile.FullName)
        $wasCompiled = @($compressLines | Where-Object {
            $_.Trim().StartsWith("Compressing: $expectedSourcePath", [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
        if (-not $wasCompiled) {
            throw "Inno Setup did not confirm packaging required UI resource '$($criticalFile.FullName)'."
        }
    }
    if (-not (Test-Path -LiteralPath $stagedInstaller -PathType Leaf)) {
        throw "Inno Setup completed without producing '$stagedInstaller'."
    }

    Copy-Item -LiteralPath $stagedInstaller -Destination $finalInstaller -Force
    $hash = (Get-FileHash -LiteralPath $finalInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumText = "$hash  $installerName$([System.Environment]::NewLine)"
    [System.IO.File]::WriteAllText($checksumPath, $checksumText, $utf8WithoutBom)
    Copy-Item -LiteralPath $stagedPayloadManifest -Destination $payloadManifestPath -Force

    Write-Host "Installer: $finalInstaller"
    Write-Host "SHA-256:   $checksumPath"
    Write-Host "Payload:   $payloadManifestPath"
    Write-Host "Demo UI:   $($smokeReport.Status)"
} finally {
    if (Test-Path -LiteralPath $buildRoot) {
        $cleanupPath = [System.IO.Path]::GetFullPath($buildRoot)
        if ($cleanupPath.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            try {
                Remove-Item -LiteralPath $cleanupPath -Recurse -Force
            } catch {
                Write-Warning "Temporary build files remain at '$cleanupPath': $($_.Exception.Message)"
            }
        }
    }
}

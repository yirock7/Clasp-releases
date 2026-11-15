[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePublishDirectory
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.Parent.Parent.FullName
$validatorPath = Join-Path $repoRoot 'windows\hub\installer\Test-PublishedAppPayload.ps1'
$appProjectPath = Join-Path $repoRoot 'windows\hub\src\AppleDeviceHub.App\AppleDeviceHub.App.csproj'
$installerScriptPath = Join-Path $repoRoot 'windows\hub\installer\Clasp.iss'
$sourcePublishPath = [System.IO.Path]::GetFullPath($SourcePublishDirectory)

foreach ($requiredPath in @($validatorPath, $appProjectPath, $installerScriptPath, $sourcePublishPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required validator fixture input does not exist: $requiredPath"
    }
}
if (-not (Test-Path -LiteralPath $sourcePublishPath -PathType Container)) {
    throw "Source publish input is not a directory: $sourcePublishPath"
}

$appDirectory = Split-Path -Parent $appProjectPath
$requiredRootFiles = @(
    'Clasp.exe',
    'Clasp.pri',
    'Microsoft.ui.xaml.dll',
    'Microsoft.WindowsAppRuntime.dll',
    'Microsoft.WindowsAppRuntime.Bootstrap.dll',
    'Assets\AppIcon.ico',
    'Assets\ClaspLogo.png'
)

$sourceXamlFiles = @(
    Get-ChildItem -LiteralPath $appDirectory -Recurse -File -Filter '*.xaml' |
        Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' } |
        Sort-Object FullName
)
if ($sourceXamlFiles.Count -eq 0) {
    throw "No source XAML files were found under '$appDirectory'."
}

$sourceXbfRelativePaths = [System.Collections.Generic.List[string]]::new()
foreach ($xamlFile in $sourceXamlFiles) {
    $relativeXamlPath = $xamlFile.FullName.Substring($appDirectory.TrimEnd([char[]]@('\', '/')).Length).TrimStart([char[]]@('\', '/'))
    $sourceXbfRelativePaths.Add([System.IO.Path]::ChangeExtension($relativeXamlPath, '.xbf'))
}

$tempRoot = if (-not [string]::IsNullOrWhiteSpace($env:TEMP)) { $env:TEMP } else { [System.IO.Path]::GetTempPath() }
$tempRoot = [System.IO.Path]::GetFullPath($tempRoot)
$fixtureRoot = Join-Path $tempRoot ('Clasp-payload-validator-' + [Guid]::NewGuid().ToString('N'))
$validFixturePath = Join-Path $fixtureRoot 'valid'
$tempRootPrefix = $tempRoot.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
if (-not $fixtureRoot.StartsWith($tempRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Fixture cleanup target is outside the temporary directory: $fixtureRoot"
}
if (Test-Path -LiteralPath $fixtureRoot) {
    throw "The temporary fixture directory already exists: $fixtureRoot"
}

function Copy-ValidFixture {
    param([Parameter(Mandatory = $true)][string]$DestinationPath)

    [void][System.IO.Directory]::CreateDirectory($DestinationPath)
    foreach ($relativePath in $requiredRootFiles) {
        $sourcePath = Join-Path $sourcePublishPath $relativePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Source publish is missing a validator-required file: $relativePath"
        }
        $targetPath = Join-Path $DestinationPath $relativePath
        [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $targetPath))
        Copy-Item -LiteralPath $sourcePath -Destination $targetPath
    }

    foreach ($relativePath in $sourceXbfRelativePaths) {
        $sourcePath = Join-Path $sourcePublishPath $relativePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Source publish is missing the XAML resource for '$relativePath'."
        }
        $targetPath = Join-Path $DestinationPath $relativePath
        $destinationDirectory = Split-Path -Parent $targetPath
        [void][System.IO.Directory]::CreateDirectory($destinationDirectory)
        Copy-Item -LiteralPath $sourcePath -Destination $targetPath
    }
}

function Invoke-PayloadValidator {
    param([Parameter(Mandatory = $true)][string]$PublishPath)

    try {
        $null = & $validatorPath `
            -PublishDirectory $PublishPath `
            -AppProjectPath $appProjectPath `
            -InstallerScriptPath $installerScriptPath
        return [pscustomobject]@{ Passed = $true; Error = '' }
    }
    catch {
        return [pscustomobject]@{ Passed = $false; Error = $_.Exception.Message }
    }
}

function Assert-ValidationResult {
    param(
        [Parameter(Mandatory = $true)][string]$CaseName,
        [Parameter(Mandatory = $true)][string]$PublishPath,
        [Parameter(Mandatory = $true)][bool]$ShouldPass,
        [string]$ExpectedErrorPattern = ''
    )

    $result = Invoke-PayloadValidator -PublishPath $PublishPath
    if ($result.Passed -ne $ShouldPass) {
        if ($ShouldPass) {
            throw "Validator rejected the valid fixture '$CaseName': $($result.Error)"
        }
        throw "Validator accepted invalid fixture '$CaseName'."
    }
    if (-not $ShouldPass -and $ExpectedErrorPattern -and $result.Error -notmatch $ExpectedErrorPattern) {
        throw "Validator rejected '$CaseName' for an unexpected reason: $($result.Error)"
    }

    if ($ShouldPass) {
        Write-Output "PASS: valid minimal publish payload"
    }
    else {
        Write-Output "PASS: rejected $CaseName"
    }
}

try {
    [void][System.IO.Directory]::CreateDirectory($fixtureRoot)
    Copy-ValidFixture -DestinationPath $validFixturePath
    Assert-ValidationResult -CaseName 'valid payload' -PublishPath $validFixturePath -ShouldPass $true

    $priFixturePath = Join-Path $fixtureRoot 'missing-pri'
    Copy-ValidFixture -DestinationPath $priFixturePath
    Remove-Item -LiteralPath (Join-Path $priFixturePath 'Clasp.pri') -Force
    Assert-ValidationResult -CaseName 'missing Clasp.pri' -PublishPath $priFixturePath -ShouldPass $false -ExpectedErrorPattern 'Clasp\.pri'

    $flyoutXaml = Join-Path $appDirectory 'Views\FlyoutView.xaml'
    if (-not (Test-Path -LiteralPath $flyoutXaml -PathType Leaf)) {
        throw "The source project no longer contains the FlyoutView XAML required by this fixture: $flyoutXaml"
    }
    $relativeFlyoutXbf = [System.IO.Path]::ChangeExtension(
        $flyoutXaml.Substring($appDirectory.TrimEnd([char[]]@('\', '/')).Length).TrimStart([char[]]@('\', '/')),
        '.xbf')
    $xbfFixturePath = Join-Path $fixtureRoot 'missing-flyout-xbf'
    Copy-ValidFixture -DestinationPath $xbfFixturePath
    Remove-Item -LiteralPath (Join-Path $xbfFixturePath $relativeFlyoutXbf) -Force
    Assert-ValidationResult -CaseName 'missing FlyoutView XBF' -PublishPath $xbfFixturePath -ShouldPass $false -ExpectedErrorPattern 'FlyoutView\.xbf'

    $xamlRuntimeFixturePath = Join-Path $fixtureRoot 'missing-xaml-runtime'
    Copy-ValidFixture -DestinationPath $xamlRuntimeFixturePath
    Remove-Item -LiteralPath (Join-Path $xamlRuntimeFixturePath 'Microsoft.ui.xaml.dll') -Force
    Assert-ValidationResult -CaseName 'missing Microsoft.ui.xaml.dll' -PublishPath $xamlRuntimeFixturePath -ShouldPass $false -ExpectedErrorPattern 'Microsoft\.ui\.xaml\.dll'

    $logoFixturePath = Join-Path $fixtureRoot 'missing-logo'
    Copy-ValidFixture -DestinationPath $logoFixturePath
    Remove-Item -LiteralPath (Join-Path $logoFixturePath 'Assets\ClaspLogo.png') -Force
    Assert-ValidationResult -CaseName 'missing ClaspLogo.png' -PublishPath $logoFixturePath -ShouldPass $false -ExpectedErrorPattern 'ClaspLogo\.png'

    $jitRuntimeFixturePath = Join-Path $fixtureRoot 'jit-runtime'
    Copy-ValidFixture -DestinationPath $jitRuntimeFixturePath
    Set-Content -LiteralPath (Join-Path $jitRuntimeFixturePath 'coreclr.dll') -Value 'fixture-only JIT runtime' -NoNewline
    Assert-ValidationResult -CaseName 'JIT runtime in a Native AOT payload' -PublishPath $jitRuntimeFixturePath -ShouldPass $false -ExpectedErrorPattern 'coreclr\.dll'

    $driverFixturePath = Join-Path $fixtureRoot 'driver-file'
    Copy-ValidFixture -DestinationPath $driverFixturePath
    Set-Content -LiteralPath (Join-Path $driverFixturePath 'unexpected.sys') -Value 'fixture-only forbidden driver file' -NoNewline
    Assert-ValidationResult -CaseName 'driver file in payload' -PublishPath $driverFixturePath -ShouldPass $false -ExpectedErrorPattern 'driver-free installer payload'

    $wrongArchitectureFixturePath = Join-Path $fixtureRoot 'wrong-architecture'
    Copy-ValidFixture -DestinationPath $wrongArchitectureFixturePath
    $executablePath = Join-Path $wrongArchitectureFixturePath 'Clasp.exe'
    $stream = [System.IO.File]::Open($executablePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw 'The source executable is not a PE file, so the wrong-architecture fixture cannot be created.'
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt ($stream.Length - 6)) {
            throw 'The source executable has an invalid PE header, so the wrong-architecture fixture cannot be created.'
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw 'The source executable has an invalid PE signature, so the wrong-architecture fixture cannot be created.'
        }
        $stream.Position = $peOffset + 4
        $x86Machine = [System.BitConverter]::GetBytes([UInt16]0x014C)
        $stream.Write($x86Machine, 0, $x86Machine.Length)
    }
    finally {
        $stream.Dispose()
    }
    Assert-ValidationResult -CaseName 'wrong-architecture app executable' -PublishPath $wrongArchitectureFixturePath -ShouldPass $false -ExpectedErrorPattern 'must be native x64'

    Write-Output 'Publish payload validator fixture suite passed (1 valid and 5 invalid cases).'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

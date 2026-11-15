[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [Parameter(Mandatory = $true)]
    [string]$AppProjectPath,
    [Parameter(Mandatory = $true)]
    [string]$InstallerScriptPath,
    [string]$OutputManifestPath = ''
)

$ErrorActionPreference = 'Stop'

function Stop-PayloadValidation {
    param([Parameter(Mandatory = $true)][string]$Message)
    throw [System.InvalidOperationException]::new($Message)
}

function Assert-NonEmptyFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Stop-PayloadValidation "Required publish file is missing: $Path"
    }

    if ((Get-Item -LiteralPath $Path).Length -le 0) {
        Stop-PayloadValidation "Required publish file is empty: $Path"
    }
}

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            Stop-PayloadValidation "The app executable is not a Windows PE file: $Path"
        }

        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt ($stream.Length - 6)) {
            Stop-PayloadValidation "The app executable has an invalid PE header: $Path"
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            Stop-PayloadValidation "The app executable has an invalid PE signature: $Path"
        }

        return $reader.ReadUInt16()
    }
    finally {
        $stream.Dispose()
    }
}

$publishPath = [System.IO.Path]::GetFullPath($PublishDirectory)
$appProject = [System.IO.Path]::GetFullPath($AppProjectPath)
$installerScript = [System.IO.Path]::GetFullPath($InstallerScriptPath)

foreach ($requiredPath in @($publishPath, $appProject, $installerScript)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        Stop-PayloadValidation "Validation input does not exist: $requiredPath"
    }
}
if (-not (Test-Path -LiteralPath $publishPath -PathType Container)) {
    Stop-PayloadValidation "Publish path is not a directory: $publishPath"
}

[xml]$projectXml = Get-Content -LiteralPath $appProject -Raw
$expectedProjectProperties = @{
    RuntimeIdentifier = 'win-x64'
    SelfContained = 'true'
    WindowsAppSDKSelfContained = 'true'
    WindowsPackageType = 'None'
}
foreach ($propertyName in $expectedProjectProperties.Keys) {
    $expectedValue = $expectedProjectProperties[$propertyName]
    $node = $projectXml.SelectSingleNode("/Project/PropertyGroup/$propertyName")
    if ($null -eq $node -or $node.InnerText.Trim() -ine $expectedValue) {
        Stop-PayloadValidation "The app project must set $propertyName to '$expectedValue' for the supported installer build."
    }
}

$installerText = Get-Content -LiteralPath $installerScript -Raw
if ($installerText -notmatch '(?im)^\s*MinVersion\s*=\s*10\.0\.22000\s*$') {
    Stop-PayloadValidation 'The installer must enforce the Windows 11 build 22000 minimum.'
}
if ($installerText -notmatch '(?im)^\s*ArchitecturesAllowed\s*=\s*x64os\s*$' -or
    $installerText -notmatch '(?im)^\s*ArchitecturesInstallIn64BitMode\s*=\s*x64os\s*$') {
    Stop-PayloadValidation 'The installer must be restricted to native x64 Windows.'
}
if ($installerText -notmatch '(?i)function\s+InitializeSetup\s*:\s*Boolean' -or
    $installerText -notmatch '(?i)GetWindowsVersionEx\s*\(\s*Version\s*\)' -or
    $installerText -notmatch '(?i)ProductType\s*=\s*VER_NT_WORKSTATION') {
    Stop-PayloadValidation 'The installer must reject Windows Server and require a Windows client SKU.'
}
if ($installerText -notmatch '(?i)Windows 11 \(64-bit\)') {
    Stop-PayloadValidation 'The installer must explain that Windows 11 (64-bit) is required.'
}
if ($installerText -notmatch '(?im)^\s*PrivilegesRequired\s*=\s*lowest\s*$') {
    Stop-PayloadValidation 'The installer must remain per-user and must not request administrator privileges.'
}
if ($installerText -notmatch '(?im)^\s*Source:\s*"\{#AppSourceDir\}\\\*";\s*DestDir:\s*"\{app\}";\s*Flags:\s*ignoreversion\s+recursesubdirs\s+createallsubdirs\s*$') {
    Stop-PayloadValidation 'The installer must recursively include the full publish directory.'
}

$publishPrefix = $publishPath.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
$appDirectory = Split-Path -Parent $appProject
$sourceXamlFiles = @(
    Get-ChildItem -LiteralPath $appDirectory -Recurse -File -Filter '*.xaml' |
        Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' } |
        Sort-Object FullName
)
if ($sourceXamlFiles.Count -eq 0) {
    Stop-PayloadValidation "No source XAML files were found under '$appDirectory'."
}

$expectedXbfPaths = [System.Collections.Generic.List[string]]::new()
foreach ($xamlFile in $sourceXamlFiles) {
    $relativeXamlPath = $xamlFile.FullName.Substring($appDirectory.Length).TrimStart([char[]]@('\', '/'))
    $relativeXbfPath = [System.IO.Path]::ChangeExtension($relativeXamlPath, '.xbf')
    $xbfPath = [System.IO.Path]::GetFullPath((Join-Path $publishPath $relativeXbfPath))
    if (-not $xbfPath.StartsWith($publishPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        Stop-PayloadValidation "Source XAML maps outside the publish directory: $relativeXamlPath"
    }
    Assert-NonEmptyFile -Path $xbfPath
    $expectedXbfPaths.Add($xbfPath)
}

$appPriPath = Join-Path $publishPath 'Clasp.pri'
Assert-NonEmptyFile -Path $appPriPath

$requiredRuntimeFiles = @(
    'Clasp.exe',
    'Microsoft.ui.xaml.dll',
    'Microsoft.WindowsAppRuntime.dll',
    'Microsoft.WindowsAppRuntime.Bootstrap.dll'
)
foreach ($relativePath in $requiredRuntimeFiles) {
    Assert-NonEmptyFile -Path (Join-Path $publishPath $relativePath)
}

# The app is compiled with Native AOT: a .NET runtime (coreclr.dll) in the payload means a JIT build was packaged.
if (Test-Path -LiteralPath (Join-Path $publishPath 'coreclr.dll')) {
    Stop-PayloadValidation 'The payload contains coreclr.dll; expected a Native AOT publish with no .NET runtime files.'
}

$appExecutable = Join-Path $publishPath 'Clasp.exe'
$peMachine = Get-PeMachine -Path $appExecutable
if ($peMachine -ne 0x8664) {
    Stop-PayloadValidation ('The app executable must be native x64 (PE machine 0x8664); found 0x{0:X4}.' -f $peMachine)
}

$forbiddenExtensions = @('.sys', '.inf', '.cat', '.pfx', '.p12', '.snk', '.cer', '.crt')
$publishedFiles = @(Get-ChildItem -LiteralPath $publishPath -Recurse -File -Force | Sort-Object FullName)
$forbiddenFiles = @($publishedFiles | Where-Object { $forbiddenExtensions -contains $_.Extension.ToLowerInvariant() })
if ($forbiddenFiles.Count -gt 0) {
    $relativeForbidden = $forbiddenFiles | ForEach-Object { $_.FullName.Substring($publishPath.Length + 1) }
    Stop-PayloadValidation ('The driver-free installer payload contains forbidden files: ' + ($relativeForbidden -join ', '))
}

$licensePath = Join-Path $PSScriptRoot '..\..\..\LICENSE'
$licensePath = [System.IO.Path]::GetFullPath($licensePath)
Assert-NonEmptyFile -Path $licensePath

if (-not [string]::IsNullOrWhiteSpace($OutputManifestPath)) {
    $manifestPath = [System.IO.Path]::GetFullPath($OutputManifestPath)
    $manifestDirectory = Split-Path -Parent $manifestPath
    [System.IO.Directory]::CreateDirectory($manifestDirectory) | Out-Null

    $manifestLines = [System.Collections.Generic.List[string]]::new()
    $manifestLines.Add('Clasp self-contained x64 publish payload')
    $manifestLines.Add('Required XAML resources: {0} XBF files plus Clasp.pri.' -f $expectedXbfPaths.Count)
    $manifestLines.Add('Publish files: {0}; LICENSE is added by the installer.' -f $publishedFiles.Count)
    $manifestLines.Add('')
    $manifestLines.Add("Relative path`tBytes`tSHA-256")
    foreach ($file in $publishedFiles) {
        $relativePath = $file.FullName.Substring($publishPath.Length + 1)
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifestLines.Add(("{0}`t{1}`t{2}" -f $relativePath, $file.Length, $hash))
    }
    $licenseHash = (Get-FileHash -LiteralPath $licensePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestLines.Add(("LICENSE`t{0}`t{1}" -f (Get-Item -LiteralPath $licensePath).Length, $licenseHash))
    [System.IO.File]::WriteAllLines($manifestPath, $manifestLines, [System.Text.UTF8Encoding]::new($false))
}

[pscustomobject]@{
    Status = 'PASS'
    Architecture = 'x64 (PE 0x8664)'
    MinimumWindows = 'Windows 11 build 22000, workstation SKU'
    Runtime = '.NET and Windows App SDK self-contained'
    XbfCount = $expectedXbfPaths.Count
    Pri = 'Clasp.pri'
    PublishFileCount = $publishedFiles.Count
    ForbiddenFileCount = $forbiddenFiles.Count
}

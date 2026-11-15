<#
.SYNOPSIS
Builds an UNSIGNED x64 AAP driver submission candidate. Never installs or signs it.
.DESCRIPTION
Uses the installed MSVC compiler and pinned, signature-verified Microsoft SDK/WDK
NuGet packages. Does not install a WDK/VS extension or change machine configuration.
Compiles with /kernel and links the documented KMDF entry/libraries; the existing
vcxproj remains available to developers with the WDK Visual Studio integration.
#>
[CmdletBinding()]
param(
    [string]$CacheRoot = (Join-Path $PSScriptRoot '.build'),
    [string]$OutputRoot = '',
    [switch]$Restore
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
if (-not $OutputRoot) { $OutputRoot = Join-Path $CacheRoot ('candidate-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'Use a fresh OutputRoot; existing artifacts are never overwritten.' }
New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
$nuget = Join-Path $CacheRoot 'nuget.exe'
$nugetHash = '92DBED160DDEE0F64B901E907439E021211B428E57C089ECC12FC38DCC4BD9A5'
$packages = @(
    @{ Id='Microsoft.Windows.WDK.x64'; Version='10.0.28000.2526'; Hash='63C939FB5A79295BF40E941DB592681272219B04EDFF095FE2F3D123E5579A90' },
    @{ Id='Microsoft.Windows.SDK.CPP'; Version='10.0.28000.1721'; Hash='722EF3C9780FFDB0966A4AD28871BDE0B37594553652F939C79DD96EA12D2E9E' },
    @{ Id='Microsoft.Windows.SDK.CPP.x64'; Version='10.0.28000.1721'; Hash='FBDAE03F674D84728B3456AE8A0745D1375DA8204EFD50BDD3BDC257425EA3D1' }
)
$savedEnv = @{}
foreach ($key in @('NUGET_HTTP_CACHE_PATH','NUGET_PLUGINS_CACHE_PATH','NUGET_SCRATCH','NUGET_PACKAGES')) {
    $savedEnv[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, (Join-Path $CacheRoot $key.ToLowerInvariant()), 'Process')
}
function Invoke-Checked([string]$Tool, [string[]]$Arguments) {
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Tool failed with exit ${LASTEXITCODE}: $Tool" }
}
function Get-SourceHashes {
    $hashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in '.c','.h','.inf','.ps1','.vcxproj' }) {
        $hashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $hashes
}
try {
    if (-not (Test-Path -LiteralPath $nuget)) {
        if (-not $Restore) { throw 'NuGet is missing. Re-run with -Restore to acquire pinned build dependencies.' }
        Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/v6.14.0/nuget.exe' -OutFile $nuget
    }
    if ((Get-FileHash -LiteralPath $nuget -Algorithm SHA256).Hash -ne $nugetHash) { throw 'NuGet hash mismatch.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $nuget
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'NuGet publisher verification failed.' }
    $packageRoot = Join-Path $CacheRoot 'packages'
    foreach ($p in $packages) {
        $idVersion = $p.Id + '.' + $p.Version
        $nupkg = Join-Path $packageRoot "$idVersion\$idVersion.nupkg"
        if (-not (Test-Path -LiteralPath $nupkg)) {
            if (-not $Restore) { throw "Missing $idVersion. Re-run with -Restore." }
            Invoke-Checked $nuget @('install',$p.Id,'-Version',$p.Version,'-OutputDirectory',$packageRoot,'-Source','https://api.nuget.org/v3/index.json','-DirectDownload','-NoHttpCache','-NonInteractive')
        }
        if ((Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash -ne $p.Hash) { throw "Package hash mismatch: $idVersion" }
        Invoke-Checked $nuget @('verify','-All',$nupkg,'-Verbosity','quiet')
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ Build Tools are required.' }
    $vsRoot = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
    if (-not $vsRoot) { throw 'No installed MSVC x64 compiler was found.' }
    $compilerVersion = (Get-ChildItem -LiteralPath (Join-Path $vsRoot 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
    $compilerRoot = Join-Path $vsRoot "VC\Tools\MSVC\$compilerVersion"
    $cl = Join-Path $compilerRoot 'bin\Hostx64\x64\cl.exe'
    $link = Join-Path $compilerRoot 'bin\Hostx64\x64\link.exe'
    $wdk = Join-Path $packageRoot 'Microsoft.Windows.WDK.x64.10.0.28000.2526\c'
    $sdk = Join-Path $packageRoot 'Microsoft.Windows.SDK.CPP.10.0.28000.1721\c'
    $includePaths = @(
        (Join-Path $wdk 'Include\10.0.28000.0\km'),
        (Join-Path $wdk 'Include\10.0.28000.0\shared'),
        (Join-Path $wdk 'Include\wdf\kmdf\1.15'),
        (Join-Path $sdk 'Include\10.0.28000.0\shared'),
        (Join-Path $sdk 'Include\10.0.28000.0\ucrt'),
        (Join-Path $compilerRoot 'include')
    )
    $packageDir = Join-Path $OutputRoot 'ClaspAAP'
    $objectDir = Join-Path $OutputRoot 'obj'
    New-Item -ItemType Directory -Path $packageDir,$objectDir -Force | Out-Null
    $sourceHashes = Get-SourceHashes
    $compileArgs = @('/nologo','/c','/kernel','/GS','/guard:cf','/Gy','/Z7','/W4','/WX','/TC','/O2','/Zl',
        '/D_AMD64_','/DNDEBUG','/DKMDF_VERSION_MAJOR=1','/DKMDF_VERSION_MINOR=15','/external:W0',
        '/DWINVER=0x0A00','/D_WIN32_WINNT=0x0A00','/DNTDDI_VERSION=0x0A00000C',('/Fo' + $objectDir + '\'))
    # Keep /W4 /WX for our C; older WDF headers intentionally contain padded structs.
    foreach ($path in $includePaths) { $compileArgs += '/external:I' + $path }
    foreach ($name in @('Driver.c','Device.c','L2cap.c','Ioctl.c')) { $compileArgs += Join-Path $PSScriptRoot $name }
    Invoke-Checked $cl $compileArgs
    $driverBinary = Join-Path $packageDir 'NTPodsAAP.sys'
    $pdb = Join-Path $packageDir 'NTPodsAAP.pdb'
    $linkArgs = @('/nologo','/DRIVER','/SUBSYSTEM:NATIVE,10.00','/ENTRY:FxDriverEntry','/MACHINE:X64','/NODEFAULTLIB',
        '/INCREMENTAL:NO','/RELEASE','/DYNAMICBASE','/NXCOMPAT','/GUARD:CF','/INTEGRITYCHECK','/DEBUG:FULL','/Brepro',
        ('/OUT:' + $driverBinary),('/PDB:' + $pdb),
        ('/LIBPATH:' + (Join-Path $wdk 'Lib\10.0.28000.0\km\x64')),
        ('/LIBPATH:' + (Join-Path $wdk 'Lib\wdf\kmdf\x64\1.15')),
        'ntoskrnl.lib','hal.lib','wmilib.lib','wdmsec.lib','BufferOverflowFastFailK.lib','WdfDriverEntry.lib','WdfLdr.lib')
    foreach ($name in @('Driver','Device','L2cap','Ioctl')) { $linkArgs += Join-Path $objectDir ($name + '.obj') }
    Invoke-Checked $link $linkArgs
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NTPodsAAP.inf') -Destination $packageDir
    $infverif = Join-Path $wdk 'tools\10.0.28000.0\x64\infverif.exe'
    Invoke-Checked $infverif @('/w','/v',(Join-Path $packageDir 'NTPodsAAP.inf'))
    $inf2cat = Join-Path $wdk 'bin\10.0.28000.0\x86\Inf2Cat.exe'
    Invoke-Checked $inf2cat @(('/driver:' + $packageDir),'/os:10_GE_X64','/uselocaltime','/verbose')
    $afterHashes = Get-SourceHashes
    if ($afterHashes.Count -ne $sourceHashes.Count) { throw 'Source files changed during the build; discard this incomplete output and rebuild.' }
    foreach ($name in $sourceHashes.Keys) {
        if ($afterHashes[$name] -ne $sourceHashes[$name]) { throw 'Source content changed during the build; discard this incomplete output and rebuild.' }
    }
    $artifactHashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $packageDir -File) { $artifactHashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    $manifest = [ordered]@{
        schema_version=1; status='UNSIGNED_SUBMISSION_CANDIDATE'; created_at_utc=[DateTime]::UtcNow.ToString('o')
        compiler_version=$compilerVersion; wdk_version='10.0.28000.2526'; sdk_version='10.0.28000.1721'; kmdf_version='1.15'
        source_file_sha256=$sourceHashes; artifact_sha256=$artifactHashes
        validation=@{compile='PASS';link='PASS';infverif_windows_driver='PASS';inf2cat='PASS';microsoft_signature='NOT_PRESENT';hardware='NOT_RUN'}
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'build-manifest.json') -Encoding UTF8
    # CAB preparation is deliberately separate from private-key signing and submission.
    $cabPath = Join-Path $OutputRoot 'ClaspAAP-unsigned.cab'
    $ddf = @('.OPTION EXPLICIT','.Set Cabinet=on','.Set Compress=on','.Set CompressionType=MSZIP',
        '.Set CabinetNameTemplate=ClaspAAP-unsigned.cab',('.Set DiskDirectoryTemplate="' + $OutputRoot + '"'),'.Set DestinationDir=ClaspAAP')
    foreach ($file in Get-ChildItem -LiteralPath $packageDir -File | Sort-Object Name) {
        if ($file.FullName -match '["\r\n]') { throw 'Invalid CAB input path.' }
        $ddf += '"' + $file.FullName + '"'
    }
    $ddfPath = Join-Path $OutputRoot 'submission.ddf'
    $ddf | Set-Content -LiteralPath $ddfPath -Encoding ASCII
    Push-Location -LiteralPath $OutputRoot
    try { Invoke-Checked (Join-Path $env:WINDIR 'System32\makecab.exe') @('/F',$ddfPath) }
    finally { Pop-Location }
    (Get-FileHash -LiteralPath $cabPath -Algorithm SHA256) | Format-List
    "UNSIGNED submission candidate prepared at $OutputRoot. It is NOT installable under the selected normal-security route."
} finally {
    foreach ($key in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnv[$key], 'Process') }
}

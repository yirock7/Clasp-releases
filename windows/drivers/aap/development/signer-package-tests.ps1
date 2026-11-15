[CmdletBinding()]
param(
    [string]$ScriptPath = '',
    [string]$SourcePackageDirectory = (Join-Path $env:LOCALAPPDATA 'Clasp\build-cache\development-driver\unsigned-current\ClaspAAP'),
    [string]$SourceBuildManifestPath = (Join-Path $env:LOCALAPPDATA 'Clasp\build-cache\development-driver\unsigned-current\build-manifest.json'),
    [string]$CacheRoot = (Join-Path $env:LOCALAPPDATA 'Clasp\build-cache'),
    [string]$SignedTestSysPath = '',
    [string]$SignedTestCatalogPath = '',
    [string]$SignerThumbprint = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $ScriptPath) { $ScriptPath = Join-Path $PSScriptRoot 'New-TestSignedPackage.ps1' }
$ScriptPath = [IO.Path]::GetFullPath($ScriptPath)
$SourcePackageDirectory = [IO.Path]::GetFullPath($SourcePackageDirectory)
$SourceBuildManifestPath = [IO.Path]::GetFullPath($SourceBuildManifestPath)
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
$developmentRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$testRoot = Join-Path $developmentRoot ('signer-test-temp-' + [Guid]::NewGuid().ToString('N'))
$plannedOutput = Join-Path (Split-Path -Parent (Split-Path -Parent $SourcePackageDirectory)) ('signer-plan-only-' + [Guid]::NewGuid().ToString('N'))

function Assert-Condition {
    param([Parameter(Mandatory = $true)][bool]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$MessagePattern,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )
    try {
        & $Action | Out-Null
    }
    catch {
        if ($_.Exception.Message -match $MessagePattern) { return }
        throw "Unexpected failure. Expected '$MessagePattern', received '$($_.Exception.Message)'"
    }
    throw $FailureMessage
}

function Get-CurrentUserMyThumbprints {
    return @(Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My' | ForEach-Object { $_.Thumbprint } | Sort-Object)
}

$storeBefore = @(Get-CurrentUserMyThumbprints)
New-Item -ItemType Directory -Path $testRoot -ErrorAction Stop | Out-Null
try {
    . $ScriptPath -PlanOnly -OutputDirectory $plannedOutput | Out-Null
    Assert-Condition -Condition (-not (Test-Path -LiteralPath $plannedOutput)) -Message 'PlanOnly unexpectedly created its output path.'

    $source = Assert-SourcePackage -PackageDirectory $SourcePackageDirectory -BuildManifestPath $SourceBuildManifestPath
    Assert-Condition -Condition ($source.package_file_sha256['NTPodsAAP.sys'] -eq '48287DB2424B299EDEE438C1FBD55AD8EE06208CC4A7D98B21E415AB26AC0A42') -Message 'Pinned source SYS hash changed.'

    $wdkVersion = [string](Get-ObjectProperty $source.build_manifest 'wdk_version')
    $sdkVersion = [string](Get-ObjectProperty $source.build_manifest 'sdk_version')
    $wdkRoot = Join-Path $CacheRoot ("packages\Microsoft.Windows.WDK.x64.$wdkVersion\c")
    $sdkRoot = Join-Path $CacheRoot ("packages\Microsoft.Windows.SDK.CPP.$sdkVersion\c")
    $inf2cat = Join-Path $wdkRoot 'bin\10.0.28000.0\x86\Inf2Cat.exe'
    $signtool = Join-Path $sdkRoot 'bin\10.0.28000.0\x64\signtool.exe'
    $dependencyEvidence = Get-PinnedSigningDependencyEvidence -CacheRoot $CacheRoot -WdkVersion $wdkVersion -SdkVersion $sdkVersion -Inf2CatPath $inf2cat -SignToolPath $signtool
    Assert-Condition -Condition ($dependencyEvidence.native_commands.Count -eq 3) -Message 'Expected all three pinned NuGet packages to be signature-verified.'
    Assert-Condition -Condition ($dependencyEvidence.tools.inf2cat.embedded_authenticode_digest -eq 'PASS') -Message 'Inf2Cat embedded signature digest did not verify.'
    Assert-Condition -Condition ($dependencyEvidence.tools.signtool.embedded_authenticode_digest -eq 'PASS') -Message 'SignTool embedded signature digest did not verify.'

    $catalogPath = Join-Path $SourcePackageDirectory 'ntpodsaap.cat'
    $sourceSysPath = Join-Path $SourcePackageDirectory 'NTPodsAAP.sys'
    $sourceInfPath = Join-Path $SourcePackageDirectory 'NTPodsAAP.inf'
    Assert-Condition -Condition ((Test-CatalogMember -CatalogPath $catalogPath -MemberPath $sourceSysPath).status -eq 'PASS') -Message 'The source catalog did not validate NTPodsAAP.sys.'
    Assert-Condition -Condition ((Test-CatalogMember -CatalogPath $catalogPath -MemberPath $sourceInfPath).status -eq 'PASS') -Message 'The source catalog did not validate NTPodsAAP.inf.'

    $tamperedSys = Join-Path $testRoot 'NTPodsAAP-tampered.sys'
    Copy-Item -LiteralPath $sourceSysPath -Destination $tamperedSys
    $tamperedSysBytes = [IO.File]::ReadAllBytes($tamperedSys)
    $tamperedSysBytes[4096] = [byte]($tamperedSysBytes[4096] -bxor 1)
    [IO.File]::WriteAllBytes($tamperedSys, $tamperedSysBytes)
    Assert-Throws -Action { Test-CatalogMember -CatalogPath $catalogPath -MemberPath $tamperedSys } -MessagePattern 'Catalog member hash check failed' -FailureMessage 'A tampered SYS unexpectedly passed catalog membership.'

    $tamperedTool = Join-Path $testRoot 'signtool-tampered.exe'
    Copy-Item -LiteralPath $signtool -Destination $tamperedTool
    $tamperedToolBytes = [IO.File]::ReadAllBytes($tamperedTool)
    $tamperedToolBytes[4096] = [byte]($tamperedToolBytes[4096] -bxor 1)
    [IO.File]::WriteAllBytes($tamperedTool, $tamperedToolBytes)
    Assert-Throws -Action { Test-EmbeddedAuthenticodeDigest -Path $tamperedTool } -MessagePattern 'Embedded Authenticode file-digest check failed' -FailureMessage 'A tampered PE unexpectedly passed the embedded Authenticode digest check.'

    $failingCommand = Join-Path $testRoot 'exit-17.cmd'
    [IO.File]::WriteAllText($failingCommand, "@echo off`r`nexit /b 17`r`n", [Text.Encoding]::ASCII)
    Assert-Throws -Action { Invoke-CheckedNative -Tool $failingCommand -Arguments @('ignored') -Purpose 'signer native-exit regression test' } -MessagePattern 'exit code .17.' -FailureMessage 'A nonzero native exit code was unexpectedly accepted.'

    Assert-Throws -Action { Assert-ContainedStagingDirectory -StageDirectory (Join-Path $developmentRoot 'outside-staging') -OutputDirectory (Join-Path $developmentRoot 'expected-output') -OutputParent $developmentRoot } -MessagePattern 'unexpected staging path' -FailureMessage 'An unexpected staging path was accepted.'

    $signedFixtureResult = 'SKIPPED_NO_SIGNED_FIXTURE_SUPPLIED'
    if ($SignedTestSysPath -or $SignedTestCatalogPath -or $SignerThumbprint) {
        if (-not $SignedTestSysPath -or -not $SignedTestCatalogPath -or -not $SignerThumbprint) {
            throw 'Signed fixture checks require SignedTestSysPath, SignedTestCatalogPath, and SignerThumbprint together.'
        }
        $SignedTestSysPath = [IO.Path]::GetFullPath($SignedTestSysPath)
        $SignedTestCatalogPath = [IO.Path]::GetFullPath($SignedTestCatalogPath)
        $sysSigner = Test-AuthenticodeSigner -Path $SignedTestSysPath -ExpectedThumbprint $SignerThumbprint -Purpose 'Embedded SYS'
        $catSigner = Test-AuthenticodeSigner -Path $SignedTestCatalogPath -ExpectedThumbprint $SignerThumbprint -Purpose 'Catalog'
        $catCms = Test-CatalogCmsSignature -CatalogPath $SignedTestCatalogPath -ExpectedThumbprint $SignerThumbprint
        $signedFixtureInfPath = Join-Path (Split-Path -Parent $SignedTestSysPath) 'NTPodsAAP.inf'
        $signedFixtureSysMembership = Test-CatalogMember -CatalogPath $SignedTestCatalogPath -MemberPath $SignedTestSysPath
        $signedFixtureInfMembership = Test-CatalogMember -CatalogPath $SignedTestCatalogPath -MemberPath $signedFixtureInfPath
        Assert-Condition -Condition ($sysSigner.native_trust_status -eq '800B0109' -and $sysSigner.embedded_authenticode_digest -eq 'PASS' -and $sysSigner.embedded_cms_signature -eq 'PASS') -Message 'Self-signed SYS did not produce the exact expected untrusted-root plus independent digest/CMS evidence.'
        Assert-Condition -Condition ($catSigner.native_trust_status -eq '800B0109' -and $catCms.status -eq 'PASS' -and $signedFixtureSysMembership.status -eq 'PASS' -and $signedFixtureInfMembership.status -eq 'PASS') -Message 'Self-signed CAT did not produce the exact expected untrusted-root, CMS, and INF/SYS member evidence.'

        $tamperedCmsSys = Join-Path $testRoot 'NTPodsAAP-cms-tampered.sys'
        Copy-Item -LiteralPath $SignedTestSysPath -Destination $tamperedCmsSys
        $cmsSysBytes = [IO.File]::ReadAllBytes($tamperedCmsSys)
        $ntOffset = [BitConverter]::ToInt32($cmsSysBytes, 0x3C)
        $optionalOffset = $ntOffset + 24
        $optionalMagic = [BitConverter]::ToUInt16($cmsSysBytes, $optionalOffset)
        $directoryOffset = if ($optionalMagic -eq 0x10B) { 96 } elseif ($optionalMagic -eq 0x20B) { 112 } else { throw 'Signed fixture has an unsupported PE optional header.' }
        $securityOffset = $optionalOffset + $directoryOffset + (4 * 8)
        $certificateOffset = [int][BitConverter]::ToUInt32($cmsSysBytes, $securityOffset)
        $certificateLength = [int][BitConverter]::ToUInt32($cmsSysBytes, $certificateOffset)
        if ($certificateLength -lt 64) { throw 'Signed fixture WIN_CERTIFICATE is unexpectedly short.' }
        $cmsBlob = New-Object byte[] ($certificateLength - 8)
        [Array]::Copy($cmsSysBytes, $certificateOffset + 8, $cmsBlob, 0, $cmsBlob.Length)
        Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
        $signedCms = [System.Security.Cryptography.Pkcs.SignedCms]::new()
        $signedCms.Decode($cmsBlob)
        if ($signedCms.SignerInfos.Count -ne 1) { throw 'Signed fixture must contain one embedded CMS signer.' }
        $signatureBytes = $signedCms.SignerInfos[0].GetSignature()
        if ($signatureBytes.Length -lt 16) { throw 'Signed fixture CMS signature value is unexpectedly short.' }
        $signatureOffsets = New-Object 'System.Collections.Generic.List[int]'
        $searchStart = $certificateOffset + 8
        $searchEnd = $certificateOffset + $certificateLength - $signatureBytes.Length
        for ($candidateOffset = $searchStart; $candidateOffset -le $searchEnd; $candidateOffset++) {
            $matchesSignature = $true
            for ($signatureIndex = 0; $signatureIndex -lt $signatureBytes.Length; $signatureIndex++) {
                if ($cmsSysBytes[$candidateOffset + $signatureIndex] -ne $signatureBytes[$signatureIndex]) {
                    $matchesSignature = $false
                    break
                }
            }
            if ($matchesSignature) { $signatureOffsets.Add($candidateOffset) }
        }
        if ($signatureOffsets.Count -ne 1) { throw "Expected one embedded copy of CMS signature bytes, found $($signatureOffsets.Count)." }
        $signatureByteToFlip = $signatureOffsets[0] + [int]($signatureBytes.Length / 2)
        $cmsSysBytes[$signatureByteToFlip] = [byte]($cmsSysBytes[$signatureByteToFlip] -bxor 1)
        [IO.File]::WriteAllBytes($tamperedCmsSys, $cmsSysBytes)
        Assert-Throws -Action { Test-EmbeddedPeAuthenticodeCmsSignature -Path $tamperedCmsSys -ExpectedThumbprint $SignerThumbprint } -MessagePattern 'Embedded PE Authenticode CMS|signature|signer|WIN_CERTIFICATE' -FailureMessage 'A tampered PE CMS signature unexpectedly passed cryptographic verification.'
        Assert-Condition -Condition ((Test-EmbeddedAuthenticodeDigest -Path $tamperedCmsSys).status -eq 'PASS') -Message 'Changing only the embedded CMS unexpectedly changed the independent PE SIP file digest.'
        $signedFixtureResult = 'PASS'
    }

    $storeAfter = @(Get-CurrentUserMyThumbprints)
    Assert-Condition -Condition (($storeBefore -join ',') -ceq ($storeAfter -join ',')) -Message 'Offline signer tests changed Cert:\CurrentUser\My.'
    [pscustomobject]@{
        status = 'PASS'
        source_manifest_and_hashes = 'PASS'
        pinned_nuget_package_signatures = 'PASS'
        pinned_tool_authenticode_digests = 'PASS'
        source_catalog_sys_inf_positive_checks = 'PASS'
        tampered_catalog_member_rejected = 'PASS'
        tampered_embedded_authenticode_digest_rejected = 'PASS'
        native_nonzero_exit_rejected = 'PASS'
        staging_containment_rejected = 'PASS'
        plan_only_no_output = 'PASS'
        preserved_signed_fixture_checks = $signedFixtureResult
        current_user_my_store_unchanged = 'PASS'
    }
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $expectedPrefix = $developmentRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedTestRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        $resolvedTestRoot.StartsWith((Join-Path $developmentRoot 'signer-test-temp-'), [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Assert-NoReparsePointInPath -Path $resolvedTestRoot
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force -ErrorAction Stop
    }
}

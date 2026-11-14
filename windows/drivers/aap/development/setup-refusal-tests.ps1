<#
.SYNOPSIS
Runs offline refusal checks for the bounded development test-host scripts.
.DESCRIPTION
This Pester-free check parses the scripts and feeds them malformed temporary
manifests. It never imports a certificate, reads/writes the real trust stores,
stages a driver, binds a device, changes boot settings, or touches pairing.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$developmentDirectory = Join-Path $repoRoot 'windows\drivers\aap\development'
$hostCollector = Join-Path $developmentDirectory 'Get-TestHostState.ps1'
$trustScript = Join-Path $developmentDirectory 'Set-TestDriverTrust.ps1'
$installScript = Join-Path $developmentDirectory 'Install-TestDriver.ps1'
$reviewedManifestSha256 = '698FB455EDCEF9A33556C65BDAB6CF39F6B02E566FE308BD3FDD89FC30C550E5'
foreach ($path in @($hostCollector,$trustScript,$installScript)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required setup script is missing: $path" }
    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) { throw "PowerShell syntax failed for $path`: $($parseErrors[0].Message)" }
}

$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempParent ('AppleDeviceHub-setup-refusal-tests-' + [Guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusal-test temporary path escaped the system temporary directory.' }
New-Item -ItemType Directory -Path $testRoot -ErrorAction Stop | Out-Null
$passed = 0

function Assert-Refusal([string]$Name, [scriptblock]$Attempt, [string]$ExpectedText) {
    $failure = $null
    try { & $Attempt | Out-Null }
    catch { $failure = $_.Exception.Message }
    if (-not $failure -or $failure -notmatch [regex]::Escape($ExpectedText)) {
        throw "Refusal check failed: $Name. Expected error containing '$ExpectedText'; received '$failure'."
    }
    $script:passed++
    Write-Output "PASS $Name"
}

try {
    $collectorAst = [System.Management.Automation.Language.Parser]::ParseFile($hostCollector, [ref]$tokens, [ref]$parseErrors)
    $converterAst = $collectorAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Convert-PnpDevicePropertyReadResult' }, $true)
    if ($null -eq $converterAst) { throw 'PnP empty-property converter is missing from the host collector.' }
    $converterScript = [scriptblock]::Create($converterAst.Extent.Text)
    . $converterScript
    $emptyPropertyFixture = [pscustomobject]@{ Type = 0 }
    $emptyResult = Convert-PnpDevicePropertyReadResult $emptyPropertyFixture
    if ($emptyResult.status -cne 'NOT_SET' -or $null -ne $emptyResult.value -or $emptyResult.source -cne 'DEVPROP_TYPE_EMPTY') { throw 'DEVPROP_TYPE_EMPTY fixture was not classified as a successful NOT_SET read.' }
    $passed++
    Write-Output 'PASS empty PnP property fixture is read as NOT_SET without accessing Data'

    $missingManifest = Join-Path $testRoot 'missing-manifest.json'
    Assert-Refusal 'trust import requires the explicit Apply opt-in' {
        & $trustScript -ManifestPath $missingManifest -ExpectedManifestSha256 $reviewedManifestSha256 -Action Import
    } 'No action taken'
    Assert-Refusal 'driver stage requires the explicit Apply opt-in' {
        & $installScript -ManifestPath $missingManifest -ExpectedManifestSha256 $reviewedManifestSha256 -Action StagePackage -BluetoothAddress 001122334455 -TargetInstanceId 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_VID&0001004C_PID&2027\9&1a2b3c4d&0&001122334455_C00000000'
    } 'No action taken'
    Assert-Refusal 'installer rejects every target that does not match the selected address before host access' {
        & $installScript -ManifestPath $missingManifest -ExpectedManifestSha256 $reviewedManifestSha256 -Action Plan -BluetoothAddress 001122334455 -TargetInstanceId 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}\66778899AABB'
    } 'TargetInstanceId must match the selected AAP Server instance'

    $badPackageManifest = [ordered]@{
        schema_version = 1; status = 'TEST_ONLY_SIGNED_PACKAGE'; trust = 'TestOnly'; microsoft_signature = 'NOT_PRESENT'
        signer_thumbprint = ('A' * 40)
        target = [ordered]@{ hardware_id = 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}'; service = 'NTPodsAAP' }
        package_directory = '..\outside'; artifact_sha256 = [ordered]@{ 'NTPodsAAP.inf' = ('0' * 64); 'NTPodsAAP.sys' = ('0' * 64); 'NTPodsAAP.cat' = ('0' * 64) }
        certificate = [ordered]@{ public_certificate_file = 'signer.cer'; public_certificate_sha256 = ('0' * 64); thumbprint = ('A' * 40) }
    }
    $badPackagePath = Join-Path $testRoot 'package-traversal.json'
    $badPackageManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $badPackagePath -Encoding UTF8
    $badPackageHash = (Get-FileHash -LiteralPath $badPackagePath -Algorithm SHA256).Hash
    Assert-Refusal 'installer rejects a manifest outside the reviewed hash pin' {
        & $installScript -ManifestPath $badPackagePath -ExpectedManifestSha256 $reviewedManifestSha256 -Action Plan -BluetoothAddress 001122334455 -TargetInstanceId 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_VID&0001004C_PID&2027\9&1a2b3c4d&0&001122334455_C00000000'
    } 'Manifest SHA-256 does not match the externally reviewed expected hash'
    Assert-Refusal 'installer rejects package directory traversal before host or PnP access' {
        & $installScript -ManifestPath $badPackagePath -ExpectedManifestSha256 $badPackageHash -Action Plan -BluetoothAddress 001122334455 -TargetInstanceId 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_VID&0001004C_PID&2027\9&1a2b3c4d&0&001122334455_C00000000'
    } 'Package directory escapes the manifest directory'

    $badTrustManifest = [ordered]@{
        schema_version = 1; status = 'TEST_ONLY_SIGNED_PACKAGE'; trust = 'TestOnly'; microsoft_signature = 'NOT_PRESENT'; signer_thumbprint = ('A' * 40)
        target = [ordered]@{ hardware_id = 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}'; service = 'NTPodsAAP' }
        certificate = [ordered]@{ public_certificate_file = '..\outside.cer'; public_certificate_sha256 = ('0' * 64); thumbprint = ('A' * 40); subject = 'CN=Test'; serial_number = '01' }
    }
    $badTrustPath = Join-Path $testRoot 'trust-traversal.json'
    $badTrustManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $badTrustPath -Encoding UTF8
    $badTrustHash = (Get-FileHash -LiteralPath $badTrustPath -Algorithm SHA256).Hash
    Assert-Refusal 'trust import rejects certificate path traversal before store access' {
        & $trustScript -ManifestPath $badTrustPath -ExpectedManifestSha256 $badTrustHash -Action Import -Apply -WhatIf
    } 'Public certificate file escapes the manifest directory'

    Write-Output "PASS $passed offline setup refusal checks"
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolvedTestRoot).StartsWith('AppleDeviceHub-setup-refusal-tests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

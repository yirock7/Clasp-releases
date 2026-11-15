<#
.SYNOPSIS
Imports or rolls back only the manifest-pinned test driver certificate.
.DESCRIPTION
This script never creates a certificate or private key. Import touches only the
exact certificate named by a TEST_ONLY_SIGNED_PACKAGE manifest and only the
LocalMachine Root and TrustedPublisher stores. Rollback removes only store
entries this receipt records as newly added. Machine changes require -Apply and
ShouldProcess approval. No boot or security settings are changed.

Time-of-check/time-of-use hardening: the manifest is read once into memory, hashed
against -ExpectedManifestSha256, and parsed from those same bytes (the path is never
reopened). The certificate and the four package files are read once, hashed against
the manifest, and copied into a fresh snapshot directory under
%ProgramData%\Clasp\driver-staging\<guid> (SYSTEM and Administrators only, inheritance
disabled, no reparse points). The snapshot is re-hashed immediately before each
certificate-store change, and removed at the end.

Receipts are written to %ProgramData%\Clasp\driver-staging\receipts (same protection).
-ReceiptPath may be a bare file name or a path inside that directory. After writing,
the script prints the receipt's SHA-256. Rollback requires that value as
-ExpectedReceiptHash and refuses a receipt whose exact bytes do not hash to it, so a
tampered or substituted receipt is rejected before any store is touched. Record the
printed hash somewhere outside this machine's writable folders. Run from an elevated
PowerShell session.
.PARAMETER ExpectedReceiptHash
SHA-256 of the exact Import receipt bytes (printed by the Import run). Required for Rollback.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedManifestSha256,
    [ValidateSet('Import','Rollback')][string]$Action = 'Import',
    [string]$ReceiptPath,
    [string]$RollbackReceiptPath,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedReceiptHash,
    [switch]$ConfirmNoInstalledPackageUsesCertificate,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'DriverStaging.ps1')

function Resolve-ManifestChild([string]$Root, [string]$RelativePath, [string]$Label) {
    if ([IO.Path]::IsPathRooted($RelativePath)) { throw "$Label must be relative to the manifest directory." }
    $resolved = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "$Label escapes the manifest directory." }
    return $resolved
}

function Read-TestCertificateManifest([string]$Path, [bool]$ForImport, [string]$ExpectedHash) {
    $manifestFull = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $manifestFull -PathType Leaf)) { throw "Manifest not found: $manifestFull" }
    $root = Split-Path -Parent $manifestFull
    # Single read: the bytes are hashed first, then parsed. The path is never opened again.
    $manifestRead = Read-VerifiedManifest $manifestFull $ExpectedHash
    $json = $manifestRead.manifest
    if ([int]$json.schema_version -ne 1 -or [string]$json.status -cne 'TEST_ONLY_SIGNED_PACKAGE' -or [string]$json.trust -cne 'TestOnly' -or [string]$json.microsoft_signature -cne 'NOT_PRESENT') {
        throw 'Refusing trust changes: manifest is not schema 1 TEST_ONLY_SIGNED_PACKAGE with TestOnly trust and no Microsoft signature.'
    }
    if ([string]$json.target.hardware_id -ine 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}' -or [string]$json.target.service -cne 'NTPodsAAP') {
        throw 'Refusing trust changes: manifest target is not the restricted AAP hardware ID and service.'
    }
    if ($null -eq $json.certificate -or -not $json.certificate.public_certificate_file -or -not $json.certificate.public_certificate_sha256 -or -not $json.certificate.thumbprint) {
        throw 'Manifest lacks the public certificate file, SHA-256, or thumbprint.'
    }
    $certPath = Resolve-ManifestChild $root ([string]$json.certificate.public_certificate_file) 'Public certificate file'
    if (-not (Test-Path -LiteralPath $certPath -PathType Leaf)) { throw 'Public certificate file is missing.' }
    # Single read of the certificate: hashed against the manifest, then the object is built from those bytes.
    $certRead = Read-VerifiedBytes $certPath (([string]$json.certificate.public_certificate_sha256).Replace(' ','')) 'Public certificate'
    $certHash = $certRead.sha256
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([byte[]]$certRead.bytes)
    $expectedThumbprint = ([string]$json.certificate.thumbprint -replace '\s','').ToUpperInvariant()
    if ($certificate.Thumbprint.ToUpperInvariant() -ne $expectedThumbprint) { throw 'Public certificate thumbprint does not match the signed manifest.' }
    if (([string]$json.signer_thumbprint -replace '\s','').ToUpperInvariant() -cne $expectedThumbprint) { throw 'Top-level signer thumbprint does not match the certificate identity.' }
    if ($certificate.Subject -cne $certificate.Issuer) { throw 'Trust import is limited to the manifest-pinned self-issued test certificate.' }
    if ($json.certificate.PSObject.Properties['subject'] -and $json.certificate.subject -and $certificate.Subject -cne [string]$json.certificate.subject) { throw 'Public certificate subject does not match the signed manifest.' }
    if ($json.certificate.PSObject.Properties['serial_number'] -and $json.certificate.serial_number -and ($certificate.SerialNumber -replace '\s','').ToUpperInvariant() -ne ([string]$json.certificate.serial_number -replace '\s','').ToUpperInvariant()) { throw 'Public certificate serial number does not match the signed manifest.' }
    if ($ForImport) {
        $now = [DateTime]::UtcNow
        if ($now -lt $certificate.NotBefore.ToUniversalTime() -or $now -gt $certificate.NotAfter.ToUniversalTime()) { throw 'Test certificate is outside its validity period.' }
    }
    $ekuExtension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    $eku = @()
    if ($ekuExtension) {
        $typedEku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($ekuExtension, $ekuExtension.Critical)
        $eku = @($typedEku.EnhancedKeyUsages | ForEach-Object { $_.Value })
    }
    if ($eku.Count -eq 0 -or $eku -notcontains '1.3.6.1.5.5.7.3.3') { throw 'Test certificate does not include the code-signing EKU.' }
    return [ordered]@{ path = $manifestFull; root = $root; sha256 = $manifestRead.sha256; manifest = $json; certificate = $certificate; certificate_path = $certPath; certificate_sha256 = $certHash; certificate_bytes = $certRead.bytes; thumbprint = $expectedThumbprint }
}

function Assert-TestPackageFiles([object]$Info) {
    # Memory-only verification: every package file is read once and hashed against the manifest.
    # The verified bytes are kept in $Info so the snapshot is written from them (no second read).
    $manifest = $Info.manifest
    $expectedNames = @('NTPodsAAP.inf','NTPodsAAP.sys','ntpodsaap.cat','NTPodsAAP.pdb')
    if ([string]$manifest.package_directory -cne 'ClaspAAP') { throw 'Trust import is limited to the reviewed ClaspAAP package directory.' }
    $packageDirectory = Resolve-ManifestChild $Info.root ([string]$manifest.package_directory) 'Package directory'
    if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) { throw 'Reviewed package directory is missing.' }
    $actualItems = @(Get-ChildItem -LiteralPath $packageDirectory -Force -ErrorAction Stop)
    if (@($actualItems | Where-Object { -not $_.PSIsContainer }).Count -ne $actualItems.Count) { throw 'Package directory contains a directory; exact four-file package set is required.' }
    $actualNames = @($actualItems | ForEach-Object { $_.Name })
    if ($actualNames.Count -ne $expectedNames.Count -or @($actualNames | Where-Object { $_ -notin $expectedNames }).Count -gt 0 -or @($expectedNames | Where-Object { $_ -notin $actualNames }).Count -gt 0) { throw 'Package directory does not contain the exact reviewed INF/SYS/CAT/PDB file set.' }

    $artifactProperties = @($manifest.artifact_sha256.PSObject.Properties)
    if ($artifactProperties.Count -ne $expectedNames.Count) { throw 'Manifest artifact hash map does not contain exactly four reviewed package files.' }
    $hashByName = @{}
    foreach ($property in $artifactProperties) {
        $name = [string]$property.Name
        $canonicalName = @($expectedNames | Where-Object { $_ -ieq $name })
        if ($canonicalName.Count -ne 1 -or $property.Value -notmatch '^[A-Fa-f0-9]{64}$') { throw "Manifest package hash entry is unexpected or invalid: $name" }
        if ($hashByName.ContainsKey([string]$canonicalName[0])) { throw "Manifest repeats package hash entry using different casing: $name" }
        $hashByName[[string]$canonicalName[0]] = [string]$property.Value
    }
    $bytesByName = [ordered]@{}
    foreach ($name in $expectedNames) {
        if (-not $hashByName.ContainsKey($name)) { throw "Manifest does not hash exactly one $name artifact." }
        $read = Read-VerifiedBytes (Join-Path $packageDirectory $name) $hashByName[$name] "Package artifact $name"
        $bytesByName[$name] = $read.bytes
    }

    foreach ($key in @('catalog_generation','embedded_sys_signature','catalog_cms_signature','catalog_membership','exact_signer_thumbprint','signer_key_nonexportable')) {
        if (-not $manifest.validation.PSObject.Properties[$key] -or [string]$manifest.validation.$key -cne 'PASS') { throw "Reviewed manifest validation '$key' is not PASS." }
    }
    foreach ($alias in @('cat_generation','sys_signature','cat_signature')) {
        if (-not $manifest.validation.PSObject.Properties[$alias] -or [string]$manifest.validation.$alias -cne 'PASS') { throw "Reviewed manifest validation '$alias' is not PASS." }
    }
    foreach ($fileName in @('NTPodsAAP.inf','NTPodsAAP.sys')) {
        if (-not $manifest.validation.catalog_member_files.PSObject.Properties[$fileName] -or [string]$manifest.validation.catalog_member_files.$fileName -cne 'PASS') { throw "Reviewed catalog membership for $fileName is not PASS." }
    }
    if ([string]$manifest.validation.signer_chain_trust -cne 'NOT_IMPORTED' -or [string]$manifest.validation.microsoft_signature -cne 'NOT_PRESENT' -or [string]$manifest.validation.hardware -cne 'NOT_RUN') { throw 'Manifest validation trust/hardware claims are outside the reviewed test-only contract.' }

    $Info['package_directory'] = $packageDirectory
    $Info['package_bytes'] = $bytesByName
    $Info['package_hashes'] = $hashByName
    return $packageDirectory
}

function Assert-PackageSignatures([string]$Directory, [object]$Info) {
    # Authenticode needs real paths, so this runs against the verified snapshot copies.
    foreach ($fileName in @('NTPodsAAP.sys','ntpodsaap.cat')) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $Directory $fileName) -ErrorAction Stop
        $signatureThumbprint = if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint.ToUpperInvariant() } else { $null }
        if ($signatureThumbprint -cne $Info.thumbprint -or [string]$signature.Status -notin @('UnknownError','Valid')) { throw "Package signature identity/status is not the reviewed exact test signer for $fileName." }
    }
}

function New-TrustSnapshot([object]$Info) {
    # Copies the already-verified certificate and package bytes into admin-only, non-reparse storage.
    $snapshot = New-StagingSnapshot
    try {
        [void](Add-SnapshotFile $snapshot 'certificate.cer' $Info.certificate_bytes $Info.certificate_sha256)
        foreach ($name in $Info.package_bytes.Keys) {
            [void](Add-SnapshotFile $snapshot $name $Info.package_bytes[$name] $Info.package_hashes[$name])
        }
        return $snapshot
    }
    catch { Remove-StagingSnapshot $snapshot; throw }
}

function Get-VerifiedSnapshotCertificate([object]$Snapshot, [object]$Info, [string]$Reason) {
    # Reverify the whole snapshot, then build the certificate from bytes hashed in this very call.
    Assert-SnapshotIntact $Snapshot $Reason
    $bytes = Read-AllBytes (Join-Path $Snapshot.directory 'certificate.cer')
    if ((Get-BytesSha256 $bytes) -cne $Info.certificate_sha256) { throw "Snapshot certificate changed after validation; refusing $Reason." }
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([byte[]]$bytes)
    if ($certificate.Thumbprint.ToUpperInvariant() -ne $Info.thumbprint) { throw "Snapshot certificate identity changed; refusing $Reason." }
    return $certificate
}

function Get-StoreContains([string]$StoreName, [string]$Thumbprint) {
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($StoreName, [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        foreach ($item in $store.Certificates) { if ($item.Thumbprint -and $item.Thumbprint.ToUpperInvariant() -eq $Thumbprint) { return $true } }
        return $false
    }
    finally { $store.Close() }
}

function Add-ExactCertificate([string]$StoreName, [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($StoreName, [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $store.Add($Certificate)
    }
    finally { $store.Close() }
}

function Remove-ExactCertificate([string]$StoreName, [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($StoreName, [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        foreach ($item in @($store.Certificates)) {
            if ($item.Thumbprint -and $item.Thumbprint.ToUpperInvariant() -eq $Certificate.Thumbprint.ToUpperInvariant()) { $store.Remove($item); break }
        }
    }
    finally { $store.Close() }
}

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run trust-store mutation from an elevated PowerShell session.' }
}

function Get-CurrentSid {
    return [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}

if (-not $Apply) { throw 'No action taken. Re-run with the named -Apply opt-in; WhatIf remains supported.' }
if ($Action -eq 'Import') {
    $info = Read-TestCertificateManifest $ManifestPath $true $ExpectedManifestSha256
    $null = Assert-TestPackageFiles $info
    $stores = @('Root','TrustedPublisher')
    $before = [ordered]@{}
    foreach ($name in $stores) { $before[$name] = Get-StoreContains $name $info.thumbprint }
    $receiptName = $ReceiptPath
    if (-not $receiptName) { $receiptName = 'test-driver-trust-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json' }
    $receiptTarget = Resolve-ReceiptLocation $receiptName '-ReceiptPath'
    if (Test-Path -LiteralPath $receiptTarget) { throw 'Receipt path already exists; choose a new path so evidence is never overwritten.' }
    $receipt = [ordered]@{
        schema_version = 1; status = 'IMPORT_PENDING'; action = 'Import'; captured_at_utc = [DateTime]::UtcNow.ToString('o')
        manifest_sha256 = $info.sha256; certificate_sha256 = $info.certificate_sha256; certificate_thumbprint = $info.thumbprint; signer_thumbprint = $info.thumbprint; thumbprint = $info.thumbprint; certificate = [ordered]@{ sha256 = $info.certificate_sha256; thumbprint = $info.thumbprint; subject = $info.certificate.Subject; file = $info.certificate_path }
        owner_sid = (Get-CurrentSid)
        stores = @($stores | ForEach-Object { [ordered]@{ name = $_; was_present = [bool]$before[$_]; add_intended = $false; added_by_run = $false; present_after = [bool]$before[$_]; status = if ($before[$_]) { 'PRESENT_BEFORE' } else { 'NOT_YET_ADDED' } } })
        rollback = 'Rollback can remove only exact store entries with added_by_run=true, after confirming no installed package uses this certificate. Rollback requires -ExpectedReceiptHash (the SHA-256 printed after this run).'
    }
    if (-not $PSCmdlet.ShouldProcess("LocalMachine Root and TrustedPublisher for $($info.thumbprint)", "Import exact test certificate and journal to $receiptTarget")) {
        [ordered]@{ status = 'WHATIF_OR_DECLINED'; action = 'Import'; thumbprint = $info.thumbprint; planned_stores = @($stores | Where-Object { -not $before[$_] }); receipt_path = $receiptTarget } | ConvertTo-Json -Depth 5 | Write-Output
        return
    }
    Assert-Elevated
    [void](Initialize-ReceiptDirectory)
    $snapshot = $null
    try {
        # Verified bytes -> administrator-only snapshot; signatures are checked on the snapshot copies.
        $snapshot = New-TrustSnapshot $info
        Assert-PackageSignatures $snapshot.directory $info
        # Journal location and initial contents are proven writable before either store changes.
        Write-JsonJournal $receiptTarget $receipt
        try {
            for ($i = 0; $i -lt $stores.Count; $i++) {
                $name = $stores[$i]
                if (-not $before[$name]) {
                    $receipt.stores[$i].add_intended = $true
                    $receipt.stores[$i].status = 'ADD_INTENT_DURABLE'
                    Write-JsonJournal $receiptTarget $receipt
                    # Reverify the snapshot immediately before this privileged change.
                    $verifiedCertificate = Get-VerifiedSnapshotCertificate $snapshot $info "import into LocalMachine\$name"
                    Add-ExactCertificate $name $verifiedCertificate
                    $receipt.stores[$i].added_by_run = $true
                    $receipt.stores[$i].add_intended = $false
                    $receipt.stores[$i].status = 'ADDED_BY_RUN'
                }
                $receipt.stores[$i].present_after = Get-StoreContains $name $info.thumbprint
                if (-not $receipt.stores[$i].present_after) { throw "Certificate presence verification failed in LocalMachine\$name." }
                Write-JsonJournal $receiptTarget $receipt
            }
            $receipt.status = 'TEST_ONLY_CERTIFICATE_TRUSTED'
            $receipt.captured_at_utc = [DateTime]::UtcNow.ToString('o')
            Write-JsonJournal $receiptTarget $receipt
        }
        catch {
            $failure = $_.Exception.Message
            for ($i = 0; $i -lt $stores.Count; $i++) {
                if ($receipt.stores[$i].added_by_run -eq $true -or ($receipt.stores[$i].add_intended -eq $true -and $receipt.stores[$i].was_present -eq $false)) {
                    try {
                        if (Get-StoreContains $stores[$i] $info.thumbprint) { Remove-ExactCertificate $stores[$i] $info.certificate }
                        $receipt.stores[$i].present_after = Get-StoreContains $stores[$i] $info.thumbprint
                        if ($receipt.stores[$i].present_after) { throw "Certificate remains in LocalMachine\$($stores[$i]) after import-failure rollback." }
                        $receipt.stores[$i].added_by_run = $false
                        $receipt.stores[$i].add_intended = $false
                        $receipt.stores[$i].status = 'ROLLED_BACK_AFTER_IMPORT_FAILURE'
                    }
                    catch { $receipt.stores[$i].status = 'ROLLBACK_FAILED_REQUIRES_REVIEW' }
                }
            }
            $receipt.status = 'IMPORT_FAILED_ROLLBACK_ATTEMPTED'
            $receipt.error = $failure
            $failureHash = ''
            try { Write-JsonJournal $receiptTarget $receipt; $failureHash = ' receipt SHA-256: ' + (Publish-ReceiptHash $receiptTarget) + '.' } catch { }
            throw "Test certificate import failed; journal: $receiptTarget.$failureHash $failure"
        }
    }
    finally { Remove-StagingSnapshot $snapshot }
    $null = Publish-ReceiptHash $receiptTarget
    $receipt | ConvertTo-Json -Depth 8 | Write-Output
    return
}

if (-not $RollbackReceiptPath) { throw '-RollbackReceiptPath is required for rollback.' }
if (-not $ExpectedReceiptHash) { throw '-ExpectedReceiptHash is required for rollback: the SHA-256 printed when the Import receipt was written.' }
if (-not $ConfirmNoInstalledPackageUsesCertificate) { throw 'Rollback requires -ConfirmNoInstalledPackageUsesCertificate after the exact test package is no longer installed or staged.' }
$manifestInfo = Read-TestCertificateManifest $ManifestPath $false $ExpectedManifestSha256
$null = Assert-TestPackageFiles $manifestInfo
# Authenticate the receipt (admin-only location + exact-bytes SHA-256) before trusting any field of it.
$receiptRead = Read-HashedReceipt $RollbackReceiptPath $ExpectedReceiptHash 'Trust rollback receipt'
$rollbackFull = $receiptRead.path
$receiptInput = $receiptRead.receipt
if ([int]$receiptInput.schema_version -ne 1 -or [string]$receiptInput.action -cne 'Import' -or [string]$receiptInput.status -notin @('TEST_ONLY_CERTIFICATE_TRUSTED','IMPORT_PENDING','IMPORT_FAILED_ROLLBACK_ATTEMPTED') -or [string]$receiptInput.manifest_sha256 -cne $manifestInfo.sha256 -or ([string]$receiptInput.owner_sid -cne (Get-CurrentSid))) { throw 'Rollback receipt schema, status, owner SID, or manifest hash does not match.' }
if (([string]$receiptInput.certificate.thumbprint -replace '\s','').ToUpperInvariant() -ne $manifestInfo.thumbprint -or ([string]$receiptInput.certificate.sha256 -replace '\s','').ToUpperInvariant() -ne $manifestInfo.certificate_sha256 -or ([string]$receiptInput.signer_thumbprint -replace '\s','').ToUpperInvariant() -ne $manifestInfo.thumbprint -or ([string]$receiptInput.certificate_sha256 -replace '\s','').ToUpperInvariant() -ne $manifestInfo.certificate_sha256) { throw 'Rollback receipt certificate identity does not match manifest-pinned DER.' }
$sourceStores = @($receiptInput.stores)
$sourceStoreNames = @($sourceStores | Select-Object -ExpandProperty name | Sort-Object -Unique)
if ($sourceStores.Count -ne 2 -or (($sourceStoreNames -join ',') -cne 'Root,TrustedPublisher')) { throw 'Rollback receipt must contain exactly one entry for Root and TrustedPublisher.' }
foreach ($entry in $sourceStores) {
    if ($null -eq $entry.was_present -or $null -eq $entry.added_by_run -or $null -eq $entry.add_intended -or $null -eq $entry.present_after) { throw 'Rollback receipt lacks durable per-store intent/state fields.' }
    if ($entry.add_intended -eq $true -and $entry.was_present -eq $true) { throw 'Rollback receipt has an inconsistent add intent for a certificate already present before import.' }
}
$rollbackPlan = @($sourceStores | Where-Object { $_.added_by_run -eq $true -or ($_.add_intended -eq $true -and $_.was_present -eq $false) })
foreach ($entry in $rollbackPlan) { if ([string]$entry.name -notin @('Root','TrustedPublisher')) { throw 'Receipt names an unsupported certificate store.' } }
$journalName = $ReceiptPath
if (-not $journalName) { $journalName = 'trust-rollback-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json' }
$journalTarget = Resolve-ReceiptLocation $journalName '-ReceiptPath'
if (Test-Path -LiteralPath $journalTarget) { throw 'Rollback journal already exists; choose a new path.' }
$journal = [ordered]@{ schema_version = 1; status = 'ROLLBACK_PENDING'; action = 'Rollback'; source_receipt = $rollbackFull; source_receipt_sha256 = $receiptRead.sha256; source_manifest_sha256 = $manifestInfo.sha256; owner_sid = (Get-CurrentSid); captured_at_utc = [DateTime]::UtcNow.ToString('o'); thumbprint = $manifestInfo.thumbprint; stores = @($rollbackPlan | ForEach-Object { [ordered]@{ name = [string]$_.name; source_add_intended = [bool]$_.add_intended; source_added_by_run = [bool]$_.added_by_run; removed = $false; already_absent = $false; status = 'NOT_STARTED' } }) }
if (-not $PSCmdlet.ShouldProcess("LocalMachine Root and TrustedPublisher for $($manifestInfo.thumbprint)", "Remove only receipt-added test trust and journal to $journalTarget")) {
    [ordered]@{ status = 'WHATIF_OR_DECLINED'; action = 'Rollback'; thumbprint = $manifestInfo.thumbprint; eligible_stores = @($rollbackPlan | ForEach-Object { $_.name }); journal_path = $journalTarget } | ConvertTo-Json -Depth 5 | Write-Output
    return
}
Assert-Elevated
[void](Initialize-ReceiptDirectory)
$snapshot = $null
try {
    $snapshot = New-TrustSnapshot $manifestInfo
    Assert-PackageSignatures $snapshot.directory $manifestInfo
    Write-JsonJournal $journalTarget $journal
    for ($i = 0; $i -lt $rollbackPlan.Count; $i++) {
        $storeName = [string]$rollbackPlan[$i].name
        if (-not (Get-StoreContains $storeName $manifestInfo.thumbprint)) {
            $journal.stores[$i].already_absent = $true
            $journal.stores[$i].status = 'ALREADY_ABSENT'
        }
        else {
            # Reverify the snapshot immediately before this privileged change.
            $verifiedCertificate = Get-VerifiedSnapshotCertificate $snapshot $manifestInfo "removal from LocalMachine\$storeName"
            Remove-ExactCertificate $storeName $verifiedCertificate
            if (Get-StoreContains $storeName $manifestInfo.thumbprint) { $journal.stores[$i].status = 'REMOVE_VERIFICATION_FAILED'; $journal.status = 'ROLLBACK_PARTIAL'; Write-JsonJournal $journalTarget $journal; throw "Exact certificate remains in LocalMachine\$storeName after removal." }
            $journal.stores[$i].removed = $true
            $journal.stores[$i].status = 'REMOVED'
        }
        Write-JsonJournal $journalTarget $journal
    }
    $journal.status = 'ROLLBACK_COMPLETE'
    $journal.completed_at_utc = [DateTime]::UtcNow.ToString('o')
    Write-JsonJournal $journalTarget $journal
}
finally { Remove-StagingSnapshot $snapshot }
$null = Publish-ReceiptHash $journalTarget
$journal | ConvertTo-Json -Depth 8 | Write-Output

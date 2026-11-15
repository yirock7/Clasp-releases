<#
.SYNOPSIS
Stages the manifest-pinned AAP test package and finalizes only the exact selected node.
.DESCRIPTION
This deliberately uses pnputil /add-driver without /install. Binding is a manual
Device Manager step on the exact AAP Server node because a reviewed exact-node
driver-selection API is not available here. After that manual selection, this
script verifies the exact INF, SYS, CAT, instance, service, signer, and owner SID
before setting only that devnode's security property. All machine writes require
both -Apply and ShouldProcess. Rollback restores the saved exact-node security
descriptor and deletes only the manifest-matching staged OEM INF, without force
or uninstall switches, after the user manually restores the prior binding.

Time-of-check/time-of-use hardening:
 * The manifest is read once into memory, hashed against -ExpectedManifestSha256 and
   parsed from those same bytes; the path is never reopened. Package, certificate and
   INF content is likewise read once and hashed against the manifest.
 * StagePackage copies the verified package bytes into a fresh snapshot directory under
   %ProgramData%\Clasp\driver-staging\<guid> (SYSTEM and Administrators only, inheritance
   disabled, no reparse points), re-hashes the snapshot immediately before pnputil, and
   passes pnputil only the SNAPSHOT INF. The snapshot is removed at the end.
 * If pnputil publishes a package whose published name or contents are not the expected
   one, exactly that oemNN.inf is removed (pnputil /delete-driver, no force) before the
   script fails, and the removal is recorded in the receipt.

Receipts live in %ProgramData%\Clasp\driver-staging\receipts (same protection). -ReceiptPath
may be a bare file name or a path inside that directory. After every receipt-writing run
the script prints the receipt's SHA-256. Steps that consume a receipt (FinalizeManualBinding,
ConfirmDeviceRestart, Rollback) require that value as -ExpectedReceiptHash and reject any
receipt whose exact bytes differ - including a receipt where only before.device_security.sddl
was changed - before any machine state is touched. Run machine-changing actions elevated.
.PARAMETER ExpectedReceiptHash
SHA-256 of the exact receipt named by -StagingReceiptPath (FinalizeManualBinding),
-FinalizationReceiptPath (ConfirmDeviceRestart) or -RollbackReceiptPath (Rollback), as printed
by the run that wrote it.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedManifestSha256,
    [ValidateSet('Plan','StagePackage','FinalizeManualBinding','ConfirmDeviceRestart','Rollback')][string]$Action = 'Plan',
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$TargetInstanceId,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{12}$')][string]$BluetoothAddress,
    [ValidatePattern('^([A-Fa-f0-9]{12})?$')][string]$OtherAapAddress = '',
    [string]$OwnerSid,
    [string]$ReceiptPath,
    [string]$StagingReceiptPath,
    [string]$RollbackReceiptPath,
    [string]$FinalizationReceiptPath,
    [string]$TrustReceiptPath,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedReceiptHash,
    [ValidateSet('OwnerConfirmedSelectedNodeRestart','ObservedWindowsReboot')][string]$RestartEvidence,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:ExpectedHardwareId = 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_VID&0001004C_PID&2027'
$script:ExpectedCompatibleId = 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_GENERIC'
$script:ExpectedHardwareGuid = '{74EC2172-0BAD-4D01-8F77-997B2BE0722A}'
$script:ExpectedBluetoothAddress = $BluetoothAddress.ToUpperInvariant()
$script:OtherAapAddress = $OtherAapAddress.ToUpperInvariant()
$script:ServiceName = 'NTPodsAAP'

. (Join-Path $PSScriptRoot 'DriverStaging.ps1')

function Get-Sha256([string]$Path) { return (Get-BytesSha256 (Read-AllBytes ([IO.Path]::GetFullPath($Path)))) }

function Convert-BytesToText([byte[]]$Bytes) {
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE) { return [Text.Encoding]::Unicode.GetString($Bytes, 2, $Bytes.Length - 2) }
    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFE -and $Bytes[1] -eq 0xFF) { return [Text.Encoding]::BigEndianUnicode.GetString($Bytes, 2, $Bytes.Length - 2) }
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) { return [Text.Encoding]::UTF8.GetString($Bytes, 3, $Bytes.Length - 3) }
    return [Text.Encoding]::UTF8.GetString($Bytes)
}

function Resolve-ManifestChild([string]$Root, [string]$RelativePath, [string]$Label) {
    if ([IO.Path]::IsPathRooted($RelativePath)) { throw "$Label must be relative to the manifest directory." }
    $resolved = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "$Label escapes the manifest directory." }
    return $resolved
}

function Resolve-ManifestReference([string]$Root, [string]$Reference, [string]$Label) {
    if ([IO.Path]::IsPathRooted($Reference)) { return [IO.Path]::GetFullPath($Reference) }
    return [IO.Path]::GetFullPath((Join-Path $Root $Reference))
}

function Get-MapEntries([object]$Map) {
    if ($null -eq $Map) { return @() }
    return @($Map.PSObject.Properties | ForEach-Object { [ordered]@{ name = $_.Name; value = [string]$_.Value } })
}

function Read-TestPackageManifest([string]$Path, [string]$ExpectedHash, [switch]$AllowUntrustedSignature) {
    $manifestFull = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $manifestFull -PathType Leaf)) { throw "Test package manifest not found: $manifestFull" }
    # Single read: hash those exact bytes, then parse JSON from them. The path is never reopened.
    $manifestRead = Read-VerifiedManifest $manifestFull $ExpectedHash
    $manifestHash = $manifestRead.sha256
    $root = Split-Path -Parent $manifestFull
    $manifest = $manifestRead.manifest
    if ([int]$manifest.schema_version -ne 1 -or [string]$manifest.status -cne 'TEST_ONLY_SIGNED_PACKAGE' -or [string]$manifest.trust -cne 'TestOnly' -or [string]$manifest.microsoft_signature -cne 'NOT_PRESENT') { throw 'Refusing: manifest is not an explicitly test-only signed package.' }
    if ([string]$manifest.target.hardware_id -ine ('BTHENUM\' + $script:ExpectedHardwareGuid) -or [string]$manifest.target.service -cne $script:ServiceName) { throw 'Manifest target does not match the restricted AAP hardware ID and service.' }
    if (-not $manifest.package_directory -or -not $manifest.artifact_sha256) { throw 'Manifest lacks package_directory or artifact_sha256.' }
    if (-not $manifest.certificate -or -not $manifest.certificate.public_certificate_file -or -not $manifest.certificate.public_certificate_sha256 -or -not $manifest.certificate.thumbprint) { throw 'Manifest lacks its public test certificate identity.' }

    $packageDir = Resolve-ManifestChild $root ([string]$manifest.package_directory) 'Package directory'
    if (-not (Test-Path -LiteralPath $packageDir -PathType Container)) { throw 'Signed package directory is missing.' }
    $hashEntries = Get-MapEntries $manifest.artifact_sha256
    if ($hashEntries.Count -lt 3) { throw 'Manifest must hash the INF, SYS, and CAT files.' }
    $hashes = [ordered]@{}
    foreach ($entry in $hashEntries) {
        $name = [IO.Path]::GetFileName([string]$entry.name)
        if ($name -cne [string]$entry.name -or $name -notin @('NTPodsAAP.inf','NTPodsAAP.sys','NTPodsAAP.cat','NTPodsAAP.pdb')) { throw "Unexpected package artifact name: $($entry.name)" }
        if ($entry.value -notmatch '^[A-Fa-f0-9]{64}$') { throw "Invalid SHA-256 in manifest for $name." }
        if ($hashes.Contains($name)) { throw "Duplicate artifact entry: $name" }
        $hashes[$name] = $entry.value.ToUpperInvariant()
    }
    foreach ($required in @('NTPodsAAP.inf','NTPodsAAP.sys','NTPodsAAP.cat')) { if (-not $hashes.Contains($required)) { throw "Manifest does not hash $required." } }
    $actualFiles = @(Get-ChildItem -LiteralPath $packageDir -File | ForEach-Object { $_.Name } | Sort-Object)
    $manifestFiles = @($hashes.Keys | Sort-Object)
    if (($actualFiles -join "`n") -cne ($manifestFiles -join "`n")) {
        if (($actualFiles.Count -ne $manifestFiles.Count) -or (Compare-Object -ReferenceObject $actualFiles -DifferenceObject $manifestFiles -CaseSensitive:$false)) { throw 'Package directory file set does not exactly match the manifest hash list.' }
    }
    # Each package file is read exactly once; the verified bytes are kept so StagePackage can
    # write its snapshot from them instead of trusting the mutable source path again.
    $fileBytes = [ordered]@{}
    foreach ($name in $hashes.Keys) {
        $packageRead = Read-VerifiedBytes (Join-Path $packageDir $name) $hashes[$name] "Package artifact $name"
        $fileBytes[$name] = $packageRead.bytes
    }

    if (-not $manifest.source_build_manifest_path -or -not $manifest.source_build_manifest_sha256) { throw 'Manifest lacks its source build-manifest identity.' }
    $sourceManifest = Resolve-ManifestReference $root ([string]$manifest.source_build_manifest_path) 'Source build manifest'
    if (-not (Test-Path -LiteralPath $sourceManifest -PathType Leaf) -or (Get-Sha256 $sourceManifest) -ne ([string]$manifest.source_build_manifest_sha256).ToUpperInvariant()) { throw 'Source build-manifest file/hash is missing or mismatched.' }

    foreach ($key in @('catalog_generation','embedded_sys_signature','catalog_cms_signature','catalog_membership','exact_signer_thumbprint','signer_key_nonexportable')) {
        if (-not $manifest.validation.PSObject.Properties[$key] -or [string]$manifest.validation.$key -cne 'PASS') { throw "Signer validation '$key' is not PASS." }
    }
    $certPath = Resolve-ManifestChild $root ([string]$manifest.certificate.public_certificate_file) 'Public certificate file'
    if (-not (Test-Path -LiteralPath $certPath -PathType Leaf)) { throw 'Public certificate file/hash is missing or mismatched.' }
    $certBytes = Read-AllBytes $certPath
    if ((Get-BytesSha256 $certBytes) -ne ([string]$manifest.certificate.public_certificate_sha256).ToUpperInvariant()) { throw 'Public certificate file/hash is missing or mismatched.' }
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([byte[]]$certBytes)
    $thumbprint = ([string]$manifest.certificate.thumbprint -replace '\s','').ToUpperInvariant()
    if ($cert.Thumbprint.ToUpperInvariant() -ne $thumbprint -or ([string]$manifest.signer_thumbprint -replace '\s','').ToUpperInvariant() -ne $thumbprint) { throw 'Manifest signer thumbprints disagree with the public certificate.' }
    if ($cert.Subject -cne $cert.Issuer) { throw 'Manifest-pinned test certificate is not self-issued.' }
    if ($manifest.certificate.subject -and $cert.Subject -cne [string]$manifest.certificate.subject) { throw 'Certificate subject does not match the manifest.' }
    if (($cert.SerialNumber -replace '\s','').ToUpperInvariant() -ne ([string]$manifest.certificate.serial_number -replace '\s','').ToUpperInvariant()) { throw 'Certificate serial number does not match the manifest.' }

    $signatureResults = [ordered]@{}
    foreach ($fileName in @('NTPodsAAP.sys','NTPodsAAP.cat')) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $packageDir $fileName)
        $sigThumb = if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint.ToUpperInvariant() } else { $null }
        if ($sigThumb -ne $thumbprint) { throw "Embedded signer identity for $fileName does not match the manifest certificate." }
        if ($signature.Status -ne 'Valid' -and (-not $AllowUntrustedSignature -or [string]$signature.Status -cne 'UnknownError')) { throw "Authenticode verification failed for $fileName (status $($signature.Status))." }
        $signatureResults[$fileName] = [ordered]@{ status = [string]$signature.Status; thumbprint = $sigThumb; trust_chain = if ($signature.Status -eq 'Valid') { 'TRUSTED' } else { 'UNTRUSTED_TEST_CERTIFICATE' } }
    }
    $infText = Convert-BytesToText $fileBytes['NTPodsAAP.inf']
    if ($infText -notmatch '(?im)^\s*AddService\s*=\s*NTPodsAAP\s*,\s*%SPSVCINST_ASSOCSERVICE%\s*,\s*NTPodsAAP_Service_Inst\s*$' -or $infText -notmatch '(?im)^\s*\[\s*NTPodsAAP_Service_Inst\s*\]\s*$' -or $infText -notmatch '(?im)^\s*%DeviceDesc%\s*=\s*NTPodsAAP_Inst\s*,\s*BTHENUM\\\{74ec2172-0bad-4d01-8f77-997b2be0722a\}\s*$') { throw 'INF does not declare the expected AAP service and hardware ID.' }
    return [ordered]@{ path = $manifestFull; root = $root; sha256 = $manifestHash; package_directory = $packageDir; artifact_sha256 = $hashes; source_manifest = $sourceManifest; certificate = $cert; certificate_path = $certPath; certificate_sha256 = Get-BytesSha256 $certBytes; file_bytes = $fileBytes; thumbprint = $thumbprint; signature_results = $signatureResults; target = $manifest.target }
}

function Get-TestHostSnapshot {
    $collector = Join-Path $PSScriptRoot 'Get-TestHostState.ps1'
    if (-not (Test-Path -LiteralPath $collector -PathType Leaf)) { throw 'Get-TestHostState.ps1 is missing.' }
    try { $json = (& $collector -InstanceId $TargetInstanceId -BluetoothAddress $script:ExpectedBluetoothAddress -ExcludedBluetoothAddress $script:OtherAapAddress | Out-String) }
    catch { throw "Read-only test-host collector failed: $($_.Exception.Message)" }
    if (-not $json.Trim()) { throw 'Read-only test-host collector produced no JSON.' }
    return ($json | ConvertFrom-Json -ErrorAction Stop)
}

function Assert-TargetIdentity([object]$HostState, [string]$InstanceId) {
    $device = $HostState.aap_device
    if ([string]$device.query_status -cne 'READ' -or [string]$device.instance_id -cne $InstanceId) { throw 'Exact selected AAP Server devnode is not present/readable.' }
    if ($InstanceId -notmatch [regex]::Escape($script:ExpectedHardwareGuid) -or $InstanceId -notmatch [regex]::Escape($script:ExpectedBluetoothAddress) -or (($script:OtherAapAddress -ne '') -and $InstanceId -match [regex]::Escape($script:OtherAapAddress))) { throw 'Target instance ID does not match the selected AAP Server address.' }
    $entry = $device.device
    if ($entry.is_present.status -ne 'READ' -or $entry.is_present.value -ne $true -or $entry.problem_code.status -ne 'READ' -or [int]$entry.problem_code.value -ne 0) { throw 'Exact target presence/problem code is unavailable or indicates a problem.' }
    if (@($entry.hardware_ids.value) -notcontains $script:ExpectedHardwareId -or @($entry.compatible_ids.value) -notcontains $script:ExpectedCompatibleId) { throw 'Exact target HardwareIds/CompatibleIds do not match AAP Server.' }
    return $entry
}

function Assert-TestHostReady([object]$HostState, [string]$Owner) {
    if ([string]$HostState.host.sid -cne $Owner) { throw 'Owner SID must match the current Windows identity.' }
    if ($HostState.secure_boot.firmware.status -ne 'READ' -or $HostState.secure_boot.firmware.enabled -ne $false) { throw 'Test Mode setup is blocked until firmware Secure Boot is explicitly off and read successfully.' }
    if ($HostState.boot_integrity.status -ne 'READ' -or $HostState.boot_integrity.test_signing.status -ne 'READ' -or $HostState.boot_integrity.test_signing.value -ne $true) { throw 'Current BCD state does not verify Test Mode enabled.' }
    if ($HostState.boot_integrity.no_integrity_checks.status -notin @('READ','DEFAULTED') -or $HostState.boot_integrity.no_integrity_checks.value -ne $false) { throw 'Current BCD integrity-check state is not verified enabled.' }
    foreach ($key in @('debug','boot_debug')) {
        $entry = $HostState.boot_integrity.PSObject.Properties[$key].Value
        if ($entry.status -notin @('READ','DEFAULTED') -or $entry.value -ne $false) { throw "Current BCD $key state is not verified off." }
    }
    if ($HostState.hvci_vbs.query_status -ne 'READ' -or $HostState.bitlocker.status -ne 'READ') { throw 'HVCI/VBS or BitLocker protection state is unavailable; setup requires a fresh readable security baseline.' }
}

function Assert-TrustPresent([string]$Thumbprint) {
    foreach ($storeName in @('Root','TrustedPublisher')) {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($storeName, [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
        try {
            $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
            $present = @($store.Certificates | Where-Object { $_.Thumbprint -and $_.Thumbprint.ToUpperInvariant() -eq $Thumbprint }).Count -gt 0
        }
        finally { $store.Close() }
        if (-not $present) { throw "Exact test signer $Thumbprint is not in LocalMachine\$storeName; use Set-TestDriverTrust.ps1 first." }
    }
}

function Read-TrustReceipt([string]$Path, [object]$Package, [string]$Owner) {
    if (-not $Path) { throw '-TrustReceiptPath is required for package staging.' }
    # Must live in the administrator-only receipt directory; read once, hashed and parsed from the same bytes.
    $fullPath = Resolve-ReceiptLocation $Path '-TrustReceiptPath'
    Assert-NoReparsePath $fullPath
    Assert-AdminOnlyAcl (Get-ReceiptDirectory)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Trust receipt not found: $fullPath" }
    $trustBytes = Read-AllBytes $fullPath
    $trustHash = Get-BytesSha256 $trustBytes
    $receipt = ConvertFrom-Utf8JsonBytes $trustBytes
    if ([int]$receipt.schema_version -ne 1 -or [string]$receipt.status -cne 'TEST_ONLY_CERTIFICATE_TRUSTED' -or [string]$receipt.action -cne 'Import') { throw 'Trust receipt is not a completed test-only certificate import.' }
    if ([string]$receipt.manifest_sha256 -cne $Package.sha256 -or [string]$receipt.owner_sid -cne $Owner) { throw 'Trust receipt manifest or owner SID does not match this package/current user.' }
    if (([string]$receipt.certificate.thumbprint -replace '\s','').ToUpperInvariant() -cne $Package.thumbprint -or (([string]$receipt.certificate.sha256 -replace '\s','').ToUpperInvariant() -cne $Package.certificate_sha256)) { throw 'Trust receipt certificate identity does not match the package manifest.' }
    foreach ($storeName in @('Root','TrustedPublisher')) {
        $entries = @($receipt.stores | Where-Object { [string]$_.name -ceq $storeName -and $_.present_after -eq $true })
        if ($entries.Count -ne 1) { throw "Trust receipt does not confirm the exact certificate in LocalMachine\$storeName." }
    }
    Assert-TrustPresent $Package.thumbprint
    return [ordered]@{ path = $fullPath; sha256 = $trustHash; receipt = $receipt }
}

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run machine-changing setup actions from an elevated PowerShell session.' }
}

function Get-PnpApiType {
    $typeName = 'AppleDeviceHub_TestDriverSetupApi'
    if ($null -ne ($typeName -as [type])) { return ($typeName -as [type]) }
    $source = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class AppleDeviceHub_TestDriverSetupApi {
    const uint SPDRP_SECURITY_SDS = 0x00000018;
    const uint ERROR_INVALID_DATA = 13;
    const uint ERROR_INSUFFICIENT_BUFFER = 122;
    const uint ERROR_NOT_FOUND = 1168;
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiOpenDeviceInfoW(IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint OpenFlags, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, out uint PropertyRegDataType, IntPtr PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, IntPtr PropertyBuffer, uint PropertyBufferSize);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr LocalFree(IntPtr handle);
    static SP_DEVINFO_DATA OpenDevice(string instanceId, out IntPtr set) {
        set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfoList failed");
        SP_DEVINFO_DATA data = new SP_DEVINFO_DATA(); data.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
        if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data)) { int error=Marshal.GetLastWin32Error(); SetupDiDestroyDeviceInfoList(set); set=IntPtr.Zero; throw new Win32Exception(error, "SetupDiOpenDeviceInfoW failed for exact instance"); }
        return data;
    }
    public static string GetSecuritySddl(string instanceId) {
        IntPtr set; SP_DEVINFO_DATA data=OpenDevice(instanceId, out set);
        try {
            uint type, needed; bool ok=SetupDiGetDeviceRegistryPropertyW(set, ref data, SPDRP_SECURITY_SDS, out type, IntPtr.Zero, 0, out needed);
            int error=Marshal.GetLastWin32Error();
            if (!ok && (error==ERROR_INVALID_DATA || error==ERROR_NOT_FOUND)) return null;
            if (ok && needed==0) return String.Empty;
            if (error!=ERROR_INSUFFICIENT_BUFFER || needed==0) throw new Win32Exception(error, "Could not read exact devnode SPDRP_SECURITY_SDS");
            IntPtr buffer=Marshal.AllocHGlobal((int)needed);
            try {
                if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SPDRP_SECURITY_SDS, out type, buffer, needed, out needed)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read exact devnode SPDRP_SECURITY_SDS");
                if (type != 1) throw new InvalidOperationException("SPDRP_SECURITY_SDS was not REG_SZ.");
                return Marshal.PtrToStringUni(buffer).TrimEnd('\0');
            } finally { Marshal.FreeHGlobal(buffer); }
        } finally { SetupDiDestroyDeviceInfoList(set); }
    }
    public static void SetSecuritySddl(string instanceId, string sddl) {
        IntPtr set; SP_DEVINFO_DATA data=OpenDevice(instanceId, out set); IntPtr buffer=IntPtr.Zero;
        try {
            uint size=0;
            if (sddl!=null) { byte[] encoded=System.Text.Encoding.Unicode.GetBytes(sddl+"\0"); size=(uint)encoded.Length; buffer=Marshal.AllocHGlobal(encoded.Length); Marshal.Copy(encoded,0,buffer,encoded.Length); }
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, SPDRP_SECURITY_SDS, buffer, size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not set exact devnode SPDRP_SECURITY_SDS");
        } finally { if (buffer!=IntPtr.Zero) Marshal.FreeHGlobal(buffer); SetupDiDestroyDeviceInfoList(set); }
    }
}
'@
    try { Add-Type -TypeDefinition $source -Language CSharp -ErrorAction Stop }
    catch {
        if ($null -eq ($typeName -as [type])) { throw }
    }
    return ('AppleDeviceHub_TestDriverSetupApi' -as [type])
}

function Assert-OwnerSid([string]$RequestedSid) {
    $identitySid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if (-not $RequestedSid) { $RequestedSid = $identitySid }
    try { $parsed = [Security.Principal.SecurityIdentifier]::new($RequestedSid) } catch { throw 'Owner SID is not a valid Windows SID.' }
    if ($parsed.Value -cne $identitySid) { throw 'Owner SID must exactly match the current interactive/elevated Windows identity.' }
    if ($parsed.Value -notmatch '^S-1-5-21-') { throw 'Owner SID must be a regular local/domain account SID.' }
    return $parsed.Value
}

function Assert-ValidSddl {
    # A receipt-supplied descriptor is parsed before it can reach SetSecuritySddl; $null means "no explicit descriptor"
    # (a device with no explicit security). The parameter is deliberately untyped: [string]$Sddl would turn $null into ''
    # and the guard below would never fire.
    param([AllowNull()][AllowEmptyString()][object]$Sddl)
    if ($null -eq $Sddl) { return }
    $Sddl = [string]$Sddl
    try { [void][Security.AccessControl.RawSecurityDescriptor]::new($Sddl) }
    catch { throw 'Receipt before.device_security.sddl is not a valid security descriptor; refusing to apply it.' }
}

function Get-SddlSha256([string]$Sddl) {
    if ($null -eq $Sddl) { return $null }
    return Get-BytesSha256 ([Text.Encoding]::UTF8.GetBytes($Sddl))
}

function Get-DriverPackageUsers([string]$PublishedInf) {
    $all = @(Get-CimInstance -ClassName Win32_PnPSignedDriver -ErrorAction Stop)
    return @($all | Where-Object { [string]$_.InfName -ieq $PublishedInf } | Select-Object DeviceID,DeviceName,InfName,DriverProviderName,DriverVersion,IsSigned)
}

function Export-DriverPackage([string]$PublishedInf, [string]$Destination) {
    $pnputil = Join-Path $env:WINDIR 'System32\pnputil.exe'
    if (-not (Test-Path -LiteralPath $pnputil)) { throw 'pnputil.exe was not found.' }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $output = @(& $pnputil /export-driver $PublishedInf $Destination 2>&1 | ForEach-Object { [string]$_ })
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "pnputil /export-driver failed with exit $exitCode. Output: $($output -join ' | ')" }
    return [ordered]@{ exit_code = $exitCode; output = $output; directory = $Destination }
}

function Get-DriverStoreInventory {
    $pnputil = Join-Path $env:WINDIR 'System32\pnputil.exe'
    if (-not (Test-Path -LiteralPath $pnputil)) { throw 'pnputil.exe was not found.' }
    $output = @(& $pnputil /enum-drivers 2>&1 | ForEach-Object { [string]$_ })
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "pnputil /enum-drivers failed with exit $exitCode. Output: $($output -join ' | ')" }
    $entries = New-Object System.Collections.Generic.List[object]
    $current = $null
    foreach ($line in $output) {
        if ($line -match '^\s*Published Name\s*:\s*(oem\d+\.inf)\s*$') {
            if ($null -ne $current) { $entries.Add($current) }
            $current = [ordered]@{ published_inf = $Matches[1].ToLowerInvariant(); original_name = $null; provider_name = $null }
        }
        elseif ($null -ne $current -and $line -match '^\s*Original Name\s*:\s*(.+?)\s*$') { $current.original_name = $Matches[1].Trim() }
        elseif ($null -ne $current -and $line -match '^\s*Provider Name\s*:\s*(.+?)\s*$') { $current.provider_name = $Matches[1].Trim() }
    }
    if ($null -ne $current) { $entries.Add($current) }
    if ($entries.Count -eq 0) { throw 'Could not parse Published Name fields from pnputil /enum-drivers; package staging is refused.' }
    return [ordered]@{ exit_code = $exitCode; output = $output; packages = @($entries.ToArray()) }
}

function Find-ExactPackageInStore([object]$Inventory, [object]$Package, [string]$EvidenceBasePath) {
    $candidates = @($Inventory.packages | Where-Object { [string]$_.original_name -ieq 'NTPodsAAP.inf' -and [string]$_.provider_name -ieq 'NTPods' })
    $matches = New-Object System.Collections.Generic.List[object]
    foreach ($candidate in $candidates) {
        $destination = $EvidenceBasePath + '.' + [string]$candidate.published_inf
        $exportInfo = Export-DriverPackage ([string]$candidate.published_inf) $destination
        $exportInf = Get-ChildItem -LiteralPath $destination -Filter 'NTPodsAAP.inf' -File -Recurse | Select-Object -First 1
        if ($null -eq $exportInf) { continue }
        $exportDirectory = $exportInf.Directory.FullName
        $hashesMatch = $true
        foreach ($name in @('NTPodsAAP.inf','NTPodsAAP.sys','NTPodsAAP.cat')) {
            $path = Join-Path $exportDirectory $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Sha256 $path) -ne $Package.artifact_sha256[$name]) { $hashesMatch = $false; break }
        }
        if (-not $hashesMatch) { continue }
        $sysSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $exportDirectory 'NTPodsAAP.sys')
        $catSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $exportDirectory 'NTPodsAAP.cat')
        if ($sysSignature.Status -eq 'Valid' -and $catSignature.Status -eq 'Valid' -and $sysSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -eq $Package.thumbprint -and $catSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -eq $Package.thumbprint) {
            $matches.Add([ordered]@{ published_inf = [string]$candidate.published_inf; export = $exportInfo; export_package_directory = $exportDirectory })
        }
    }
    return @($matches.ToArray())
}

function Assert-ExportMatchesManifest([string]$ExportDirectory, [object]$Package) {
    $exportInf = Get-ChildItem -LiteralPath $ExportDirectory -Filter 'NTPodsAAP.inf' -File -Recurse | Select-Object -First 1
    if ($null -eq $exportInf) { throw 'Exported OEM package lacks NTPodsAAP.inf.' }
    $exportDir = $exportInf.Directory.FullName
    foreach ($name in @('NTPodsAAP.inf','NTPodsAAP.sys','NTPodsAAP.cat')) {
        $path = Join-Path $exportDir $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Sha256 $path) -ne $Package.artifact_sha256[$name]) { throw "Exported OEM package does not match the manifest: $name" }
    }
    return $exportDir
}

function New-PackageReceiptBase([object]$Package, [object]$HostState, [object]$Device, [string]$Owner, [string]$ActionName) {
    $api = Get-PnpApiType
    $beforeSecurity = $api::GetSecuritySddl($TargetInstanceId)
    $inf = $Device.driver_inf_path
    $service = $Device.service
    $binding = [ordered]@{
        state = [string]$Device.binding_state; service = $service; inf = $inf; driver_version = $Device.driver_version
        driver_provider = $Device.driver_provider; signed_driver_package = $Device.signed_driver_package
    }
    return [ordered]@{
        schema_version = 1; status = 'PENDING'; action = $ActionName; captured_at_utc = [DateTime]::UtcNow.ToString('o')
        manifest_path = $Package.path; manifest_sha256 = $Package.sha256; package_artifact_sha256 = $Package.artifact_sha256; package_hashes = $Package.artifact_sha256
        target_instance_id = $TargetInstanceId; owner_sid = $Owner
        target = [ordered]@{ instance_id = $TargetInstanceId; hardware_id = $script:ExpectedHardwareId; compatible_id = $script:ExpectedCompatibleId; bluetooth_address = $script:ExpectedBluetoothAddress; owner_sid = $Owner }
        before = [ordered]@{ host_state = $HostState; binding = $binding; device_security = [ordered]@{ status = if ($null -eq $beforeSecurity) { 'NOT_SET' } else { 'READ' }; sddl = $beforeSecurity; sha256 = Get-SddlSha256 $beforeSecurity } }
        package = [ordered]@{ package_directory = $Package.package_directory; published_inf = $null; verified = $true; was_present_before = $null; added_by_run = $null }
        trust = [ordered]@{ thumbprint = $Package.thumbprint; local_machine_root = $true; local_machine_trusted_publisher = $true }
        rollback = [ordered]@{ prior_binding_restoration = 'Use the Device Manager manual route named in this receipt; do not delete pairing.'; package_deletion = 'Only after the target is manually restored and no devnode uses published_inf; pnputil is invoked without /uninstall or /force.' }
    }
}

function New-PackageSnapshot([object]$Package) {
    # Writes the already-verified package bytes into administrator-only, non-reparse storage.
    $snapshot = New-StagingSnapshot
    try {
        foreach ($name in $Package.file_bytes.Keys) { [void](Add-SnapshotFile $snapshot $name $Package.file_bytes[$name] $Package.artifact_sha256[$name]) }
        return $snapshot
    }
    catch { Remove-StagingSnapshot $snapshot; throw }
}

function Assert-SnapshotSignatures([object]$Snapshot, [object]$Package) {
    foreach ($fileName in @('NTPodsAAP.sys','NTPodsAAP.cat')) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $Snapshot.directory $fileName)
        $sigThumb = if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint.ToUpperInvariant() } else { $null }
        if ($sigThumb -ne $Package.thumbprint -or $signature.Status -ne 'Valid') { throw "Snapshot signature for $fileName is not a valid signature by the exact manifest test signer (status $($signature.Status))." }
    }
}

function Remove-UnexpectedStagedPackages([string[]]$PublishedNames, [string[]]$ProtectedNames) {
    # Removes exactly the named oemNN.inf packages that this run just caused pnputil to publish and
    # that are not the expected package. No /force and no /uninstall; names that existed before the
    # run, or that are not strictly oemNN.inf, are never touched.
    $records = New-Object System.Collections.Generic.List[object]
    $pnputil = Join-Path $env:WINDIR 'System32\pnputil.exe'
    foreach ($name in @($PublishedNames | Select-Object -Unique)) {
        if ($name -notmatch '^oem\d+\.inf$' -or $name -in $ProtectedNames) {
            $records.Add([ordered]@{ published_inf = $name; removed = $false; reason = 'SKIPPED_NOT_A_NEW_OEM_PACKAGE' })
            continue
        }
        $output = @(& $pnputil /delete-driver $name 2>&1 | ForEach-Object { [string]$_ })
        $exitCode = $LASTEXITCODE
        $records.Add([ordered]@{ published_inf = $name; removed = ($exitCode -eq 0); exit_code = $exitCode; output = $output; command = "pnputil.exe /delete-driver $name" })
    }
    return @($records.ToArray())
}

function Get-AttributedUnexpectedPackages([string]$PrintedName, [string[]]$BeforeNames, [string[]]$AfterNames, [object[]]$NewMatches, [string]$ExpectedName) {
    # Which new packages may be removed? Only those positively attributed to this run: the Published Name pnputil
    # printed for this /add-driver call, or a new package whose original INF name and hash match this run's snapshot
    # (Find-ExactPackageInStore already requires both). Any other package that appeared between the two inventories
    # (for example one Windows Update staged meanwhile) is only reported, never deleted.
    $before = @($BeforeNames | Where-Object { $_ })
    $newNames = @($AfterNames | Where-Object { $_ -and $_ -notin $before })
    $attributed = @()
    if ($PrintedName) { $attributed += $PrintedName }
    foreach ($m in @($NewMatches | Where-Object { $_ })) { $attributed += [string]$m.published_inf }
    $attributed = @($attributed | Where-Object { $_ -and $_ -notin $before } | Select-Object -Unique)
    $remove = @($attributed | Where-Object { $_ -cne $ExpectedName })
    $unrelated = @($newNames | Where-Object { $_ -notin $attributed })
    return [ordered]@{ remove = $remove; unrelated_not_removed = $unrelated }
}

function Get-ManualBindingInstructions([string]$InfPath) {
    return @(
        'Open Device Manager (devmgmt.msc) and locate the AAP Server Bluetooth service node.',
        'Before changing it, open Properties > Details > Device instance path and confirm the full instance ID shown in this receipt; the Bluetooth address must be ' + $script:ExpectedBluetoothAddress + '.',
        'Do not select a nearby AAP Client node (a different Bluetooth address), and do not remove a Bluetooth device or pairing.',
        'Choose Update driver > Browse my computer > Let me pick > Have Disk, then select this exact INF (the staging snapshot used for pnputil was already removed): ' + $InfPath,
        'Confirm the displayed provider is NTPods and the device is NTPods AAP (AirPods). Leave the package staged if the dialog proposes another node.',
        'After Device Manager finishes, run Install-TestDriver.ps1 -Action FinalizeManualBinding with this stage receipt, its printed SHA-256 as -ExpectedReceiptHash, and the trust receipt. The script verifies the exact node/package before setting its owner ACL.',
        'After ACL finalization, restart only this selected node in Device Manager (disable then enable it) or reboot Windows before testing the normal-user application. The script does not restart the device or open a driver handle.'
    )
}

if ($Action -ne 'Plan' -and -not $Apply) { throw 'No action taken. Supply the explicit -Apply opt-in; -WhatIf is supported.' }
if ($TargetInstanceId -notmatch [regex]::Escape($script:ExpectedHardwareGuid) -or $TargetInstanceId -notmatch [regex]::Escape($script:ExpectedBluetoothAddress) -or (($script:OtherAapAddress -ne '') -and $TargetInstanceId -match [regex]::Escape($script:OtherAapAddress))) { throw 'TargetInstanceId must match the selected AAP Server instance (service GUID and Bluetooth address). A nearby AAP Client is excluded.' }
$owner = Assert-OwnerSid $OwnerSid
$allowUntrustedPackageSignatures = $Action -in @('Plan','Rollback')
$package = Read-TestPackageManifest -Path $ManifestPath -ExpectedHash $ExpectedManifestSha256 -AllowUntrustedSignature:$allowUntrustedPackageSignatures
$hostState = Get-TestHostSnapshot
$device = Assert-TargetIdentity $hostState $TargetInstanceId

if ($Action -eq 'Plan') {
    $securityRead = [ordered]@{ status = 'UNAVAILABLE'; sddl = $null; sha256 = $null; error = $null }
    try {
        $api = Get-PnpApiType
        $currentSddl = $api::GetSecuritySddl($TargetInstanceId)
        $securityRead.status = if ($null -eq $currentSddl) { 'NOT_SET' } else { 'READ' }
        $securityRead.sddl = $currentSddl
        $securityRead.sha256 = Get-SddlSha256 $currentSddl
    }
    catch { $securityRead.error = $_.Exception.Message }
    $plan = [ordered]@{
        schema_version = 1; status = 'READ_ONLY_PLAN'; manifest_sha256 = $package.sha256; package_artifact_sha256 = $package.artifact_sha256
        target_instance_id = $TargetInstanceId; owner_sid = $owner
        target = [ordered]@{ instance_id = $TargetInstanceId; bluetooth_address = $script:ExpectedBluetoothAddress; binding_state = $device.binding_state; current_inf = $device.driver_inf_path; owner_sid = $owner }
        signer = [ordered]@{ thumbprint = $package.thumbprint; embedded_signatures = $package.signature_results }
        host_security = [ordered]@{ secure_boot = $hostState.secure_boot; hvci_vbs = $hostState.hvci_vbs; bitlocker = $hostState.bitlocker; boot_integrity = $hostState.boot_integrity; selected_device_security = $securityRead }
        manual_binding_required = $true; planned_stage_command = 'pnputil.exe /add-driver <manifest NTPodsAAP.inf> (no /install switch)'
        next_actions = @('After the owner verifies host configuration, trust only this signer with Set-TestDriverTrust.ps1.', 'Stage with -Action StagePackage -Apply and pass the resulting trust receipt.', 'Use Device Manager Have Disk on the exact instance path above.', 'Finalize with -Action FinalizeManualBinding -Apply to set and verify the selected-owner ACL.', 'Restart only this selected node or reboot Windows, then use -Action ConfirmDeviceRestart to emit a BOUND receipt. Normal-user access remains unverified until the observation test.')
    }
    $plan | ConvertTo-Json -Depth 12 | Write-Output
    return
}

if ($Action -eq 'StagePackage') {
    Assert-TestHostReady $hostState $owner
    $trustInfo = Read-TrustReceipt $TrustReceiptPath $package $owner
    if ($device.binding_state -notin @('NO_DRIVER_BOUND','BOUND')) { throw 'Current exact-node binding is not provably readable; refusing to stage.' }
    if (-not $ReceiptPath) { throw '-ReceiptPath is required for StagePackage so the rollback baseline has a known location.' }
    $receiptTarget = Resolve-ReceiptLocation $ReceiptPath '-ReceiptPath'
    if (Test-Path -LiteralPath $receiptTarget) { throw 'Receipt path already exists; choose a new path.' }
    # Source INF path is only for the manual Device Manager step; pnputil is given the SNAPSHOT INF below.
    $infPath = Join-Path $package.package_directory 'NTPodsAAP.inf'
    $receipt = New-PackageReceiptBase $package $hostState $device $owner 'StagePackage'
    $receipt.trust = [ordered]@{ thumbprint = $package.thumbprint; local_machine_root = $true; local_machine_trusted_publisher = $true; trust_receipt_path = $trustInfo.path; trust_receipt_sha256 = $trustInfo.sha256 }
    $receipt.manual_binding = [ordered]@{ required = $true; instructions = Get-ManualBindingInstructions $infPath }
    $receipt.stage = [ordered]@{ command = 'pnputil.exe /add-driver <administrator-only snapshot of the exact-manifest INF>'; included_install_switch = $false; exit_code = $null; output = @(); published_inf = $null; unexpected_package_cleanup = @() }
    if (-not $PSCmdlet.ShouldProcess($TargetInstanceId, "Stage exact manifest package (from a verified administrator-only snapshot) without binding; write journal $receiptTarget")) {
        [ordered]@{ status = 'WHATIF_OR_DECLINED'; action = 'StagePackage'; command = 'pnputil.exe /add-driver <administrator-only snapshot of the exact-manifest INF>'; included_install_switch = $false; receipt_path = $receiptTarget } | ConvertTo-Json -Depth 8 | Write-Output
        return
    }
    Assert-Elevated
    [void](Initialize-ReceiptDirectory)
    $snapshot = $null
    try {
    $beforeInventory = Get-DriverStoreInventory
    $matchingBefore = @(Find-ExactPackageInStore $beforeInventory $package ($receiptTarget + '.store-before'))
    if ($matchingBefore.Count -gt 1) { throw 'Multiple existing driver-store packages match the exact test manifest; refusing ambiguous stage/rollback ownership.' }
    $beforePublishedNames = @($beforeInventory.packages | ForEach-Object { [string]$_.published_inf })
    $receipt.before.driver_store_inventory = [ordered]@{ pnputil_exit_code = $beforeInventory.exit_code; published_inf_names = $beforePublishedNames; matching_test_packages = $matchingBefore }
    if ($matchingBefore.Count -eq 1) {
        $receipt.package.published_inf = [string]$matchingBefore[0].published_inf
        $receipt.package.was_present_before = $true
        $receipt.package.added_by_run = $false
        $receipt.package.preexisting_store_export = $matchingBefore[0].export
        $receipt.stage.exit_code = $null
        $receipt.stage.skipped_because_preexisting = $true
        $receipt.stage.output = @('Exact manifest-matching package was already in the driver store; pnputil /add-driver was skipped.')
    }
    else {
        $receipt.package.was_present_before = $false
        $receipt.package.added_by_run = $false
    }
    Write-JsonJournal $receiptTarget $receipt
    try {
        if ($device.binding_state -eq 'BOUND' -and [string]$device.driver_inf_path.value -match '^oem\d+\.inf$') {
            $priorExport = $receiptTarget + '.previous-driver'
            $receipt.before['previous_package_export'] = Export-DriverPackage ([string]$device.driver_inf_path.value) $priorExport
            $receipt.before['previous_package_export'].published_inf = [string]$device.driver_inf_path.value
            Write-JsonJournal $receiptTarget $receipt
        }
        if ($matchingBefore.Count -eq 0) {
            $pnputil = Join-Path $env:WINDIR 'System32\pnputil.exe'
            if (-not (Test-Path -LiteralPath $pnputil)) { throw 'pnputil.exe was not found.' }
            # Snapshot the already-verified bytes into administrator-only storage, check signatures on
            # the copies, and reverify immediately before pnputil. pnputil only ever sees the snapshot INF.
            $snapshot = New-PackageSnapshot $package
            Assert-SnapshotSignatures $snapshot $package
            $snapshotInf = Join-Path $snapshot.directory 'NTPodsAAP.inf'
            $receipt.stage.snapshot_directory = $snapshot.directory
            $receipt.stage.snapshot_sha256 = $snapshot.files
            Assert-SnapshotIntact $snapshot 'pnputil /add-driver'
            $stageOutput = @(& $pnputil /add-driver $snapshotInf 2>&1 | ForEach-Object { [string]$_ })
            $exitCode = $LASTEXITCODE
            $receipt.stage.exit_code = $exitCode
            $receipt.stage.output = $stageOutput
            foreach ($line in $stageOutput) { if ($line -match '(?i)\bPublished Name\s*:\s*(oem\d+\.inf)\b') { $receipt.stage.published_inf = $Matches[1].ToLowerInvariant() } }
            $afterInventory = Get-DriverStoreInventory
            $matchingAfter = @(Find-ExactPackageInStore $afterInventory $package ($receiptTarget + '.store-after'))
            $afterNames = @($afterInventory.packages | ForEach-Object { [string]$_.published_inf })
            $newNames = @($afterNames | Where-Object { $_ -notin $beforePublishedNames })
            $newMatches = @($matchingAfter | Where-Object { $_.published_inf -notin $beforePublishedNames })
            $printedName = [string]$receipt.stage.published_inf
            # The expected package is the single NEW hash-matching package whose name agrees with what pnputil printed.
            $expectedName = $null
            if ($newMatches.Count -eq 1 -and (-not $printedName -or $printedName -ceq [string]$newMatches[0].published_inf)) { $expectedName = [string]$newMatches[0].published_inf }
            $attribution = Get-AttributedUnexpectedPackages $printedName $beforePublishedNames $afterNames $newMatches $expectedName
            $unexpectedNames = @($attribution.remove)
            $receipt.stage.unrelated_new_packages_not_removed = @($attribution.unrelated_not_removed)
            if ($expectedName) {
                $receipt.package.published_inf = $expectedName
                $receipt.package.added_by_run = $true
                $receipt.package.store_export = $newMatches[0].export
            }
            elseif ($matchingAfter.Count -eq 1 -and [string]$matchingAfter[0].published_inf -in $beforePublishedNames) {
                $receipt.package.published_inf = [string]$matchingAfter[0].published_inf
                $receipt.package.was_present_before = $true
                $receipt.package.added_by_run = $false
                $receipt.package.preexisting_store_export = $matchingAfter[0].export
            }
            $receipt.after_driver_store_inventory = [ordered]@{ pnputil_exit_code = $afterInventory.exit_code; published_inf_names = $afterNames; matching_test_packages = $matchingAfter }
            if ($unexpectedNames.Count -gt 0) {
                # pnputil published something attributable to this run other than the expected package: remove exactly those packages.
                $receipt.stage.unexpected_package_cleanup = @(Remove-UnexpectedStagedPackages $unexpectedNames $beforePublishedNames)
                $receipt.status = 'STAGE_FAILED_UNEXPECTED_PACKAGE_REMOVED'
                Write-JsonJournal $receiptTarget $receipt
                throw "pnputil published unexpected package(s) [$($unexpectedNames -join ', ')] whose name or contents do not match the expected manifest package; exactly those packages were removed (see receipt stage.unexpected_package_cleanup)."
            }
            Write-JsonJournal $receiptTarget $receipt
            if ($exitCode -ne 0) { throw "pnputil /add-driver failed with exit $exitCode." }
            if (-not $receipt.package.published_inf) { throw 'Package was staged, but the exact published OEM INF could not be proven; review the journal and pnputil output before proceeding.' }
        }
        else {
            $receipt.after_driver_store_inventory = $receipt.before.driver_store_inventory
        }
        $receipt.stage.published_inf = $receipt.package.published_inf
        $afterStage = Get-TestHostSnapshot
        $afterDevice = Assert-TargetIdentity $afterStage $TargetInstanceId
        if ([string]$afterDevice.driver_inf_path.value -and [string]$afterDevice.driver_inf_path.value -ine [string]$device.driver_inf_path.value) { throw 'The exact node binding changed during /add-driver staging; stop and review the receipt.' }
        $receipt.after_stage = [ordered]@{ host_state = $afterStage; binding = [ordered]@{ state = $afterDevice.binding_state; inf = $afterDevice.driver_inf_path; service = $afterDevice.service } }
        $receipt.status = 'STAGED_WAITING_MANUAL_BIND'
        $receipt.completed_at_utc = [DateTime]::UtcNow.ToString('o')
        Write-JsonJournal $receiptTarget $receipt
    }
    catch {
        $stageFailure = $_.Exception.Message
        $receipt.status = 'STAGE_FAILED_OR_REQUIRES_REVIEW'
        $receipt.error = $stageFailure
        $failureHash = ''
        try { Write-JsonJournal $receiptTarget $receipt; $failureHash = ' Receipt SHA-256: ' + (Publish-ReceiptHash $receiptTarget) + '.' } catch { }
        throw "Stage action failed; inspect the recovery journal at $receiptTarget.$failureHash $stageFailure"
    }
    }
    finally { Remove-StagingSnapshot $snapshot }
    $null = Publish-ReceiptHash $receiptTarget
    $receipt | ConvertTo-Json -Depth 14 | Write-Output
    return
}

if ($Action -eq 'FinalizeManualBinding') {
    Assert-TestHostReady $hostState $owner
    $trustInfo = Read-TrustReceipt $TrustReceiptPath $package $owner
    if (-not $StagingReceiptPath -or -not $ReceiptPath) { throw 'FinalizeManualBinding requires -StagingReceiptPath and a new -ReceiptPath.' }
    # Authenticate the stage receipt: admin-only location and exact-bytes SHA-256 before any field is used.
    $stageRead = Read-HashedReceipt $StagingReceiptPath $ExpectedReceiptHash 'Stage receipt'
    $stageReceiptFull = $stageRead.path
    $stageReceipt = $stageRead.receipt
    if ([int]$stageReceipt.schema_version -ne 1 -or [string]$stageReceipt.status -cne 'STAGED_WAITING_MANUAL_BIND' -or [string]$stageReceipt.manifest_sha256 -cne $package.sha256 -or [string]$stageReceipt.target.instance_id -cne $TargetInstanceId -or [string]$stageReceipt.target.owner_sid -cne $owner) { throw 'Stage receipt does not match this manifest, owner, target, or successful stage action.' }
    if ([string]$stageReceipt.trust.trust_receipt_path -cne $trustInfo.path -or [string]$stageReceipt.trust.trust_receipt_sha256 -cne $trustInfo.sha256) { throw 'Finalization trust receipt differs from the one recorded during exact package staging.' }
    if ($device.binding_state -cne 'BOUND' -or [string]$device.service.value -cne $script:ServiceName -or [string]$device.driver_inf_path.value -notmatch '^oem\d+\.inf$') { throw 'Manual binding is not verified on the exact AAP Server devnode.' }
    $signedDetails = $device.signed_driver_package.details
    if ($null -eq $signedDetails -or [string]$signedDetails.InfName -ine [string]$device.driver_inf_path.value -or [string]$signedDetails.DriverProviderName -cne 'NTPods' -or [string]$signedDetails.IsSigned -ine 'True') { throw 'PnP signed-driver metadata does not prove the exact staged NTPods package.' }
    $publishedInf = [string]$device.driver_inf_path.value
    $exportPath = [IO.Path]::GetTempPath() + 'AppleDeviceHub-AAP-' + [Guid]::NewGuid().ToString('N')
    $exportInfo = Export-DriverPackage $publishedInf $exportPath
    $exportPackageDirectory = Assert-ExportMatchesManifest $exportPath $package
    $exportSysSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $exportPackageDirectory 'NTPodsAAP.sys')
    $exportCatSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $exportPackageDirectory 'NTPodsAAP.cat')
    if ($exportSysSignature.Status -ne 'Valid' -or $exportSysSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $package.thumbprint -or $exportCatSignature.Status -ne 'Valid' -or $exportCatSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $package.thumbprint) { throw 'Exported installed package signatures do not match the exact test signer.' }

    $receiptTarget = Resolve-ReceiptLocation $ReceiptPath '-ReceiptPath'
    if (Test-Path -LiteralPath $receiptTarget) { throw 'Final receipt path already exists; choose a new path.' }
    $api = Get-PnpApiType
    $priorSecurity = if ($stageReceipt.before.device_security.sddl) { [string]$stageReceipt.before.device_security.sddl } else { $null }
    Assert-ValidSddl $priorSecurity
    $currentSecurity = $api::GetSecuritySddl($TargetInstanceId)
    $desiredSddl = 'D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;' + $owner + ')'
    $receipt = [ordered]@{
        schema_version = 1; status = 'ACL_PENDING'; action = 'FinalizeManualBinding'; captured_at_utc = [DateTime]::UtcNow.ToString('o')
        manifest_path = $package.path; manifest_sha256 = $package.sha256; package_artifact_sha256 = $package.artifact_sha256; package_hashes = $package.artifact_sha256
        target_instance_id = $TargetInstanceId; owner_sid = $owner
        staging_receipt = $stageReceiptFull; staging_receipt_sha256 = $stageRead.sha256; target = $stageReceipt.target; package = [ordered]@{ published_inf = $publishedInf; provider = [string]$signedDetails.DriverProviderName; service = [string]$device.service.value; driver_version = [string]$signedDetails.DriverVersion; was_present_before = [bool]$stageReceipt.package.was_present_before; added_by_run = [bool]$stageReceipt.package.added_by_run; exported_package = $exportInfo; exported_package_sha256 = $package.artifact_sha256 }
        trust = $stageReceipt.trust
        before = [ordered]@{ host_state = $hostState; binding = $stageReceipt.before.binding; device_security = $stageReceipt.before.device_security; current_security_sddl = $currentSecurity; current_security_sha256 = Get-SddlSha256 $currentSecurity }
        after = [ordered]@{ host_state = $null; binding = $null; device_security = [ordered]@{ sddl = $desiredSddl; sha256 = Get-SddlSha256 $desiredSddl; verified = $false }; owner_access_runtime = 'UNVERIFIED'; restart_required = $true; pending_restart = $true }
        restart_evidence = [ordered]@{ status = 'PENDING_OWNER_RESTART'; method = 'Manually restart this exact devnode in Device Manager or reboot Windows after ACL finalization.' }
        rollback = $stageReceipt.rollback
    }
    if (-not $PSCmdlet.ShouldProcess($TargetInstanceId, "Set SPDRP_SECURITY_SDS on this exact AAP Server node to owner $owner; write journal $receiptTarget")) {
        [ordered]@{ status = 'WHATIF_OR_DECLINED'; action = 'FinalizeManualBinding'; target_instance_id = $TargetInstanceId; owner_sid = $owner; desired_sddl = $desiredSddl; receipt_path = $receiptTarget } | ConvertTo-Json -Depth 6 | Write-Output
        return
    }
    Assert-Elevated
    [void](Initialize-ReceiptDirectory)
    Write-JsonJournal $receiptTarget $receipt
    try {
        $api::SetSecuritySddl($TargetInstanceId, $desiredSddl)
        $readBack = $api::GetSecuritySddl($TargetInstanceId)
        if ($readBack -cne $desiredSddl -or (Get-SddlSha256 $readBack) -ne (Get-SddlSha256 $desiredSddl)) { throw 'SPDRP_SECURITY_SDS read-back did not match the selected owner ACL.' }
        $afterState = Get-TestHostSnapshot
        $afterDevice = Assert-TargetIdentity $afterState $TargetInstanceId
        if ([string]$afterDevice.driver_inf_path.value -ine $publishedInf -or [string]$afterDevice.service.value -cne $script:ServiceName) { throw 'Selected target binding changed during ACL provisioning.' }
        $receipt.after.host_state = $afterState
        $receipt.after.binding = [ordered]@{ state = $afterDevice.binding_state; inf = $afterDevice.driver_inf_path; service = $afterDevice.service; signed_driver_package = $afterDevice.signed_driver_package }
        $receipt.after.device_security.verified = $true
        $receipt.status = 'BOUND_WAITING_FOR_DEVICE_RESTART'
        $receipt.completed_at_utc = [DateTime]::UtcNow.ToString('o')
        Write-JsonJournal $receiptTarget $receipt
    }
    catch {
        $failure = $_.Exception.Message
        try { $api::SetSecuritySddl($TargetInstanceId, $priorSecurity); $receipt.rollback.acl_restore_after_failure = 'PASS' }
        catch { $receipt.rollback.acl_restore_after_failure = 'FAIL_REQUIRES_REVIEW' }
        $receipt.status = 'FINALIZE_FAILED_ROLLBACK_ATTEMPTED'
        $receipt.error = $failure
        $failureHash = ''
        try { Write-JsonJournal $receiptTarget $receipt; $failureHash = ' Receipt SHA-256: ' + (Publish-ReceiptHash $receiptTarget) + '.' } catch { }
        throw "Selected-node ACL finalization failed; journal: $receiptTarget.$failureHash $failure"
    }
    $null = Publish-ReceiptHash $receiptTarget
    $receipt | ConvertTo-Json -Depth 14 | Write-Output
    return
}

if ($Action -eq 'ConfirmDeviceRestart') {
    if (-not $FinalizationReceiptPath -or -not $ReceiptPath -or -not $RestartEvidence) { throw 'ConfirmDeviceRestart requires -FinalizationReceiptPath, -ReceiptPath, and -RestartEvidence.' }
    Assert-TestHostReady $hostState $owner
    $trustInfo = Read-TrustReceipt $TrustReceiptPath $package $owner
    $sourceRead = Read-HashedReceipt $FinalizationReceiptPath $ExpectedReceiptHash 'ACL finalization receipt'
    $sourcePath = $sourceRead.path
    $source = $sourceRead.receipt
    if ([int]$source.schema_version -ne 1 -or [string]$source.status -cne 'BOUND_WAITING_FOR_DEVICE_RESTART' -or [string]$source.manifest_sha256 -cne $package.sha256 -or [string]$source.target.instance_id -cne $TargetInstanceId -or [string]$source.target.owner_sid -cne $owner) { throw 'ACL finalization receipt does not match the exact package, owner, target, and pending-restart state.' }
    if ([string]$source.trust.trust_receipt_path -cne $trustInfo.path -or [string]$source.trust.trust_receipt_sha256 -cne $trustInfo.sha256) { throw 'Trust receipt differs from the one recorded during package staging/finalization.' }
    if ($device.binding_state -cne 'BOUND' -or [string]$device.driver_inf_path.value -ine [string]$source.package.published_inf -or [string]$device.service.value -cne $script:ServiceName) { throw 'Current selected-node binding no longer matches the finalization receipt.' }
    $signedDetails = $device.signed_driver_package.details
    if ($null -eq $signedDetails -or [string]$signedDetails.InfName -ine [string]$source.package.published_inf -or [string]$signedDetails.DriverProviderName -cne 'NTPods' -or [string]$signedDetails.IsSigned -ine 'True') { throw 'Current PnP signed-driver metadata no longer matches the verified NTPods package.' }
    $api = Get-PnpApiType
    $currentSddl = $api::GetSecuritySddl($TargetInstanceId)
    if ($currentSddl -cne [string]$source.after.device_security.sddl -or (Get-SddlSha256 $currentSddl) -cne [string]$source.after.device_security.sha256) { throw 'Current selected-node security property differs from the finalized owner ACL.' }
    $restartEvidenceInfo = [ordered]@{ type = $RestartEvidence; status = $null; recorded_at_utc = [DateTime]::UtcNow.ToString('o'); finalization_host_last_boot_up_time = [string]$source.after.host_state.host.os.last_boot_up_time; current_host_last_boot_up_time = [string]$hostState.host.os.last_boot_up_time }
    if ($RestartEvidence -eq 'ObservedWindowsReboot') {
        $beforeBoot = [string]$source.after.host_state.host.os.last_boot_up_time
        $afterBoot = [string]$hostState.host.os.last_boot_up_time
        if (-not $beforeBoot -or -not $afterBoot -or $beforeBoot -ceq $afterBoot) { throw 'Current read-only OS boot time does not prove a reboot since ACL finalization.' }
        $restartEvidenceInfo.status = 'MEASURED_LAST_BOOT_UP_TIME_CHANGED'
    }
    else {
        $restartEvidenceInfo.status = 'OWNER_CONFIRMED_SELECTED_NODE_RESTART'
        $restartEvidenceInfo.basis = 'Explicit owner attestation; Windows does not expose a reliable per-devnode restart receipt through this bounded script.'
    }
    $receiptTarget = Resolve-ReceiptLocation $ReceiptPath '-ReceiptPath'
    if (Test-Path -LiteralPath $receiptTarget) { throw 'BOUND receipt path already exists; choose a new path.' }
    $receipt = [ordered]@{
        schema_version = 1; status = 'BOUND'; action = 'ConfirmDeviceRestart'; captured_at_utc = [DateTime]::UtcNow.ToString('o')
        manifest_path = $package.path; manifest_sha256 = $package.sha256; package_artifact_sha256 = $package.artifact_sha256; package_hashes = $package.artifact_sha256
        target_instance_id = $TargetInstanceId; owner_sid = $owner; target = $source.target
        staging_receipt = $source.staging_receipt; finalization_receipt = $sourcePath; finalization_receipt_sha256 = $sourceRead.sha256; package = $source.package; trust = $source.trust
        trust_receipt_path = $trustInfo.path; trust_receipt_sha256 = $trustInfo.sha256
        before = $source.before
        after = [ordered]@{ host_state = $hostState; binding = [ordered]@{ state = $device.binding_state; inf = $device.driver_inf_path; service = $device.service; signed_driver_package = $device.signed_driver_package }; device_security = [ordered]@{ sddl = $currentSddl; sha256 = Get-SddlSha256 $currentSddl; verified = $true }; pending_restart = $false; restart_required = $false; owner_access_runtime = 'UNVERIFIED'; hardware_observation = 'NOT_RUN' }
        restart_evidence = $restartEvidenceInfo
        rollback = $source.rollback
    }
    if (-not $PSCmdlet.ShouldProcess($receiptTarget, "Write immutable BOUND receipt after exact AAP node restart evidence")) {
        [ordered]@{ status = 'WHATIF_OR_DECLINED'; action = 'ConfirmDeviceRestart'; target_instance_id = $TargetInstanceId; receipt_path = $receiptTarget; restart_evidence = $restartEvidenceInfo } | ConvertTo-Json -Depth 7 | Write-Output
        return
    }
    [void](Initialize-ReceiptDirectory)
    Write-JsonJournal $receiptTarget $receipt
    $null = Publish-ReceiptHash $receiptTarget
    $receipt | ConvertTo-Json -Depth 14 | Write-Output
    return
}

if ($Action -eq 'Rollback') {
    if (-not $RollbackReceiptPath) { throw '-RollbackReceiptPath is required.' }
    # Authenticate before anything else: administrator-only location, and the SHA-256 of the exact
    # bytes parsed must equal -ExpectedReceiptHash. Changing any field (for example only
    # before.device_security.sddl) fails here, before any ACL or driver-store change is planned.
    $sourceRead = Read-HashedReceipt $RollbackReceiptPath $ExpectedReceiptHash 'Rollback receipt'
    $sourcePath = $sourceRead.path
    $source = $sourceRead.receipt
    $validRollbackStatus = if ([string]$source.action -ceq 'StagePackage') { [string]$source.status -in @('STAGED_WAITING_MANUAL_BIND','STAGE_FAILED_OR_REQUIRES_REVIEW') } else { [string]$source.status -in @('BOUND_WAITING_FOR_DEVICE_RESTART','BOUND') }
    if ([int]$source.schema_version -ne 1 -or -not $validRollbackStatus -or [string]$source.manifest_sha256 -cne $package.sha256 -or [string]$source.target.instance_id -cne $TargetInstanceId -or [string]$source.target.owner_sid -cne $owner) { throw 'Rollback receipt does not match the exact test stage/install, owner, and target.' }
    $publishedInf = [string]$source.package.published_inf
    if ($publishedInf -notmatch '^oem\d+\.inf$') { throw 'Receipt lacks an exact published OEM INF; refusing package removal.' }
    if ($null -eq $source.package.added_by_run) { throw 'Receipt does not record whether this run added the exact package; refusing package deletion.' }
    $removePackage = [bool]$source.package.added_by_run
    if ($removePackage -and $device.driver_inf_path.status -eq 'READ' -and [string]$device.driver_inf_path.value -ieq $publishedInf) { throw 'The test driver is still bound. Restore the captured prior binding manually in Device Manager first; do not remove the device or pairing.' }
    $users = @()
    if ($removePackage) {
        $users = Get-DriverPackageUsers $publishedInf
        if ($users.Count -gt 0) { throw "The exact test package is still referenced by PnP device(s); no ACL or package changes were made: $($users.DeviceID -join ', ')" }
    }
    $currentInf = if ($device.driver_inf_path.status -eq 'READ') { [string]$device.driver_inf_path.value } else { $null }
    $priorInf = if ($source.before.binding.inf.status -eq 'READ') { [string]$source.before.binding.inf.value } else { $null }
    if ($device.driver_inf_path.status -cne [string]$source.before.binding.inf.status -or $currentInf -cne $priorInf -or $device.service.status -cne [string]$source.before.binding.service.status -or [string]$device.service.value -cne [string]$source.before.binding.service.value) { throw 'Current target INF/service status does not match the captured prior binding. Restore it manually before rollback.' }
    if ($removePackage) {
        $exportPath = [IO.Path]::GetTempPath() + 'AppleDeviceHub-AAP-rollback-' + [Guid]::NewGuid().ToString('N')
        [void](Export-DriverPackage $publishedInf $exportPath)
        $null = Assert-ExportMatchesManifest $exportPath $package
    }
    if (-not $ReceiptPath) { throw '-ReceiptPath is required for rollback journal output.' }
    $receiptTarget = Resolve-ReceiptLocation $ReceiptPath '-ReceiptPath'
    if (Test-Path -LiteralPath $receiptTarget) { throw 'Rollback journal path already exists.' }
    $api = Get-PnpApiType
    $priorSecurity = if ($source.before.device_security.sddl) { [string]$source.before.device_security.sddl } else { $null }
    Assert-ValidSddl $priorSecurity
    $receipt = [ordered]@{ schema_version = 1; status = 'ROLLBACK_PENDING'; action = 'Rollback'; source_receipt = $sourcePath; source_receipt_sha256 = $sourceRead.sha256; manifest_sha256 = $package.sha256; target = $source.target; published_inf = $publishedInf; package_added_by_run = $removePackage; package_preserved_preexisting = (-not $removePackage); current_target_inf = $currentInf; package_users = $users; prior_security_sha256 = Get-SddlSha256 $priorSecurity; native_exit_codes = [ordered]@{}; captured_at_utc = [DateTime]::UtcNow.ToString('o') }
    if (-not $PSCmdlet.ShouldProcess($TargetInstanceId, "Restore prior SPDRP_SECURITY_SDS and delete only unused $publishedInf without force; write journal $receiptTarget")) {
        [ordered]@{ status = 'WHATIF_OR_DECLINED'; action = 'Rollback'; target_instance_id = $TargetInstanceId; published_inf = $publishedInf; receipt_path = $receiptTarget } | ConvertTo-Json -Depth 6 | Write-Output
        return
    }
    Assert-Elevated
    [void](Initialize-ReceiptDirectory)
    Write-JsonJournal $receiptTarget $receipt
    try {
        $api::SetSecuritySddl($TargetInstanceId, $priorSecurity)
        $restored = $api::GetSecuritySddl($TargetInstanceId)
        if ($restored -cne $priorSecurity -or (Get-SddlSha256 $restored) -ne (Get-SddlSha256 $priorSecurity)) { throw 'Prior SPDRP_SECURITY_SDS did not verify after rollback.' }
        $receipt.security_restore = 'PASS'
        Write-JsonJournal $receiptTarget $receipt
        if ($removePackage) {
            $pnputil = Join-Path $env:WINDIR 'System32\pnputil.exe'
            $deleteOutput = @(& $pnputil /delete-driver $publishedInf 2>&1 | ForEach-Object { [string]$_ })
            $deleteExitCode = $LASTEXITCODE
            $receipt.native_exit_codes.delete_driver = $deleteExitCode
            $receipt.delete_driver_output = $deleteOutput
            if ($deleteExitCode -ne 0) { throw "pnputil /delete-driver failed with exit $deleteExitCode; no force/uninstall was attempted." }
            $receipt.status = 'ROLLBACK_COMPLETE'
        }
        else {
            $receipt.status = 'ROLLBACK_COMPLETE_PACKAGE_PRESERVED'
            $receipt.package_note = 'Exact matching package was already present before this run and was preserved.'
        }
        $receipt.completed_at_utc = [DateTime]::UtcNow.ToString('o')
        Write-JsonJournal $receiptTarget $receipt
    }
    catch {
        $rollbackFailure = $_.Exception.Message
        $receipt.status = 'ROLLBACK_PARTIAL_REQUIRES_REVIEW'
        $receipt.error = $rollbackFailure
        $failureHash = ''
        try { Write-JsonJournal $receiptTarget $receipt; $failureHash = ' Receipt SHA-256: ' + (Publish-ReceiptHash $receiptTarget) + '.' } catch { }
        throw "Rollback stopped safely; inspect journal $receiptTarget.$failureHash $rollbackFailure"
    }
    $null = Publish-ReceiptHash $receiptTarget
    $receipt | ConvertTo-Json -Depth 12 | Write-Output
    return
}

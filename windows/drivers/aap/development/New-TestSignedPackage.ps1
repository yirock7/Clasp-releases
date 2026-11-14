<#
.SYNOPSIS
Creates an immutable local TestOnly-signed AAP driver package.
.DESCRIPTION
The default invocation validates inputs and prints a plan. Pass -PreparePackage
to make a new output package. Pass -CreateCertificate as well only to create a
dedicated nonexportable code-signing key in the current user's Personal store.
This script does not import trust, change boot policy, install a driver, or claim
a Microsoft signature.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$SourcePackageDirectory = (Join-Path $env:LOCALAPPDATA 'Clasp\build-cache\development-driver\unsigned-current\ClaspAAP'),
    [string]$SourceBuildManifestPath = (Join-Path $env:LOCALAPPDATA 'Clasp\build-cache\development-driver\unsigned-current\build-manifest.json'),
    [string]$OutputDirectory = '',
    [string]$CacheRoot = (Join-Path $env:LOCALAPPDATA 'Clasp\build-cache'),
    [string]$WdkRoot = '',
    [string]$SdkRoot = '',
    [string]$Inf2CatPath = '',
    [string]$SignToolPath = '',
    [string]$CertificateThumbprint = '',
    [string]$CertificateSubject = 'CN=AppleDeviceHub Development Test Driver Signer',
    [switch]$PreparePackage,
    [switch]$CreateCertificate,
    [switch]$PlanOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ObjectProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Assert-NoReparsePointInPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    $current = $root
    $remaining = $fullPath.Substring($root.Length)
    foreach ($part in ($remaining -split '[\\/]')) {
        if (-not $part) { continue }
        $current = Join-Path $current $part
        if (-not (Test-Path -LiteralPath $current)) { break }
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not accepted in package, tool, or output paths: $current"
        }
    }
}

function Assert-ContainedStagingDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$StageDirectory,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$OutputParent
    )
    $stageFull = [IO.Path]::GetFullPath($StageDirectory)
    $parentFull = [IO.Path]::GetFullPath($OutputParent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $stageFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase) -or
        -not $stageFull.StartsWith(([IO.Path]::GetFullPath($OutputDirectory) + '.staging-'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean or publish an unexpected staging path: $stageFull"
    }
    if (Test-Path -LiteralPath $stageFull) {
        Assert-NoReparsePointInPath -Path $stageFull
        $nestedReparsePoints = @(Get-ChildItem -LiteralPath $stageFull -Recurse -Force | Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        })
        if ($nestedReparsePoints.Count -gt 0) {
            throw "Refusing recursive cleanup because the staging tree contains a reparse point: $($nestedReparsePoints[0].FullName)"
        }
    }
}

function Get-PinnedSigningDependencyEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$CacheRoot,
        [Parameter(Mandatory = $true)][string]$WdkVersion,
        [Parameter(Mandatory = $true)][string]$SdkVersion,
        [Parameter(Mandatory = $true)][string]$Inf2CatPath,
        [Parameter(Mandatory = $true)][string]$SignToolPath
    )

    if ($WdkVersion -cne '10.0.28000.2526' -or $SdkVersion -cne '10.0.28000.1721') {
        throw "The source build used unreviewed dependency versions (WDK '$WdkVersion', SDK '$SdkVersion')."
    }
    $pins = @(
        @{ id = 'Microsoft.Windows.WDK.x64'; version = '10.0.28000.2526'; sha256 = '63C939FB5A79295BF40E941DB592681272219B04EDFF095FE2F3D123E5579A90' },
        @{ id = 'Microsoft.Windows.SDK.CPP'; version = '10.0.28000.1721'; sha256 = '722EF3C9780FFDB0966A4AD28871BDE0B37594553652F939C79DD96EA12D2E9E' },
        @{ id = 'Microsoft.Windows.SDK.CPP.x64'; version = '10.0.28000.1721'; sha256 = 'FBDAE03F674D84728B3456AE8A0745D1375DA8204EFD50BDD3BDC257425EA3D1' }
    )
    $nugetPath = Join-Path $CacheRoot 'nuget.exe'
    if (-not (Test-Path -LiteralPath $nugetPath -PathType Leaf)) { throw "Pinned NuGet verifier is missing: $nugetPath" }
    Assert-NoReparsePointInPath -Path $nugetPath
    $nugetHash = Get-Sha256 $nugetPath
    if ($nugetHash -cne '92DBED160DDEE0F64B901E907439E021211B428E57C089ECC12FC38DCC4BD9A5') {
        throw 'The cached NuGet verifier does not match the build-release.ps1 pinned SHA-256.'
    }
    $nugetSignature = Get-AuthenticodeSignature -LiteralPath $nugetPath
    if ([string]$nugetSignature.Status -cne 'Valid' -or $null -eq $nugetSignature.SignerCertificate -or
        $nugetSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "The pinned NuGet verifier failed Microsoft Authenticode validation: $($nugetSignature.Status) / $($nugetSignature.StatusMessage)"
    }

    $nativeCommands = [System.Collections.Generic.List[object]]::new()
    $packageEvidence = [ordered]@{}
    foreach ($pin in $pins) {
        $idVersion = $pin.id + '.' + $pin.version
        $packagePath = Join-Path $CacheRoot ("packages\$idVersion\$idVersion.nupkg")
        if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw "Pinned dependency package is missing: $packagePath" }
        Assert-NoReparsePointInPath -Path $packagePath
        $actualHash = Get-Sha256 $packagePath
        if ($actualHash -cne $pin.sha256) { throw "Pinned dependency package hash mismatch for $idVersion." }
        $nativeCommands.Add((Invoke-CheckedNative -Tool $nugetPath -Arguments @(
            'verify', '-All', $packagePath, '-Verbosity', 'quiet'
        ) -Purpose "NuGet package signature verification: $idVersion"))
        $packageEvidence[$idVersion] = [ordered]@{
            path = $packagePath
            sha256 = $actualHash
            signature_verification = 'PASS'
        }
    }

    $toolEvidence = [ordered]@{}
    foreach ($tool in @(
        @{ name = 'Inf2Cat'; path = $Inf2CatPath },
        @{ name = 'SignTool'; path = $SignToolPath }
    )) {
        Assert-NoReparsePointInPath -Path $tool.path
        $signature = Get-AuthenticodeSignature -LiteralPath $tool.path
        if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            throw "$($tool.name) is not signed by a Microsoft organization certificate."
        }
        if ([string]$signature.Status -in @('NotSigned', 'HashMismatch', 'InvalidSignature')) {
            throw "$($tool.name) Authenticode check failed: $($signature.Status) / $($signature.StatusMessage)"
        }
        $digestEvidence = Test-EmbeddedAuthenticodeDigest -Path $tool.path
        $toolEvidence[$tool.name.ToLowerInvariant()] = [ordered]@{
            path = $tool.path
            sha256 = Get-Sha256 $tool.path
            authenticode_status = [string]$signature.Status
            authenticode_status_message = [string]$signature.StatusMessage
            signer_subject = $signature.SignerCertificate.Subject
            embedded_authenticode_digest = $digestEvidence.status
            source_dependency_package_verification = 'PASS'
        }
    }
    return [pscustomobject]@{
        nuget_path = $nugetPath
        nuget_sha256 = $nugetHash
        nuget_signature = 'PASS'
        package = $packageEvidence
        tools = $toolEvidence
        native_commands = @($nativeCommands)
    }
}

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory = $true)][string]$Tool,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Purpose
    )

    if (-not (Test-Path -LiteralPath $Tool -PathType Leaf)) {
        throw "$Purpose tool does not exist: $Tool"
    }
    $nativeOutput = @()
    try {
        $nativeOutput = @(& $Tool @Arguments 2>&1)
        $nativeExitCode = $LASTEXITCODE
    }
    catch {
        throw "$Purpose could not start '$Tool': $($_.Exception.Message)"
    }
    if ($null -eq $nativeExitCode -or $nativeExitCode -ne 0) {
        $detail = ($nativeOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
        throw ("$Purpose failed with exit code '$nativeExitCode': $Tool" + [Environment]::NewLine + $detail)
    }
    return [pscustomobject]@{
        purpose = $Purpose
        tool = [IO.Path]::GetFullPath($Tool)
        arguments = @($Arguments)
        exit_code = [int]$nativeExitCode
        output = @($nativeOutput | ForEach-Object { [string]$_ })
    }
}

function Initialize-CatalogHashVerifier {
    if ('AppleDeviceHub.TestCatalogHashVerifierV1' -as [type]) { return }
    $source = @'
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AppleDeviceHub {
    public static class TestCatalogHashVerifierV1 {
        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pInfoStruct;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustCatalogInfo {
            public uint cbStruct;
            public uint dwCatalogVersion;
            public IntPtr pcwszCatalogFilePath;
            public IntPtr pcwszMemberTag;
            public IntPtr pcwszMemberFilePath;
            public IntPtr hMemberFile;
            public IntPtr pbCalculatedFileHash;
            public uint cbCalculatedFileHash;
            public IntPtr pcCatalogContext;
            public IntPtr hCatAdmin;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CryptCATAdminAcquireContext2(
            out IntPtr phCatAdmin,
            ref Guid pgSubsystem,
            string pwszHashAlgorithm,
            IntPtr pStrongHashPolicy,
            uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminCalcHashFromFileHandle2(
            IntPtr hCatAdmin,
            IntPtr hFile,
            ref uint pcbHash,
            IntPtr pbHash,
            uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

        [DllImport("wintrust.dll", PreserveSig = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWinTrustData);

        private static string CalculateReferenceTag(IntPtr admin, string memberPath) {
            using (FileStream stream = new FileStream(memberPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                uint size = 0;
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, stream.SafeFileHandle.DangerousGetHandle(), ref size, IntPtr.Zero, 0)) {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                IntPtr hashBuffer = Marshal.AllocHGlobal((int)size);
                try {
                    if (!CryptCATAdminCalcHashFromFileHandle2(admin, stream.SafeFileHandle.DangerousGetHandle(), ref size, hashBuffer, 0)) {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    }
                    byte[] hash = new byte[size];
                    Marshal.Copy(hashBuffer, hash, 0, (int)size);
                    return BitConverter.ToString(hash).Replace("-", "");
                }
                finally {
                    Marshal.FreeHGlobal(hashBuffer);
                }
            }
        }

        public static int VerifyCatalogMember(string catalogPath, string memberPath) {
            Guid driverSubsystem = new Guid("F750E6C3-38EE-11D1-85E5-00C04FC295EE");
            IntPtr admin = IntPtr.Zero;
            if (!CryptCATAdminAcquireContext2(out admin, ref driverSubsystem, "SHA256", IntPtr.Zero, 0)) {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            IntPtr catalogText = IntPtr.Zero;
            IntPtr tagText = IntPtr.Zero;
            IntPtr memberText = IntPtr.Zero;
            IntPtr catalogInfoPointer = IntPtr.Zero;
            IntPtr trustDataPointer = IntPtr.Zero;
            try {
                string fullCatalogPath = Path.GetFullPath(catalogPath);
                string fullMemberPath = Path.GetFullPath(memberPath);
                catalogText = Marshal.StringToHGlobalUni(fullCatalogPath);
                tagText = Marshal.StringToHGlobalUni(CalculateReferenceTag(admin, fullMemberPath));
                memberText = Marshal.StringToHGlobalUni(fullMemberPath);

                WinTrustCatalogInfo catalogInfo = new WinTrustCatalogInfo();
                catalogInfo.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustCatalogInfo));
                catalogInfo.pcwszCatalogFilePath = catalogText;
                catalogInfo.pcwszMemberTag = tagText;
                catalogInfo.pcwszMemberFilePath = memberText;
                catalogInfo.hCatAdmin = admin;
                catalogInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustCatalogInfo)));
                Marshal.StructureToPtr(catalogInfo, catalogInfoPointer, false);

                WinTrustData trustData = new WinTrustData();
                trustData.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData));
                trustData.dwUIChoice = 2;
                trustData.dwUnionChoice = 2;
                trustData.pInfoStruct = catalogInfoPointer;
                trustData.dwProvFlags = 0x00000200; // WTD_HASH_ONLY_FLAG: test the catalog member hash without chain trust.
                trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustData)));
                Marshal.StructureToPtr(trustData, trustDataPointer, false);

                Guid genericVerifyAction = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                return WinVerifyTrust(IntPtr.Zero, ref genericVerifyAction, trustDataPointer);
            }
            finally {
                if (trustDataPointer != IntPtr.Zero) Marshal.FreeHGlobal(trustDataPointer);
                if (catalogInfoPointer != IntPtr.Zero) Marshal.FreeHGlobal(catalogInfoPointer);
                if (catalogText != IntPtr.Zero) Marshal.FreeHGlobal(catalogText);
                if (tagText != IntPtr.Zero) Marshal.FreeHGlobal(tagText);
                if (memberText != IntPtr.Zero) Marshal.FreeHGlobal(memberText);
                if (admin != IntPtr.Zero) CryptCATAdminReleaseContext(admin, 0);
            }
        }

        public static int VerifyEmbeddedFileDigest(string filePath) {
            return VerifyEmbeddedFile(filePath, 0x00000200);
        }

        public static int VerifyEmbeddedFileTrustStatus(string filePath) {
            return VerifyEmbeddedFile(filePath, 0);
        }

        private static int VerifyEmbeddedFile(string filePath, uint providerFlags) {
            IntPtr fileText = IntPtr.Zero;
            IntPtr fileInfoPointer = IntPtr.Zero;
            IntPtr trustDataPointer = IntPtr.Zero;
            try {
                fileText = Marshal.StringToHGlobalUni(Path.GetFullPath(filePath));
                WinTrustFileInfo fileInfo = new WinTrustFileInfo();
                fileInfo.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
                fileInfo.pcwszFilePath = fileText;
                fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

                WinTrustData trustData = new WinTrustData();
                trustData.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData));
                trustData.dwUIChoice = 2;
                trustData.dwUnionChoice = 1;
                trustData.pInfoStruct = fileInfoPointer;
                trustData.dwProvFlags = providerFlags;
                trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustData)));
                Marshal.StructureToPtr(trustData, trustDataPointer, false);

                Guid genericVerifyAction = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                return WinVerifyTrust(IntPtr.Zero, ref genericVerifyAction, trustDataPointer);
            }
            finally {
                if (trustDataPointer != IntPtr.Zero) Marshal.FreeHGlobal(trustDataPointer);
                if (fileInfoPointer != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPointer);
                if (fileText != IntPtr.Zero) Marshal.FreeHGlobal(fileText);
            }
        }
    }
}
'@
    Add-Type -TypeDefinition $source -ErrorAction Stop
}

function Test-CatalogMember {
    param(
        [Parameter(Mandatory = $true)][string]$CatalogPath,
        [Parameter(Mandatory = $true)][string]$MemberPath
    )
    Initialize-CatalogHashVerifier
    $status = [AppleDeviceHub.TestCatalogHashVerifierV1]::VerifyCatalogMember($CatalogPath, $MemberPath)
    if ($status -ne 0) {
        $hexStatus = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$status), 0).ToString('X8')
        throw "Catalog member hash check failed for '$MemberPath' against '$CatalogPath' (WinVerifyTrust 0x$hexStatus)."
    }
    return [pscustomobject]@{
        status = 'PASS'
        method = 'WinVerifyTrust WTD_HASH_ONLY_FLAG with explicit catalog and SHA256 member tag'
        member_path = [IO.Path]::GetFullPath($MemberPath)
        catalog_path = [IO.Path]::GetFullPath($CatalogPath)
    }
}

function Test-EmbeddedAuthenticodeDigest {
    param([Parameter(Mandatory = $true)][string]$Path)
    Initialize-CatalogHashVerifier
    $status = [AppleDeviceHub.TestCatalogHashVerifierV1]::VerifyEmbeddedFileDigest($Path)
    if ($status -ne 0) {
        $hexStatus = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$status), 0).ToString('X8')
        throw "Embedded Authenticode file-digest check failed for '$Path' (WinVerifyTrust WTD_HASH_ONLY_FLAG 0x$hexStatus)."
    }
    return [pscustomobject]@{
        status = 'PASS'
        method = 'WinVerifyTrust WTD_HASH_ONLY_FLAG with embedded Authenticode SIP digest'
        file_path = [IO.Path]::GetFullPath($Path)
    }
}

function Test-EmbeddedPeAuthenticodeCmsSignature {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedThumbprint
    )
    Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5A4D) {
        throw "Embedded Authenticode CMS check failed: '$Path' is not a valid PE image."
    }
    $ntHeaderOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($ntHeaderOffset -lt 0 -or $ntHeaderOffset + 24 -gt $bytes.Length -or
        [BitConverter]::ToUInt32($bytes, $ntHeaderOffset) -ne 0x00004550) {
        throw "Embedded Authenticode CMS check failed: '$Path' has an invalid PE header offset/signature."
    }
    $optionalHeaderOffset = $ntHeaderOffset + 24
    $optionalHeaderSize = [BitConverter]::ToUInt16($bytes, $ntHeaderOffset + 20)
    if ($optionalHeaderOffset + $optionalHeaderSize -gt $bytes.Length) {
        throw "Embedded Authenticode CMS check failed: '$Path' has a truncated optional header."
    }
    $optionalMagic = [BitConverter]::ToUInt16($bytes, $optionalHeaderOffset)
    if ($optionalMagic -eq 0x10B) { $dataDirectoryOffset = 96 }
    elseif ($optionalMagic -eq 0x20B) { $dataDirectoryOffset = 112 }
    else { throw "Embedded Authenticode CMS check failed: unsupported PE optional-header magic 0x$($optionalMagic.ToString('X4'))." }
    $securityDirectoryOffset = $optionalHeaderOffset + $dataDirectoryOffset + (4 * 8)
    if ($securityDirectoryOffset + 8 -gt ($optionalHeaderOffset + $optionalHeaderSize)) {
        throw "Embedded Authenticode CMS check failed: '$Path' has no security-directory entry."
    }
    $certificateTableOffset = [int64][BitConverter]::ToUInt32($bytes, $securityDirectoryOffset)
    $certificateTableSize = [int64][BitConverter]::ToUInt32($bytes, $securityDirectoryOffset + 4)
    $certificateTableEnd = $certificateTableOffset + $certificateTableSize
    if ($certificateTableOffset -le 0 -or $certificateTableSize -le 0 -or $certificateTableEnd -gt $bytes.LongLength) {
        throw "Embedded Authenticode CMS check failed: '$Path' has an invalid or empty certificate table."
    }

    $cmsCount = 0
    $expectedThumbprint = ($ExpectedThumbprint -replace '\s', '').ToUpperInvariant()
    $position = $certificateTableOffset
    while ($position -lt $certificateTableEnd) {
        if ($position + 8 -gt $certificateTableEnd) {
            throw "Embedded Authenticode CMS check failed: '$Path' has a truncated WIN_CERTIFICATE header."
        }
        $entryLength = [int64][BitConverter]::ToUInt32($bytes, [int]$position)
        $revision = [BitConverter]::ToUInt16($bytes, [int]($position + 4))
        $certificateType = [BitConverter]::ToUInt16($bytes, [int]($position + 6))
        if ($entryLength -lt 8 -or $position + $entryLength -gt $certificateTableEnd) {
            throw "Embedded Authenticode CMS check failed: '$Path' has an invalid WIN_CERTIFICATE length."
        }
        if ($certificateType -eq 2 -and $revision -eq 0x0200) {
            $cmsBytes = New-Object byte[] ([int]($entryLength - 8))
            [Array]::Copy($bytes, [int]($position + 8), $cmsBytes, 0, $cmsBytes.Length)
            $cms = [System.Security.Cryptography.Pkcs.SignedCms]::new()
            $cms.Decode($cmsBytes)
            if ($cms.SignerInfos.Count -ne 1) {
                throw "Expected one signer in the embedded PE Authenticode CMS, found $($cms.SignerInfos.Count)."
            }
            $cms.CheckSignature($true)
            $signerCertificate = $cms.SignerInfos[0].Certificate
            if ($null -eq $signerCertificate) { throw 'Embedded PE Authenticode CMS did not embed its signer certificate.' }
            $actualThumbprint = ($signerCertificate.Thumbprint -replace '\s', '').ToUpperInvariant()
            if ($actualThumbprint -cne $expectedThumbprint) {
                throw "Embedded PE Authenticode CMS signer mismatch: expected $expectedThumbprint, received $actualThumbprint."
            }
            $cmsCount++
        }
        $position += [int64]([Math]::Ceiling($entryLength / 8.0) * 8)
    }
    if ($cmsCount -ne 1) { throw "Expected one embedded PE Authenticode CMS signature, found $cmsCount." }
    return [pscustomobject]@{
        status = 'PASS'
        method = 'Extract WIN_CERTIFICATE PKCS#7 CMS; SignedCms.CheckSignature(true); exact signer thumbprint'
        signer_thumbprint = $expectedThumbprint
    }
}

function Get-EmbeddedAuthenticodeTrustStatus {
    param([Parameter(Mandatory = $true)][string]$Path)
    Initialize-CatalogHashVerifier
    $status = [AppleDeviceHub.TestCatalogHashVerifierV1]::VerifyEmbeddedFileTrustStatus($Path)
    $hexStatus = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$status), 0).ToString('X8')
    return [pscustomobject]@{
        status_code = $hexStatus
        status_code_decimal = [int64]$status
        file_path = [IO.Path]::GetFullPath($Path)
    }
}

function Get-ExactSignerCertificate {
    param([Parameter(Mandatory = $true)][string]$Thumbprint)
    $normalized = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($normalized -notmatch '^[0-9A-F]{40}$') {
        throw 'CertificateThumbprint must be the exact 40-character SHA-1 thumbprint of a certificate in Cert:\CurrentUser\My.'
    }
    $matches = @(Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My' | Where-Object {
        ($_.Thumbprint -replace '\s', '').ToUpperInvariant() -ceq $normalized
    })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one certificate with thumbprint $normalized in Cert:\CurrentUser\My; found $($matches.Count)."
    }
    $certificate = $matches[0]
    if (-not $certificate.HasPrivateKey) { throw 'The exact certificate does not have an associated private signing key.' }
    if ($certificate.Subject -cne $CertificateSubject -or $certificate.Issuer -cne $certificate.Subject) {
        throw "The signer must be the dedicated self-signed certificate with subject '$CertificateSubject'."
    }
    $now = [DateTime]::UtcNow
    if ($certificate.NotBefore.ToUniversalTime() -gt $now -or $certificate.NotAfter.ToUniversalTime() -le $now) {
        throw 'The signing certificate is outside its validity window.'
    }
    $codeSigningEku = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
    if ($codeSigningEku.Count -eq 0 -or $codeSigningEku -notcontains '1.3.6.1.5.5.7.3.3') {
        throw 'The exact certificate does not carry the Code Signing enhanced key usage.'
    }
    if ($certificate.PublicKey.Oid.Value -ne '1.2.840.113549.1.1.1' -or $certificate.PublicKey.Key.KeySize -lt 2048) {
        throw 'The signer must use an RSA key of at least 2048 bits.'
    }
    $privateKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    if ($null -eq $privateKey) { throw 'The exact certificate private key is not an RSA key.' }
    try {
        if ($privateKey -is [System.Security.Cryptography.RSACng]) {
            if ($privateKey.Key.ExportPolicy -ne [System.Security.Cryptography.CngExportPolicies]::None) {
                throw 'The private signing key is exportable; use a nonexportable dedicated key.'
            }
        }
        elseif ($privateKey -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            if ($privateKey.CspKeyContainerInfo.Exportable) {
                throw 'The private signing key is exportable; use a nonexportable dedicated key.'
            }
        }
        else {
            throw 'The private signing key provider does not expose a verifiable nonexportable policy.'
        }
    }
    finally {
        $privateKey.Dispose()
    }
    return $certificate
}

function Test-AuthenticodeSigner {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedThumbprint,
        [Parameter(Mandatory = $true)][string]$Purpose
    )
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($null -eq $signature.SignerCertificate) {
        throw "$Purpose signature did not contain a signer certificate."
    }
    $actualThumbprint = ($signature.SignerCertificate.Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($actualThumbprint -cne $ExpectedThumbprint) {
        throw "$Purpose signer mismatch: expected $ExpectedThumbprint, received $actualThumbprint."
    }
    $statusName = [string]$signature.Status
    $nativeTrust = Get-EmbeddedAuthenticodeTrustStatus -Path $Path
    $exactUntrustedRoot = ($nativeTrust.status_code -ceq '800B0109' -and
        $signature.StatusMessage -match 'terminated in a root certificate which is not trusted by the trust provider')
    if ($statusName -ceq 'Valid') {
        if ($nativeTrust.status_code -cne '00000000') {
            throw "$Purpose reports Valid but native WinVerifyTrust returned 0x$($nativeTrust.status_code)."
        }
    }
    elseif ($statusName -in @('NotTrusted', 'UnknownError') -and $exactUntrustedRoot) {
        # Test signers intentionally remain outside Root/Publisher trust stores.
    }
    else {
        throw "$Purpose signature check failed with status '$statusName' / WinVerifyTrust 0x$($nativeTrust.status_code): $($signature.StatusMessage)"
    }
    $embeddedDigest = $null
    $embeddedCms = $null
    if ($Purpose -ceq 'Embedded SYS') {
        $embeddedDigest = Test-EmbeddedAuthenticodeDigest -Path $Path
        $embeddedCms = Test-EmbeddedPeAuthenticodeCmsSignature -Path $Path -ExpectedThumbprint $ExpectedThumbprint
    }
    return [pscustomobject]@{
        status = 'PASS'
        authenticode_status = $statusName
        signer_thumbprint = $actualThumbprint
        status_message = [string]$signature.StatusMessage
        native_trust_status = $nativeTrust.status_code
        chain_trust_classification = if ($exactUntrustedRoot) { 'EXPECTED_TEST_SIGNER_UNTRUSTED_ROOT' } else { 'TRUSTED' }
        embedded_authenticode_digest = if ($embeddedDigest) { $embeddedDigest.status } else { $null }
        embedded_cms_signature = if ($embeddedCms) { $embeddedCms.status } else { $null }
    }
}

function Test-CatalogCmsSignature {
    param(
        [Parameter(Mandatory = $true)][string]$CatalogPath,
        [Parameter(Mandatory = $true)][string]$ExpectedThumbprint
    )
    Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
    $cms = [System.Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode([IO.File]::ReadAllBytes($CatalogPath))
    if ($cms.SignerInfos.Count -ne 1) {
        throw "Expected one catalog CMS signer, found $($cms.SignerInfos.Count)."
    }
    $cms.CheckSignature($true)
    $cmsSigner = $cms.SignerInfos[0].Certificate
    if ($null -eq $cmsSigner) { throw 'The catalog CMS signature did not embed its signer certificate.' }
    $actual = ($cmsSigner.Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($actual -cne $ExpectedThumbprint) {
        throw "Catalog CMS signer mismatch: expected $ExpectedThumbprint, received $actual."
    }
    return [pscustomobject]@{
        status = 'PASS'
        method = 'SignedCms.CheckSignature(true), with no certificate-chain trust check'
        signer_thumbprint = $actual
    }
}

function Assert-SourcePackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackageDirectory,
        [Parameter(Mandatory = $true)][string]$BuildManifestPath
    )
    if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
        throw "Source package directory does not exist: $PackageDirectory"
    }
    if (-not (Test-Path -LiteralPath $BuildManifestPath -PathType Leaf)) {
        throw "Sibling build manifest does not exist: $BuildManifestPath"
    }
    $manifest = Get-Content -LiteralPath $BuildManifestPath -Raw | ConvertFrom-Json
    if ((Get-ObjectProperty $manifest 'status') -cne 'UNSIGNED_SUBMISSION_CANDIDATE') {
        throw 'The source build manifest is not an unsigned submission candidate.'
    }
    $validation = Get-ObjectProperty $manifest 'validation'
    if ((Get-ObjectProperty $validation 'microsoft_signature') -cne 'NOT_PRESENT') {
        throw 'The source manifest must explicitly report microsoft_signature=NOT_PRESENT.'
    }
    $artifactObject = Get-ObjectProperty $manifest 'artifact_sha256'
    if ($null -eq $artifactObject) { throw 'The source build manifest has no artifact_sha256 map.' }
    $expectedHashes = @{}
    foreach ($property in $artifactObject.PSObject.Properties) {
        $expectedHashes[$property.Name] = ([string]$property.Value).ToUpperInvariant()
    }
    $directories = @(Get-ChildItem -LiteralPath $PackageDirectory -Directory -Force)
    if ($directories.Count -gt 0) { throw 'The source package must be a flat directory of files.' }
    $files = @(Get-ChildItem -LiteralPath $PackageDirectory -File -Force | Sort-Object Name)
    if ($files.Count -eq 0) { throw 'The source package is empty.' }
    if ($expectedHashes.Count -ne $files.Count) {
        throw "Build-manifest file count ($($expectedHashes.Count)) does not match source package file count ($($files.Count))."
    }
    foreach ($file in $files) {
        if ($file.Extension.ToLowerInvariant() -notin @('.inf', '.sys', '.cat', '.pdb')) {
            throw "Unexpected source package file type: $($file.Name)"
        }
        if (-not $expectedHashes.ContainsKey($file.Name)) {
            throw "Source build manifest does not include package file '$($file.Name)'."
        }
        $actualHash = Get-Sha256 $file.FullName
        if ($actualHash -cne $expectedHashes[$file.Name]) {
            throw "Source package hash mismatch for '$($file.Name)': expected $($expectedHashes[$file.Name]), received $actualHash."
        }
    }
    foreach ($name in @('NTPodsAAP.inf', 'NTPodsAAP.sys')) {
        if ($files.Name -notcontains $name) { throw "Required package file is missing: $name" }
    }
    $catalogs = @($files | Where-Object { $_.Extension -ieq '.cat' -and $_.BaseName -ieq 'NTPodsAAP' })
    if ($catalogs.Count -ne 1) { throw "Expected one NTPodsAAP catalog in the source package, found $($catalogs.Count)." }
    $buildManifestHash = Get-Sha256 $BuildManifestPath
    $allHashes = [ordered]@{}
    $driverHashes = [ordered]@{}
    foreach ($file in $files) {
        $hash = Get-Sha256 $file.FullName
        $allHashes[$file.Name] = $hash
        if ($file.Extension.ToLowerInvariant() -in @('.inf', '.sys', '.cat')) {
            $driverHashes[$file.Name] = $hash
        }
    }
    return [pscustomobject]@{
        build_manifest = $manifest
        build_manifest_sha256 = $buildManifestHash
        files = $files
        catalog_name = $catalogs[0].Name
        package_file_sha256 = $allHashes
        driver_file_sha256 = $driverHashes
    }
}

if ($CreateCertificate -and -not $PreparePackage) {
    throw 'Certificate creation requires the explicit -PreparePackage switch.'
}
if ($PlanOnly -and $PreparePackage) {
    throw 'Choose either -PlanOnly or -PreparePackage, not both.'
}
if ($CreateCertificate -and $CertificateThumbprint) {
    throw 'Use either -CreateCertificate or -CertificateThumbprint, not both.'
}
if ($PreparePackage -and -not $CreateCertificate -and -not $CertificateThumbprint) {
    throw 'Package preparation requires an exact -CertificateThumbprint or the explicit -CreateCertificate switch.'
}

$SourcePackageDirectory = [IO.Path]::GetFullPath($SourcePackageDirectory)
$SourceBuildManifestPath = [IO.Path]::GetFullPath($SourceBuildManifestPath)
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
Assert-NoReparsePointInPath -Path $SourcePackageDirectory
Assert-NoReparsePointInPath -Path $SourceBuildManifestPath
$source = Assert-SourcePackage -PackageDirectory $SourcePackageDirectory -BuildManifestPath $SourceBuildManifestPath

$expectedOutputParent = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $SourcePackageDirectory)))
if (-not $OutputDirectory) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $OutputDirectory = Join-Path $expectedOutputParent ("test-signed-$stamp-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$outputParent = [IO.Path]::GetFullPath((Split-Path -Parent $OutputDirectory))
if (-not $outputParent.Equals($expectedOutputParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be a new direct child of the reviewed development build root '$expectedOutputParent'."
}
if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
    throw "OutputDirectory parent does not exist: $outputParent"
}
Assert-NoReparsePointInPath -Path $outputParent
Assert-NoReparsePointInPath -Path $OutputDirectory
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "OutputDirectory already exists. Outputs are never overwritten: $OutputDirectory"
}
$sourcePrefix = $SourcePackageDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$outputPrefix = $OutputDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($OutputDirectory.Equals($SourcePackageDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $SourcePackageDirectory.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be separate from the immutable source package directory.'
}

$buildManifest = $source.build_manifest
$wdkVersion = [string](Get-ObjectProperty $buildManifest 'wdk_version')
$sdkVersion = [string](Get-ObjectProperty $buildManifest 'sdk_version')
if (-not $wdkVersion -or -not $sdkVersion) { throw 'The source manifest does not identify the WDK and SDK versions.' }
if (-not $WdkRoot) {
    $WdkRoot = Join-Path $CacheRoot ("packages\Microsoft.Windows.WDK.x64.$wdkVersion\c")
}
if (-not $SdkRoot) {
    $SdkRoot = Join-Path $CacheRoot ("packages\Microsoft.Windows.SDK.CPP.$sdkVersion\c")
}
$WdkRoot = [IO.Path]::GetFullPath($WdkRoot)
$SdkRoot = [IO.Path]::GetFullPath($SdkRoot)
$expectedWdkRoot = [IO.Path]::GetFullPath((Join-Path $CacheRoot ("packages\Microsoft.Windows.WDK.x64.$wdkVersion\c")))
$expectedSdkRoot = [IO.Path]::GetFullPath((Join-Path $CacheRoot ("packages\Microsoft.Windows.SDK.CPP.$sdkVersion\c")))
if (-not $WdkRoot.Equals($expectedWdkRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $SdkRoot.Equals($expectedSdkRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'WDK and SDK roots must resolve to the exact versioned packages under CacheRoot.'
}
$expectedInf2CatPath = Join-Path $WdkRoot 'bin\10.0.28000.0\x86\Inf2Cat.exe'
$expectedSignToolPath = Join-Path $SdkRoot 'bin\10.0.28000.0\x64\signtool.exe'
if (-not $Inf2CatPath) { $Inf2CatPath = $expectedInf2CatPath }
if (-not $SignToolPath) { $SignToolPath = $expectedSignToolPath }
$Inf2CatPath = [IO.Path]::GetFullPath($Inf2CatPath)
$SignToolPath = [IO.Path]::GetFullPath($SignToolPath)
if (-not $Inf2CatPath.Equals([IO.Path]::GetFullPath($expectedInf2CatPath), [StringComparison]::OrdinalIgnoreCase) -or
    -not $SignToolPath.Equals([IO.Path]::GetFullPath($expectedSignToolPath), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Inf2Cat and SignTool must be the exact executables from the pinned WDK and SDK packages.'
}
foreach ($tool in @(@{ path = $Inf2CatPath; name = 'Inf2Cat' }, @{ path = $SignToolPath; name = 'SignTool' })) {
    if (-not (Test-Path -LiteralPath $tool.path -PathType Leaf)) {
        throw "$($tool.name) was not found at '$($tool.path)'. Check the pinned SDK/WDK cache or pass its exact path."
    }
}

if (-not $PreparePackage) {
    $mode = if ($PlanOnly) { 'PLAN_ONLY' } else { 'DEFAULT_PLAN_ONLY' }
    [pscustomobject]@{
        mode = $mode
        source_package_directory = $SourcePackageDirectory
        source_build_manifest_path = $SourceBuildManifestPath
        source_build_manifest_sha256 = $source.build_manifest_sha256
        source_package_file_sha256 = $source.package_file_sha256
        output_directory = $OutputDirectory
        package_directory = Join-Path $OutputDirectory 'ClaspAAP'
        inf2cat_path = $Inf2CatPath
        signtool_path = $SignToolPath
        requested_signer = if ($CertificateThumbprint) { $CertificateThumbprint } elseif ($CreateCertificate) { $CertificateSubject } else { 'Select an exact certificate thumbprint or request explicit certificate creation.' }
        trust = 'TestOnly'
        writes_performed = $false
    }
    return
}

if (-not $PSCmdlet.ShouldProcess($OutputDirectory, 'Create a new immutable TestOnly-signed AAP package')) {
    return
}
$dependencyEvidence = Get-PinnedSigningDependencyEvidence -CacheRoot $CacheRoot -WdkVersion $wdkVersion -SdkVersion $sdkVersion -Inf2CatPath $Inf2CatPath -SignToolPath $SignToolPath
$sourceRecheck = Assert-SourcePackage -PackageDirectory $SourcePackageDirectory -BuildManifestPath $SourceBuildManifestPath
if ($sourceRecheck.build_manifest_sha256 -cne $source.build_manifest_sha256) {
    throw 'The source build manifest changed during preparation preflight; no certificate was created.'
}
foreach ($name in $source.package_file_sha256.Keys) {
    if ($sourceRecheck.package_file_sha256[$name] -cne $source.package_file_sha256[$name]) {
        throw "Source package file '$name' changed during preparation preflight; no certificate was created."
    }
}
if ($CreateCertificate -and -not $PSCmdlet.ShouldProcess('Cert:\CurrentUser\My', "Create nonexportable code-signing certificate '$CertificateSubject'")) {
    return
}

$stageParent = $outputParent
$stageDirectory = $OutputDirectory + '.staging-' + [Guid]::NewGuid().ToString('N')
$stageCreated = $false
$published = $false
$nativeResults = [System.Collections.Generic.List[object]]::new()
$nativeResults.AddRange([object[]]$dependencyEvidence.native_commands)
$certificate = $null
$certificateCreated = $false
$createdCertificateThumbprint = ''
try {
    New-Item -ItemType Directory -Path $stageDirectory -ErrorAction Stop | Out-Null
    $stageCreated = $true
    $packageDirectory = Join-Path $stageDirectory 'ClaspAAP'
    New-Item -ItemType Directory -Path $packageDirectory -ErrorAction Stop | Out-Null
    foreach ($file in $source.files | Where-Object { $_.Extension -ine '.cat' }) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $packageDirectory $file.Name)
    }

    $sysPath = Join-Path $packageDirectory 'NTPodsAAP.sys'
    $infPath = Join-Path $packageDirectory 'NTPodsAAP.inf'

    if ($CreateCertificate) {
        $certificateParameters = @{
            Type = 'CodeSigningCert'
            Subject = $CertificateSubject
            KeyAlgorithm = 'RSA'
            KeyLength = 3072
            HashAlgorithm = 'SHA256'
            KeyExportPolicy = 'NonExportable'
            Provider = 'Microsoft Software Key Storage Provider'
            KeyUsage = 'DigitalSignature'
            CertStoreLocation = 'Cert:\CurrentUser\My'
            NotAfter = [DateTime]::Now.AddYears(2)
            ErrorAction = 'Stop'
        }
        $certificate = New-SelfSignedCertificate @certificateParameters
        if ($null -eq $certificate) { throw 'New-SelfSignedCertificate did not return a certificate.' }
        $CertificateThumbprint = $certificate.Thumbprint
        $certificateCreated = $true
        $createdCertificateThumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
    }
    $certificate = Get-ExactSignerCertificate -Thumbprint $CertificateThumbprint
    $signerThumbprint = ($certificate.Thumbprint -replace '\s', '').ToUpperInvariant()
    $cerPath = Join-Path $stageDirectory 'signer.cer'
    Export-Certificate -Cert $certificate -FilePath $cerPath -Type CERT -NoClobber -ErrorAction Stop | Out-Null
    $publicCertificateHash = Get-Sha256 $cerPath
    $certificateReceipt = [ordered]@{
        schema_version = 1
        status = if ($certificateCreated) { 'TEST_ONLY_CERTIFICATE_CREATED' } else { 'TEST_ONLY_CERTIFICATE_USED' }
        thumbprint = $signerThumbprint
        subject = $certificate.Subject
        issuer = $certificate.Issuer
        serial_number = $certificate.SerialNumber
        store_location = 'Cert:\CurrentUser\My'
        public_certificate_file = 'signer.cer'
        public_certificate_sha256 = $publicCertificateHash
        not_before_utc = $certificate.NotBefore.ToUniversalTime().ToString('o')
        not_after_utc = $certificate.NotAfter.ToUniversalTime().ToString('o')
        key_algorithm = 'RSA'
        key_length = $certificate.PublicKey.Key.KeySize
        key_export_policy = 'NonExportable'
    }
    $certificateReceipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stageDirectory 'certificate-receipt.json') -Encoding UTF8

    $nativeResults.Add((Invoke-CheckedNative -Tool $SignToolPath -Arguments @(
        'sign', '/fd', 'SHA256', '/s', 'My', '/sha1', $signerThumbprint, $sysPath
    ) -Purpose 'Embedded SYS test signing'))

    # The catalog must describe the already-signed SYS, so regenerate it after SYS signing.
    $nativeResults.Add((Invoke-CheckedNative -Tool $Inf2CatPath -Arguments @(
        ('/driver:' + $packageDirectory), '/os:10_GE_X64', '/uselocaltime', '/verbose'
    ) -Purpose 'Catalog regeneration'))
    $catalogCandidates = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object {
        $_.Extension -ieq '.cat' -and $_.BaseName -ieq 'NTPodsAAP'
    })
    if ($catalogCandidates.Count -ne 1) {
        throw "Inf2Cat must create exactly one NTPodsAAP catalog, found $($catalogCandidates.Count)."
    }
    $catalogPath = $catalogCandidates[0].FullName
    $nativeResults.Add((Invoke-CheckedNative -Tool $SignToolPath -Arguments @(
        'sign', '/fd', 'SHA256', '/s', 'My', '/sha1', $signerThumbprint, $catalogPath
    ) -Purpose 'Catalog test signing'))

    $sysSignature = Test-AuthenticodeSigner -Path $sysPath -ExpectedThumbprint $signerThumbprint -Purpose 'Embedded SYS'
    $catSignature = Test-AuthenticodeSigner -Path $catalogPath -ExpectedThumbprint $signerThumbprint -Purpose 'Catalog'
    $catCmsSignature = Test-CatalogCmsSignature -CatalogPath $catalogPath -ExpectedThumbprint $signerThumbprint
    $infMembership = Test-CatalogMember -CatalogPath $catalogPath -MemberPath $infPath
    $sysMembership = Test-CatalogMember -CatalogPath $catalogPath -MemberPath $sysPath

    if ((Get-Sha256 $SourceBuildManifestPath) -cne $source.build_manifest_sha256) {
        throw 'The sibling build manifest changed while signing; discard this incomplete stage and retry.'
    }
    foreach ($file in $source.files) {
        if ((Get-Sha256 $file.FullName) -cne $source.package_file_sha256[$file.Name]) {
            throw "Source package file '$($file.Name)' changed while signing; discard this incomplete stage and retry."
        }
    }

    $artifactHashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -File | Sort-Object Name) {
        $artifactHashes[$file.Name] = Get-Sha256 $file.FullName
    }
    $toolInformation = @{
        inf2cat = @{
            path = $Inf2CatPath
            file_sha256 = Get-Sha256 $Inf2CatPath
            file_version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Inf2CatPath).FileVersion
            exit_code = 0
        }
        signtool = @{
            path = $SignToolPath
            file_sha256 = Get-Sha256 $SignToolPath
            file_version = [Diagnostics.FileVersionInfo]::GetVersionInfo($SignToolPath).FileVersion
            exit_code = 0
        }
    }
    $nativeSummary = @($nativeResults | ForEach-Object {
        [ordered]@{
            purpose = $_.purpose
            tool = $_.tool
            arguments = $_.arguments
            exit_code = $_.exit_code
        }
    })
    $finalPackageDirectory = Join-Path $OutputDirectory 'ClaspAAP'
    $validation = [ordered]@{
        catalog_generation = 'PASS'
        cat_generation = 'PASS'
        embedded_sys_signature = 'PASS'
        sys_signature = 'PASS'
        embedded_sys_authenticode_digest = $sysSignature.embedded_authenticode_digest
        embedded_sys_cms_signature = $sysSignature.embedded_cms_signature
        catalog_cms_signature = 'PASS'
        cat_signature = 'PASS'
        catalog_membership = 'PASS'
        catalog_member_files = [ordered]@{
            'NTPodsAAP.inf' = 'PASS'
            'NTPodsAAP.sys' = 'PASS'
        }
        exact_signer_thumbprint = 'PASS'
        signer_key_nonexportable = 'PASS'
        signer_chain_trust = 'NOT_IMPORTED'
        microsoft_signature = 'NOT_PRESENT'
        hardware = 'NOT_RUN'
    }
    $signedManifest = [ordered]@{
        schema_version = 1
        status = 'TEST_ONLY_SIGNED_PACKAGE'
        trust = 'TestOnly'
        microsoft_signature = 'NOT_PRESENT'
        created_at_utc = [DateTime]::UtcNow.ToString('o')
        package_directory = 'ClaspAAP'
        signed_package_directory = $finalPackageDirectory
        signer_thumbprint = $signerThumbprint
        artifact_sha256 = $artifactHashes
        source_build_manifest_path = $SourceBuildManifestPath
        source_build_manifest_sha256 = $source.build_manifest_sha256
        source_package_directory = $SourcePackageDirectory
        source_package_file_sha256 = $source.package_file_sha256
        source_driver_file_sha256 = $source.driver_file_sha256
        source_file_sha256 = Get-ObjectProperty $buildManifest 'source_file_sha256'
        certificate = [ordered]@{
            thumbprint = $signerThumbprint
            subject = $certificate.Subject
            issuer = $certificate.Issuer
            serial_number = $certificate.SerialNumber
            not_before_utc = $certificate.NotBefore.ToUniversalTime().ToString('o')
            not_after_utc = $certificate.NotAfter.ToUniversalTime().ToString('o')
            store_location = 'Cert:\CurrentUser\My'
            public_certificate_file = 'signer.cer'
            public_certificate_path = Join-Path $OutputDirectory 'signer.cer'
            public_certificate_sha256 = $publicCertificateHash
            receipt_file = 'certificate-receipt.json'
            key_algorithm = 'RSA'
            key_length = $certificate.PublicKey.Key.KeySize
            key_export_policy = 'NonExportable'
        }
        target = [ordered]@{
            hardware_id = 'BTHENUM\{74ec2172-0bad-4d01-8f77-997b2be0722a}'
            service = 'NTPodsAAP'
        }
        tool = $toolInformation
        native_commands = $nativeSummary
        signing_dependencies = [ordered]@{
            nuget_path = $dependencyEvidence.nuget_path
            nuget_sha256 = $dependencyEvidence.nuget_sha256
            nuget_signature = $dependencyEvidence.nuget_signature
            package = $dependencyEvidence.package
            tools = $dependencyEvidence.tools
        }
        signature_observation = [ordered]@{
            embedded_sys_authenticode_status = $sysSignature.authenticode_status
            embedded_sys_authenticode_digest = $sysSignature.embedded_authenticode_digest
            embedded_sys_cms_signature = $sysSignature.embedded_cms_signature
            embedded_sys_wvt_status = $sysSignature.native_trust_status
            catalog_authenticode_status = $catSignature.authenticode_status
            catalog_wvt_status = $catSignature.native_trust_status
            expected_untrusted_root_evidence = $sysSignature.chain_trust_classification
            catalog_cms_method = $catCmsSignature.method
            catalog_member_method = $sysMembership.method
        }
        validation = $validation
        limitations = @(
            'TestOnly package; not Microsoft-signed.',
            'No Trusted Root or Trusted Publisher import was performed.',
            'No boot policy, Secure Boot, Test Mode, driver installation, or hardware state was changed or tested.'
        )
    }
    $manifestPath = Join-Path $stageDirectory 'test-signed-package-manifest.json'
    $signedManifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $reparsedManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ((Get-ObjectProperty $reparsedManifest 'status') -cne 'TEST_ONLY_SIGNED_PACKAGE' -or
        (Get-ObjectProperty $reparsedManifest 'trust') -cne 'TestOnly' -or
        (Get-ObjectProperty $reparsedManifest 'microsoft_signature') -cne 'NOT_PRESENT' -or
        (Get-ObjectProperty $reparsedManifest 'package_directory') -cne 'ClaspAAP' -or
        (Get-ObjectProperty $reparsedManifest 'signed_package_directory') -cne $finalPackageDirectory) {
        throw 'The serialized output manifest failed its required TestOnly package identity reparse.'
    }
    $reparsedHashes = Get-ObjectProperty $reparsedManifest 'artifact_sha256'
    foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -File) {
        $serializedExpectedHash = [string](Get-ObjectProperty $reparsedHashes $file.Name)
        if ($serializedExpectedHash -cne (Get-Sha256 $file.FullName)) {
            throw "The serialized output manifest does not match package file '$($file.Name)'."
        }
    }

    if (Test-Path -LiteralPath $OutputDirectory) {
        throw "OutputDirectory appeared during packaging and will not be overwritten: $OutputDirectory"
    }
    Assert-ContainedStagingDirectory -StageDirectory $stageDirectory -OutputDirectory $OutputDirectory -OutputParent $stageParent
    [IO.Directory]::Move($stageDirectory, $OutputDirectory)
    $published = $true
    [pscustomobject]@{
        status = 'TEST_ONLY_SIGNED_PACKAGE'
        output_directory = $OutputDirectory
        package_directory = $finalPackageDirectory
        manifest_path = Join-Path $OutputDirectory 'test-signed-package-manifest.json'
        signer_thumbprint = $signerThumbprint
        trust = 'TestOnly'
        microsoft_signature = 'NOT_PRESENT'
        validation = $validation
    }
}
catch {
    $failureMessage = $_.Exception.Message
    if ($certificateCreated -and $createdCertificateThumbprint) {
        throw "TestOnly package preparation failed after creating certificate $createdCertificateThumbprint in Cert:\CurrentUser\My. The certificate remains there; work and certificate receipt are preserved at '$stageDirectory'. Original failure: $failureMessage"
    }
    throw
}
finally {
    if ($stageCreated -and -not $published -and (Test-Path -LiteralPath $stageDirectory)) {
        if ($certificateCreated) {
            try { Assert-ContainedStagingDirectory -StageDirectory $stageDirectory -OutputDirectory $OutputDirectory -OutputParent $stageParent }
            catch { Write-Warning "The failed stage was retained; containment review could not be completed: $($_.Exception.Message)" }
        }
        else {
            try {
                Assert-ContainedStagingDirectory -StageDirectory $stageDirectory -OutputDirectory $OutputDirectory -OutputParent $stageParent
                Remove-Item -LiteralPath $stageDirectory -Recurse -Force -ErrorAction Stop
            }
            catch {
                Write-Warning "The incomplete stage was retained because safe cleanup could not be verified: $($_.Exception.Message)"
            }
        }
    }
}

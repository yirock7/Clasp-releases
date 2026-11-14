<#
.SYNOPSIS
  Checks that a downloaded Clasp installer was signed by the Clasp release key.

.DESCRIPTION
  Works on Windows PowerShell 5.1 and PowerShell 7. It does not run the installer.
  The check rebuilds the exact message the release owner signed (version, file name and the
  file's SHA-256) and verifies the .sig file against the public key built into Clasp.

.EXAMPLE
  .\Verify-ClaspInstaller.ps1 -Installer .\Clasp-0.3.0-win-x64-setup.exe

.NOTES
  -PublicKey exists for testing only. Leave it out when checking a real download.
  Exit code 0 means the signature is good; 1 means do not run the installer; 2 means bad usage.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [string]$Signature,
    # Base64 SubjectPublicKeyInfo. Default is UpdateSignature.PublicKey from the Clasp source.
    [string]$PublicKey = 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE9I0IiAy9w48j6+xZHSMC7v2Lgi7LC+WfSeD4bmWdepJhBFAn/DzxL9dOqomxFzk6tRH7Ztw4SDv9nngHDTE2YQ=='
)

$ErrorActionPreference = 'Stop'

function Fail([string]$reason) {
    [Console]::Error.WriteLine("Do NOT run this installer. $reason")
    exit 1
}

try {
    if (-not (Test-Path -LiteralPath $Installer -PathType Leaf)) { Fail "File not found: $Installer" }
    $installerPath = (Resolve-Path -LiteralPath $Installer).Path
    $name = [System.IO.Path]::GetFileName($installerPath)

    # Same rule the app uses: Clasp-X.Y.Z-win-x64-setup.exe, numeric parts without leading zeros.
    $m = [regex]::Match($name, '^Clasp-(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-win-x64-setup\.exe$')
    if (-not $m.Success) { Fail "The file name '$name' is not of the form Clasp-X.Y.Z-win-x64-setup.exe. Keep the name the release page gave it." }
    $version = '{0}.{1}.{2}' -f $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value

    if (-not $Signature) { $Signature = $installerPath + '.sig' }
    if (-not (Test-Path -LiteralPath $Signature -PathType Leaf)) { Fail "Signature file not found: $Signature (download the .sig file from the same release page)." }

    # Signature: Base64 of 64 bytes (r || s).
    $sigText = (Get-Content -LiteralPath $Signature -Raw)
    if ($null -eq $sigText) { $sigText = '' }
    try { $sig = [Convert]::FromBase64String($sigText.Trim()) } catch { Fail 'The .sig file is not valid. It may be damaged or incomplete.' }
    if ($sig.Length -ne 64) { Fail 'The .sig file is not valid. It may be damaged or incomplete.' }

    # Public key: 91-byte SPKI for P-256 = 26-byte fixed header + 0x04 + X(32) + Y(32).
    try { $spki = [Convert]::FromBase64String($PublicKey) } catch { Fail 'The public key is not valid Base64.' }
    $header = [Convert]::FromBase64String('MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE')   # 27 bytes: header incl. 0x04 point marker
    $ok = ($spki.Length -eq 91)
    if ($ok) { for ($i = 0; $i -lt $header.Length; $i++) { if ($spki[$i] -ne $header[$i]) { $ok = $false; break } } }
    if (-not $ok) { Fail 'The public key is not a P-256 key.' }

    # ECCPublicKeyBlob: magic 'ECS1' (0x31534345), key length 32, then X || Y.
    $blob = New-Object byte[] 72
    [BitConverter]::GetBytes([uint32]0x31534345).CopyTo($blob, 0)
    [BitConverter]::GetBytes([uint32]32).CopyTo($blob, 4)
    [Array]::Copy($spki, 27, $blob, 8, 64)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($installerPath)
    try { $hash = $sha.ComputeHash($stream) } finally { $stream.Dispose(); $sha.Dispose() }
    $hex = ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()

    $message = [System.Text.Encoding]::UTF8.GetBytes("clasp-update-v1`nversion=$version`nfile=$name`nsha256=$hex`n")

    $cng = [System.Security.Cryptography.CngKey]::Import($blob, [System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $valid = $false
    try {
        $ecdsa = New-Object System.Security.Cryptography.ECDsaCng($cng)
        try { $valid = $ecdsa.VerifyData($message, $sig, [System.Security.Cryptography.HashAlgorithmName]::SHA256) }
        finally { $ecdsa.Dispose() }
    } finally { $cng.Dispose() }

    if (-not $valid) { Fail "The signature does not match this file (Clasp $version). It may have been changed, or it is not an official release." }

    Write-Host "Signature OK: Clasp $version"
    exit 0
}
catch {
    Fail "Could not complete the check: $($_.Exception.Message)"
}

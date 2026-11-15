<#
.SYNOPSIS
Shared safe-file helpers for Install-TestDriver.ps1 and Set-TestDriverTrust.ps1.
.DESCRIPTION
Dot-source only; this file performs no action when loaded. It exists to close
time-of-check/time-of-use gaps:
  * a file is read ONCE into a byte array; the hash is computed over those exact
    bytes and anything parsed comes from those same bytes (never reopen a path);
  * verified bytes are copied into a fresh snapshot directory under
    %ProgramData%\Clasp\driver-staging that only SYSTEM and Administrators can
    write, with no reparse points (junctions/symlinks) anywhere on the path;
  * the snapshot is re-hashed immediately before every privileged step;
  * receipts live in the same administrator-only area and are accepted only if
    their SHA-256 equals an externally supplied -ExpectedReceiptHash.
Nothing here changes certificate stores, the driver store, or device ACLs.
#>

function Read-AllBytes([string]$Path) {
    # Single choke point for reading untrusted files (mocked by the tests).
    return [IO.File]::ReadAllBytes($Path)
}

function Get-BytesSha256([byte[]]$Bytes) {
    if ($null -eq $Bytes) { return $null }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function ConvertFrom-Utf8JsonBytes([byte[]]$Bytes) {
    $text = [Text.UTF8Encoding]::new($false).GetString($Bytes)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
    return ($text | ConvertFrom-Json -ErrorAction Stop)
}

function Read-VerifiedBytes([string]$Path, [string]$ExpectedSha256, [string]$Label) {
    # Read once, hash those exact bytes, compare, and hand the bytes back.
    # Source files may live anywhere; what matters is that the bytes used are the bytes hashed.
    $full = [IO.Path]::GetFullPath($Path)
    $bytes = Read-AllBytes $full
    $actual = Get-BytesSha256 $bytes
    if ($actual -cne $ExpectedSha256.ToUpperInvariant()) { throw "$Label SHA-256 does not match the externally reviewed expected hash." }
    return [ordered]@{ path = $full; bytes = $bytes; sha256 = $actual }
}

function Read-VerifiedManifest([string]$Path, [string]$ExpectedHash) {
    # The hash is checked BEFORE the JSON is parsed, and the JSON comes from the hashed bytes.
    $read = Read-VerifiedBytes $Path $ExpectedHash 'Manifest'
    return [ordered]@{ path = $read.path; sha256 = $read.sha256; manifest = (ConvertFrom-Utf8JsonBytes $read.bytes) }
}

function Assert-NoReparsePath([string]$Path) {
    # Rejects a reparse point (junction, symlink, mount point) on the path or any existing ancestor.
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing a path containing a reparse point: $current" }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent) -or $parent -ceq $current) { break }
        $current = $parent
    }
}

function Get-AdminSids {
    return @(
        [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::LocalSystemSid, $null),
        [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    )
}

function Set-AdminOnlyAcl([string]$Path) {
    # Explicit protected DACL: SYSTEM and Administrators only, inheritance disabled.
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in (Get-AdminSids)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, ([Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'), [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $security
}

function Assert-AdminOnlyAcl([string]$Path) {
    $security = Get-Acl -LiteralPath $Path
    if (-not $security.AreAccessRulesProtected) { throw "Directory inherits permissions and is not administrator-only: $Path" }
    $allowed = @((Get-AdminSids) | ForEach-Object { $_.Value })
    $owner = $security.GetOwner([Security.Principal.SecurityIdentifier]).Value
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($owner -notin ($allowed + $currentSid)) { throw "Directory owner is not SYSTEM, Administrators or the elevated caller: $Path" }
    foreach ($rule in $security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $rule.IdentityReference.Value -notin $allowed) {
            throw "Directory ACL grants or denies access to a principal other than SYSTEM/Administrators: $Path ($($rule.IdentityReference.Value))"
        }
    }
}

function Get-ClaspStagingRoot {
    $programData = $env:ProgramData
    if (-not $programData) { throw 'ProgramData is not defined; cannot locate the administrator-only staging area.' }
    return [IO.Path]::GetFullPath((Join-Path $programData 'Clasp\driver-staging'))
}

function Initialize-AdminOnlyDirectory([string]$Path) {
    # Creates (or re-validates) one directory so only SYSTEM and Administrators can use it.
    Assert-NoReparsePath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        [void][IO.Directory]::CreateDirectory($Path)
        Set-AdminOnlyAcl $Path
    }
    Assert-NoReparsePath $Path
    Assert-AdminOnlyAcl $Path
}

function Initialize-StagingRoot {
    $root = Get-ClaspStagingRoot
    $parent = [IO.Path]::GetDirectoryName($root)
    Assert-NoReparsePath $parent
    # The shared "Clasp" folder is only created here (admin-only) or accepted when it already
    # exists with an administrator owner and no reparse point; the staging folder below is strict.
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { Initialize-AdminOnlyDirectory $parent }
    Initialize-AdminOnlyDirectory $root
    return $root
}

function Get-ReceiptDirectory {
    $directory = Join-Path (Get-ClaspStagingRoot) 'receipts'
    return $directory
}

function Initialize-ReceiptDirectory {
    [void](Initialize-StagingRoot)
    $directory = Get-ReceiptDirectory
    Initialize-AdminOnlyDirectory $directory
    return $directory
}

function Resolve-ReceiptLocation([string]$Path, [string]$Label) {
    # A bare file name lands in the receipts directory; a full path must already be inside it.
    if (-not $Path) { throw "$Label is required." }
    $directory = Get-ReceiptDirectory
    $full = if ([IO.Path]::IsPathRooted($Path)) { [IO.Path]::GetFullPath($Path) } else { [IO.Path]::GetFullPath((Join-Path $directory $Path)) }
    $prefix = $directory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be inside the administrator-only receipt directory $directory (give a bare file name or a path under it)."
    }
    if ($full.Substring($prefix.Length).IndexOfAny([char[]]@('\', '/')) -ge 0) { throw "$Label must be a file directly in $directory." }
    return $full
}

function Read-HashedReceipt([string]$Path, [string]$ExpectedReceiptHash, [string]$Label) {
    # Authenticates a receipt: lives in the admin-only receipt directory, and the SHA-256 of the
    # exact bytes parsed equals the externally supplied hash. Any change (including only the
    # before.device_security.sddl field) therefore invalidates it before anything is acted on.
    if (-not $ExpectedReceiptHash -or $ExpectedReceiptHash -notmatch '^[A-Fa-f0-9]{64}$') { throw "-ExpectedReceiptHash (SHA-256 printed when the $Label was written) is required." }
    $full = Resolve-ReceiptLocation $Path $Label
    Assert-NoReparsePath $full
    Assert-AdminOnlyAcl (Get-ReceiptDirectory)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "$Label was not found: $full" }
    $bytes = Read-AllBytes $full
    $actual = Get-BytesSha256 $bytes
    if ($actual -cne $ExpectedReceiptHash.ToUpperInvariant()) { throw "$Label SHA-256 does not match -ExpectedReceiptHash; the receipt was changed or is not the one recorded. Nothing was changed." }
    return [ordered]@{ path = $full; sha256 = $actual; receipt = (ConvertFrom-Utf8JsonBytes $bytes) }
}

function New-StagingSnapshot {
    # Fresh GUID directory under the admin-only root. Inherits the protected ACL and is re-checked.
    $root = Initialize-StagingRoot
    $directory = Join-Path $root ([Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $directory) { throw 'Snapshot directory unexpectedly exists.' }
    [void][IO.Directory]::CreateDirectory($directory)
    Set-AdminOnlyAcl $directory
    Assert-NoReparsePath $directory
    Assert-AdminOnlyAcl $directory
    return [ordered]@{ directory = $directory; files = [ordered]@{} }
}

function Add-SnapshotFile([object]$Snapshot, [string]$Name, [byte[]]$VerifiedBytes, [string]$ExpectedSha256) {
    # Writes bytes that were already hashed against the manifest; the name must be a plain leaf.
    if ($Name -cne [IO.Path]::GetFileName($Name)) { throw "Snapshot file name is not a plain leaf name: $Name" }
    $actual = Get-BytesSha256 $VerifiedBytes
    if ($actual -cne $ExpectedSha256.ToUpperInvariant()) { throw "Bytes for $Name do not match the manifest hash; not snapshotting." }
    $target = Join-Path $Snapshot.directory $Name
    $stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($VerifiedBytes, 0, $VerifiedBytes.Length) }
    finally { $stream.Dispose() }
    $Snapshot.files[$Name] = $actual
    return $target
}

function Assert-SnapshotIntact([object]$Snapshot, [string]$Reason) {
    # Re-hash every snapshot file right now. Call immediately before each privileged step.
    Assert-NoReparsePath $Snapshot.directory
    Assert-AdminOnlyAcl $Snapshot.directory
    $present = @(Get-ChildItem -LiteralPath $Snapshot.directory -Force | ForEach-Object { $_.Name })
    if ($present.Count -ne $Snapshot.files.Count) { throw "Snapshot contents changed before ${Reason}: unexpected file count." }
    foreach ($name in $Snapshot.files.Keys) {
        $path = Join-Path $Snapshot.directory $name
        Assert-NoReparsePath $path
        if ((Get-BytesSha256 (Read-AllBytes $path)) -cne $Snapshot.files[$name]) { throw "Snapshot file $name changed after validation; refusing $Reason." }
    }
}

function Remove-StagingSnapshot([object]$Snapshot) {
    if ($null -eq $Snapshot -or -not $Snapshot.directory) { return }
    $root = (Get-ClaspStagingRoot).TrimEnd('\') + '\'
    if (-not $Snapshot.directory.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { return }
    try {
        Assert-NoReparsePath $Snapshot.directory
        if (Test-Path -LiteralPath $Snapshot.directory) { Remove-Item -LiteralPath $Snapshot.directory -Recurse -Force -ErrorAction Stop }
    }
    catch { Write-Warning "Could not remove snapshot $($Snapshot.directory): $($_.Exception.Message)" }
}

function Write-JsonJournal([string]$Path, [object]$Value) {
    # Receipts are written to a temp file in the same administrator-only directory, then moved.
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Receipt directory does not exist: $directory" }
    Assert-NoReparsePath $Path
    $temporary = $Path + '.tmp-' + [Guid]::NewGuid().ToString('N')
    $encoding = [System.Text.UTF8Encoding]::new($false)
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 14), $encoding)
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, $null) }
        else { [IO.File]::Move($temporary, $Path) }
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Publish-ReceiptHash([string]$Path) {
    # SHA-256 of the exact receipt bytes as stored. Printed so the operator can record it and
    # later pass it as -ExpectedReceiptHash.
    $hash = Get-BytesSha256 (Read-AllBytes $Path)
    Write-Host "Receipt: $Path"
    Write-Host "Receipt SHA-256 (record this; pass as -ExpectedReceiptHash to later steps): $hash"
    return $hash
}

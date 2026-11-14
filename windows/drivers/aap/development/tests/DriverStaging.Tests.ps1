<#
Offline Pester tests (written for Pester 3.4+, the version that ships with Windows PowerShell 5.1)
for DriverStaging.ps1, the helper behind Install-TestDriver.ps1 and Set-TestDriverTrust.ps1.

Run:  Invoke-Pester -Path <this folder>

Safety: nothing here touches certificate stores, the driver store, device ACLs or the real
%ProgramData%. ProgramData is redirected to a throw-away temp folder, the ACL functions are
mocked, and every "privileged" step is a mocked stand-in that the tests assert was NOT called.
#>

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$helperPath = Join-Path (Split-Path -Parent $here) 'DriverStaging.ps1'

Describe 'DriverStaging helpers' {
    . $helperPath

    $sandbox = Join-Path ([IO.Path]::GetTempPath()) ('ClaspDriverStagingTests-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($sandbox)
    $savedProgramData = $env:ProgramData
    $env:ProgramData = $sandbox

    # No real ACL work: both are mocked. Everything else (hashing, reparse checks, file IO) is real.
    Mock Set-AdminOnlyAcl { }
    Mock Assert-AdminOnlyAcl { }

    function New-TestManifestBytes([string]$Thumbprint) {
        $json = '{"schema_version":1,"certificate":{"thumbprint":"' + $Thumbprint + '","public_certificate_sha256":"' + ('0' * 64) + '"}}'
        return , ([Text.UTF8Encoding]::new($false).GetBytes($json))
    }

    Context 'single read of the manifest' {
        It 'two alternating manifests cannot mix digest and certificate' {
            $bytesA = New-TestManifestBytes ('A' * 40)
            $bytesB = New-TestManifestBytes ('B' * 40)
            $hashA = Get-BytesSha256 $bytesA
            $global:AlternatingReads = 0
            $global:AlternatingA = $bytesA
            $global:AlternatingB = $bytesB
            # A TOCTOU attacker flips the file between reads: first read A, every later read B.
            Mock Read-AllBytes { $global:AlternatingReads++; if ($global:AlternatingReads -eq 1) { , $global:AlternatingA } else { , $global:AlternatingB } }

            $result = Read-VerifiedManifest (Join-Path $sandbox 'manifest.json') $hashA

            Assert-MockCalled Read-AllBytes -Exactly 1 -Scope It
            # Digest and parsed content both come from manifest A: they can never be mixed with B.
            $result.sha256 | Should Be $hashA
            $result.manifest.certificate.thumbprint | Should Be ('A' * 40)
        }

        It 'rejects a manifest whose bytes do not match the expected hash, before parsing' {
            $bytesA = New-TestManifestBytes ('A' * 40)
            $bytesB = New-TestManifestBytes ('B' * 40)
            $hashA = Get-BytesSha256 $bytesA
            $global:SwappedB = $bytesB
            Mock Read-AllBytes { , $global:SwappedB }
            { Read-VerifiedManifest (Join-Path $sandbox 'manifest.json') $hashA } | Should Throw 'does not match the externally reviewed expected hash'
        }
    }

    Context 'snapshot' {
        It 'rejects a snapshot file replaced after validation' {
            $snapshot = New-StagingSnapshot
            $original = [Text.Encoding]::UTF8.GetBytes('reviewed package bytes')
            [void](Add-SnapshotFile $snapshot 'NTPodsAAP.inf' $original (Get-BytesSha256 $original))
            { Assert-SnapshotIntact $snapshot 'test step' } | Should Not Throw

            [IO.File]::WriteAllBytes((Join-Path $snapshot.directory 'NTPodsAAP.inf'), [Text.Encoding]::UTF8.GetBytes('attacker replacement'))
            { Assert-SnapshotIntact $snapshot 'pnputil /add-driver' } | Should Throw 'changed after validation'
            Remove-StagingSnapshot $snapshot
        }

        It 'rejects an extra file planted in the snapshot' {
            $snapshot = New-StagingSnapshot
            $original = [Text.Encoding]::UTF8.GetBytes('reviewed package bytes')
            [void](Add-SnapshotFile $snapshot 'NTPodsAAP.inf' $original (Get-BytesSha256 $original))
            [IO.File]::WriteAllBytes((Join-Path $snapshot.directory 'evil.dll'), [byte[]](1, 2, 3))
            { Assert-SnapshotIntact $snapshot 'test step' } | Should Throw 'unexpected file count'
            Remove-StagingSnapshot $snapshot
        }

        It 'refuses to snapshot bytes that do not match the manifest hash' {
            $snapshot = New-StagingSnapshot
            $bytes = [Text.Encoding]::UTF8.GetBytes('not the reviewed file')
            { Add-SnapshotFile $snapshot 'NTPodsAAP.sys' $bytes ('0' * 64) } | Should Throw 'do not match the manifest hash'
            Remove-StagingSnapshot $snapshot
        }

        It 'is created under the ProgramData staging root and removed at the end' {
            $snapshot = New-StagingSnapshot
            $snapshot.directory.StartsWith((Get-ClaspStagingRoot), [StringComparison]::OrdinalIgnoreCase) | Should Be $true
            Remove-StagingSnapshot $snapshot
            Test-Path -LiteralPath $snapshot.directory | Should Be $false
        }

        It 'rejects a reparse point (junction) on the path' {
            $target = Join-Path $sandbox 'junction-target'
            $link = Join-Path $sandbox 'junction-link'
            [void][IO.Directory]::CreateDirectory($target)
            cmd /c mklink /J "$link" "$target" | Out-Null
            if (Test-Path -LiteralPath $link) {
                { Assert-NoReparsePath (Join-Path $link 'child') } | Should Throw 'reparse point'
                cmd /c rmdir "$link" | Out-Null
            }
        }
    }

    Context 'receipt authenticity' {
        function New-TestReceipt([string]$Sddl) {
            [void](Initialize-ReceiptDirectory)
            $path = Join-Path (Get-ReceiptDirectory) ('stage-' + [Guid]::NewGuid().ToString('N') + '.json')
            $json = '{"schema_version":1,"action":"StagePackage","before":{"device_security":{"sddl":"' + $Sddl + '"}}}'
            [IO.File]::WriteAllBytes($path, [Text.UTF8Encoding]::new($false).GetBytes($json))
            return $path
        }

        It 'accepts a receipt whose bytes hash to -ExpectedReceiptHash' {
            $path = New-TestReceipt 'D:P(A;;GA;;;SY)(A;;GA;;;BA)'
            $hash = Get-BytesSha256 ([IO.File]::ReadAllBytes($path))
            $read = Read-HashedReceipt $path $hash 'Rollback receipt'
            $read.receipt.before.device_security.sddl | Should Be 'D:P(A;;GA;;;SY)(A;;GA;;;BA)'
        }

        It 'rejects a receipt where only the prior SDDL changed, before anything is changed' {
            $path = New-TestReceipt 'D:P(A;;GA;;;SY)(A;;GA;;;BA)'
            $hash = Get-BytesSha256 ([IO.File]::ReadAllBytes($path))
            $text = [IO.File]::ReadAllText($path)
            [IO.File]::WriteAllText($path, $text.Replace('(A;;GA;;;BA)', '(A;;GA;;;WD)'))   # grants Everyone

            function Set-FakeDeviceAcl([string]$Sddl) { throw 'privileged stand-in must be mocked' }
            Mock Set-FakeDeviceAcl { }
            function Invoke-FakeRollback([string]$ReceiptFile, [string]$Hash) {
                $receipt = Read-HashedReceipt $ReceiptFile $Hash 'Rollback receipt'
                Set-FakeDeviceAcl $receipt.receipt.before.device_security.sddl
            }
            { Invoke-FakeRollback $path $hash } | Should Throw 'does not match -ExpectedReceiptHash'
            Assert-MockCalled Set-FakeDeviceAcl -Exactly 0 -Scope It
        }

        It 'requires a well-formed -ExpectedReceiptHash' {
            $path = New-TestReceipt 'D:P(A;;GA;;;SY)'
            { Read-HashedReceipt $path '' 'Rollback receipt' } | Should Throw 'ExpectedReceiptHash'
            { Read-HashedReceipt $path 'not-a-hash' 'Rollback receipt' } | Should Throw 'ExpectedReceiptHash'
        }

        It 'rejects a receipt stored outside the administrator-only receipt directory' {
            $outside = Join-Path $sandbox 'elsewhere.json'
            [IO.File]::WriteAllText($outside, '{}')
            $hash = Get-BytesSha256 ([IO.File]::ReadAllBytes($outside))
            { Read-HashedReceipt $outside $hash 'Rollback receipt' } | Should Throw 'administrator-only receipt directory'
        }

        It 'places a bare receipt file name inside the receipt directory' {
            (Resolve-ReceiptLocation 'stage.json' '-ReceiptPath') | Should Be (Join-Path (Get-ReceiptDirectory) 'stage.json')
            { Resolve-ReceiptLocation '..\stage.json' '-ReceiptPath' } | Should Throw
        }
    }

    $env:ProgramData = $savedProgramData
    if ($sandbox.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $sandbox).StartsWith('ClaspDriverStagingTests-')) {
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

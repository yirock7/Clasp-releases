<#
Offline Pester tests (Pester 3.4+) for two helpers inside Install-TestDriver.ps1.

The script is never run: its function definitions are pulled out with the PowerShell parser and defined here, so
nothing touches the driver store, devices, certificates or ProgramData. pnputil is never invoked: the attribution
helper under test is pure.
#>

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptPath = Join-Path (Split-Path -Parent $here) 'Install-TestDriver.ps1'

Describe 'Install-TestDriver helpers' {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
    foreach ($name in 'Assert-ValidSddl', 'Get-AttributedUnexpectedPackages') {
        $def = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
        if (-not $def) { throw "Function $name not found in Install-TestDriver.ps1" }
        . ([scriptblock]::Create($def.Extent.Text))
    }

    Context 'Assert-ValidSddl with no explicit descriptor' {
        It 'accepts $null (a baseline with no SPDRP_SECURITY_SDS) without throwing' {
            { Assert-ValidSddl $null } | Should Not Throw
        }
        It 'accepts a null that arrives through a variable' {
            $absent = $null
            { Assert-ValidSddl $absent } | Should Not Throw
        }
        It 'still rejects an invalid descriptor' {
            { Assert-ValidSddl 'not an sddl' } | Should Throw
        }
        It 'accepts a valid descriptor' {
            { Assert-ValidSddl 'O:BAG:BAD:(A;;GA;;;BA)' } | Should Not Throw
        }
    }

    Context 'Get-AttributedUnexpectedPackages' {
        It 'never removes a package Windows Update staged meanwhile; it only reports it' {
            $r = Get-AttributedUnexpectedPackages 'oem12.inf' @('oem1.inf') @('oem1.inf', 'oem12.inf', 'oem13.inf') @([pscustomobject]@{ published_inf = 'oem12.inf' }) 'oem12.inf'
            @($r.remove).Count | Should Be 0
            ($r.unrelated_not_removed -join ',') | Should Be 'oem13.inf'
        }
        It 'removes the package pnputil printed when it is not the expected one' {
            $r = Get-AttributedUnexpectedPackages 'oem20.inf' @('oem1.inf') @('oem1.inf', 'oem20.inf', 'oem21.inf') @() $null
            ($r.remove -join ',') | Should Be 'oem20.inf'
            ($r.unrelated_not_removed -join ',') | Should Be 'oem21.inf'
        }
        It 'removes an extra new package whose name and hash match this run, besides the expected one' {
            $matches = @([pscustomobject]@{ published_inf = 'oem30.inf' }, [pscustomobject]@{ published_inf = 'oem31.inf' })
            $r = Get-AttributedUnexpectedPackages 'oem30.inf' @('oem1.inf') @('oem1.inf', 'oem30.inf', 'oem31.inf') $matches 'oem30.inf'
            ($r.remove -join ',') | Should Be 'oem31.inf'
        }
        It 'never touches a name that existed before the run, even if pnputil printed it' {
            $r = Get-AttributedUnexpectedPackages 'oem1.inf' @('oem1.inf') @('oem1.inf') @() $null
            @($r.remove).Count | Should Be 0
        }
        It 'removes nothing when no new package appeared' {
            $r = Get-AttributedUnexpectedPackages '' @('oem1.inf') @('oem1.inf') @() $null
            @($r.remove).Count | Should Be 0
            @($r.unrelated_not_removed).Count | Should Be 0
        }
    }
}

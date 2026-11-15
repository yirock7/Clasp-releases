<#
.SYNOPSIS
Collects read-only Windows test-host and selected AAP device state as JSON.
.DESCRIPTION
This collector never changes boot, security, certificate, driver, or pairing state.
Unavailable reads remain unavailable; they are never represented as disabled.
BitLocker output is limited to protection/volume status and never queries keys or
key protectors. Device discovery is pinned to the selected AAP Server instance,
hardware GUID, and Bluetooth address. The nearby AAP Client address is excluded.
.PARAMETER InstanceId
Full device instance path of the AAP Server node, for example
BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_VID&0001004C_PID&2027\9&1a2b3c4d&0&001122334455_C00000000
(Device Manager > Properties > Details > Device instance path). Required; there is no default.
.PARAMETER BluetoothAddress
The 12-hex-digit Bluetooth address of the selected device, for example 001122334455. Required.
.PARAMETER ExcludedBluetoothAddress
Optional 12-hex-digit address of a nearby device (the AAP Client) that must not be selected.
.EXAMPLE
.\Get-TestHostState.ps1 -InstanceId 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_VID&0001004C_PID&2027\9&1a2b3c4d&0&001122334455_C00000000' -BluetoothAddress 001122334455
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$InstanceId,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{12}$')][string]$BluetoothAddress,
    [ValidatePattern('^([A-Fa-f0-9]{12})?$')][string]$ExcludedBluetoothAddress = '',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:ExpectedInstanceId = $InstanceId
$script:ExpectedHardwareGuid = '{74EC2172-0BAD-4D01-8F77-997B2BE0722A}'
$script:ExpectedHardwareId = 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_VID&0001004C_PID&2027'
$script:ExpectedCompatibleId = 'BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_GENERIC'
$script:ExpectedBluetoothAddress = $BluetoothAddress.ToUpperInvariant()
$script:ExcludedBluetoothAddress = $ExcludedBluetoothAddress.ToUpperInvariant()

function New-ReadResult {
    param([string]$Status, [object]$Value = $null, [string]$ErrorCategory = $null)
    return [ordered]@{ status = $Status; value = $Value; error_category = $ErrorCategory }
}

function Convert-PnpDevicePropertyReadResult {
    param([object]$Item)
    # DEVPROP_TYPE_EMPTY (0) is how PnP reports an unset property. Read Type
    # first: on Windows PowerShell 5, reading Data for this response can throw
    # a generic InvalidOperationException even though the PnP query succeeded.
    if ([uint32]$Item.Type -eq 0) {
        return [ordered]@{ status = 'NOT_SET'; value = $null; error_category = $null; source = 'DEVPROP_TYPE_EMPTY' }
    }
    return [ordered]@{ status = 'READ'; value = $Item.Data; error_category = $null; source = 'PNP_PROPERTY' }
}

function Get-DevicePropertyValue {
    param([string]$InstanceId, [string]$KeyName)
    try {
        $item = Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName $KeyName -ErrorAction Stop
        return Convert-PnpDevicePropertyReadResult $item
    }
    catch {
        $status = 'UNAVAILABLE'
        # CM/SetupAPI's HRESULT_FROM_WIN32(ERROR_NOT_FOUND) means the property
        # is absent on this node. Keep that distinct from access/query failure.
        if ($_.Exception.HResult -eq -2147023728 -or $_.CategoryInfo.Category -eq 'ObjectNotFound') { $status = 'NOT_SET' }
        $hresultBytes = [BitConverter]::GetBytes([int32]$_.Exception.HResult)
        $hresultValue = [BitConverter]::ToUInt32($hresultBytes, 0)
        return [ordered]@{ status = $status; value = $null; error_category = $_.CategoryInfo.Category.ToString(); hresult = ('0x{0:X8}' -f $hresultValue) }
    }
}

function Convert-HvciServiceCode {
    param([object]$Code, [hashtable]$Labels)
    $numeric = [int]$Code
    if ($Labels.ContainsKey($numeric)) { return $Labels[$numeric] }
    return ('Unknown:' + $numeric)
}

function Get-SecureBootState {
    $firmware = [ordered]@{ status = 'UNAVAILABLE'; enabled = $null; error_category = $null }
    try {
        $confirmed = Confirm-SecureBootUEFI -ErrorAction Stop
        $firmware.status = 'READ'
        $firmware.enabled = [bool]$confirmed
    }
    catch {
        # Do not classify firmware support by parsing a localized error string.
        $firmware.error_category = $_.CategoryInfo.Category.ToString()
    }

    $registry = [ordered]@{ status = 'UNAVAILABLE'; enabled = $null; source = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled'; error_category = $null }
    try {
        $state = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' -Name 'UEFISecureBootEnabled' -ErrorAction Stop
        $raw = [int]$state.UEFISecureBootEnabled
        if ($raw -eq 0 -or $raw -eq 1) {
            $registry.status = 'READ'
            $registry.enabled = [bool]$raw
        }
        else {
            $registry.status = 'UNVERIFIED_VALUE'
            $registry.error_category = 'UnexpectedRegistryValue'
        }
    }
    catch {
        $registry.error_category = $_.CategoryInfo.Category.ToString()
    }

    return [ordered]@{ firmware = $firmware; registry = $registry }
}

function Get-HvciVbsState {
    $result = [ordered]@{
        query_status = 'UNAVAILABLE'; error_category = $null; virtualization_based_security_status = $null
        virtualization_based_security = 'UNVERIFIED'; hvci_configured = $null; hvci_running = $null
        configured_security_services = @(); running_security_services = @(); registry_hvci_enabled = $null
    }
    $labels = @{ 1 = 'CredentialGuard'; 2 = 'HVCI'; 3 = 'SystemGuardSecureLaunch'; 4 = 'SmmFirmwareMeasurement'; 5 = 'KernelModeHardwareStackProtection'; 6 = 'KernelModeHardwareStackProtectionStrict'; 7 = 'HypervisorEnforcedPagingTranslation' }
    try {
        $guard = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName 'Win32_DeviceGuard' -ErrorAction Stop | Select-Object -First 1
        if ($null -eq $guard) { throw 'DeviceGuardCimInstanceMissing' }
        $configured = @($guard.SecurityServicesConfigured | ForEach-Object { Convert-HvciServiceCode $_ $labels })
        $running = @($guard.SecurityServicesRunning | ForEach-Object { Convert-HvciServiceCode $_ $labels })
        $result.query_status = 'READ'
        $result.virtualization_based_security_status = [int]$guard.VirtualizationBasedSecurityStatus
        switch ([int]$guard.VirtualizationBasedSecurityStatus) {
            0 { $result.virtualization_based_security = 'NOT_ENABLED' }
            1 { $result.virtualization_based_security = 'ENABLED_NOT_RUNNING' }
            2 { $result.virtualization_based_security = 'RUNNING' }
            default { $result.virtualization_based_security = 'UNVERIFIED_VALUE' }
        }
        $result.configured_security_services = $configured
        $result.running_security_services = $running
        $result.hvci_configured = ($configured -contains 'HVCI')
        $result.hvci_running = ($running -contains 'HVCI')
    }
    catch {
        $result.error_category = $_.CategoryInfo.Category.ToString()
    }

    try {
        $value = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -Name 'Enabled' -ErrorAction Stop).Enabled
        if ([int]$value -in @(0, 1)) { $result.registry_hvci_enabled = [bool][int]$value }
    }
    catch { }
    return $result
}

function Get-BitLockerProtectionState {
    $result = [ordered]@{ status = 'UNAVAILABLE'; mount_point = $env:SystemDrive; protection_status = $null; conversion_status = $null; encryption_percentage = $null; encryption_method = $null; error_category = $null; recovery_keys_queried = $false; key_protectors_queried = $false }
    try {
        $volumes = @(Get-CimInstance -Namespace 'root\CIMV2\Security\MicrosoftVolumeEncryption' -ClassName 'Win32_EncryptableVolume' -ErrorAction Stop)
        $volume = $volumes | Where-Object { [string]$_.DriveLetter -ieq $env:SystemDrive } | Select-Object -First 1
        if ($null -eq $volume) { throw 'SystemEncryptableVolumeNotReturned' }
        $protection = Invoke-CimMethod -InputObject $volume -MethodName 'GetProtectionStatus' -ErrorAction Stop
        if ([uint32]$protection.ReturnValue -ne 0) { throw 'BitLockerProtectionQueryFailed' }
        $conversion = Invoke-CimMethod -InputObject $volume -MethodName 'GetConversionStatus' -ErrorAction Stop
        if ([uint32]$conversion.ReturnValue -ne 0) { throw 'BitLockerConversionQueryFailed' }
        $protectionValue = [uint32]$protection.ProtectionStatus
        $conversionValue = [uint32]$conversion.ConversionStatus
        $percentageValue = [uint32]$conversion.EncryptionPercentage
        $result.status = 'READ'
        $result.protection_status = $protectionValue
        $result.conversion_status = $conversionValue
        $result.encryption_percentage = $percentageValue
    }
    catch {
        $result.error_category = $_.CategoryInfo.Category.ToString()
    }
    return $result
}

function Get-BootIntegrityState {
    $result = [ordered]@{
        status = 'UNAVAILABLE'; bcd_exit_code = $null; identifier = $null; test_signing = (New-ReadResult 'UNVERIFIED')
        no_integrity_checks = (New-ReadResult 'UNVERIFIED'); debug = (New-ReadResult 'UNVERIFIED'); boot_debug = (New-ReadResult 'UNVERIFIED'); error_category = $null
    }
    $bcd = Join-Path $env:WINDIR 'System32\bcdedit.exe'
    if (-not (Test-Path -LiteralPath $bcd)) { $result.error_category = 'BcdEditMissing'; return $result }
    try {
        $start = New-Object System.Diagnostics.ProcessStartInfo
        $start.FileName = $bcd
        $start.Arguments = '/enum {current} /v'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $start
        [void]$process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) {
            try { $process.Kill() } catch { }
            $result.status = 'TIMEOUT'
            $result.error_category = 'BcdReadTimedOut'
            return $result
        }
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        $result.bcd_exit_code = [int]$process.ExitCode
        if ($process.ExitCode -ne 0) {
            $result.error_category = 'BcdReadFailed'
            return $result
        }
        $identifierMatch = [regex]::Match($stdout, '(?im)^\s*identifier\s+(\{[0-9A-F-]{36}\})\s*$')
        if (-not $identifierMatch.Success) {
            $result.error_category = 'CurrentBcdIdentifierUnverified'
            return $result
        }
        $result.status = 'READ'
        $result.identifier = $identifierMatch.Groups[1].Value
        foreach ($entry in @(@('testsigning','test_signing'), @('nointegritychecks','no_integrity_checks'), @('debug','debug'), @('bootdebug','boot_debug'))) {
            $name = $entry[0]
            $target = $entry[1]
            $matches = [regex]::Matches($stdout, '(?im)^\s*' + [regex]::Escape($name) + '\s+(\S+)\s*$')
            if ($matches.Count -eq 0) {
                $result[$target] = New-ReadResult 'DEFAULTED' $false 'EntryNotReported;BCDEditCurrentDefault'
            }
            elseif ($matches.Count -eq 1 -and $matches[0].Groups[1].Value -in @('Yes','No')) {
                $result[$target] = New-ReadResult 'READ' ($matches[0].Groups[1].Value -ieq 'Yes')
            }
            else {
                $result[$target] = New-ReadResult 'UNVERIFIED' $null 'InvalidOrDuplicateBcdValue'
            }
        }
    }
    catch {
        $result.error_category = $_.CategoryInfo.Category.ToString()
    }
    return $result
}

function Get-SelectedAapDevice {
    $result = [ordered]@{ query_status = 'UNAVAILABLE'; instance_id = $script:ExpectedInstanceId; expected_hardware_guid = $script:ExpectedHardwareGuid; expected_bluetooth_address = $script:ExpectedBluetoothAddress; excluded_bluetooth_address = $script:ExcludedBluetoothAddress; device = $null; error_category = $null }
    try {
        $device = Get-PnpDevice -InstanceId $script:ExpectedInstanceId -PresentOnly -ErrorAction Stop
        if ($null -eq $device) {
            $result.query_status = 'NOT_FOUND'
            return $result
        }
        $hardwareIds = Get-DevicePropertyValue $script:ExpectedInstanceId 'DEVPKEY_Device_HardwareIds'
        $compatibleIds = Get-DevicePropertyValue $script:ExpectedInstanceId 'DEVPKEY_Device_CompatibleIds'
        $hardwareMatches = @($hardwareIds.value | ForEach-Object { [string]$_ }) -contains $script:ExpectedHardwareId
        $compatibleMatches = @($compatibleIds.value | ForEach-Object { [string]$_ }) -contains $script:ExpectedCompatibleId
        $instanceMatchesGuid = $script:ExpectedInstanceId -match [regex]::Escape($script:ExpectedHardwareGuid)
        $instanceMatchesAddress = $script:ExpectedInstanceId -match [regex]::Escape($script:ExpectedBluetoothAddress)
        $instanceIsExcluded = ($script:ExcludedBluetoothAddress -ne '') -and ($script:ExpectedInstanceId -match [regex]::Escape($script:ExcludedBluetoothAddress))
        if (-not $hardwareMatches -or -not $compatibleMatches -or -not $instanceMatchesGuid -or -not $instanceMatchesAddress -or $instanceIsExcluded) {
            $result.query_status = 'IDENTITY_MISMATCH'
            return $result
        }

        $service = Get-DevicePropertyValue $script:ExpectedInstanceId 'DEVPKEY_Device_Service'
        $infPath = Get-DevicePropertyValue $script:ExpectedInstanceId 'DEVPKEY_Device_DriverInfPath'
        $driverVersion = Get-DevicePropertyValue $script:ExpectedInstanceId 'DEVPKEY_Device_DriverVersion'
        $driverDate = Get-DevicePropertyValue $script:ExpectedInstanceId 'DEVPKEY_Device_DriverDate'
        $driverProvider = Get-DevicePropertyValue $script:ExpectedInstanceId 'DEVPKEY_Device_DriverProvider'
        $isPresent = Get-DevicePropertyValue $script:ExpectedInstanceId 'DEVPKEY_Device_IsPresent'
        $problemCode = Get-DevicePropertyValue $script:ExpectedInstanceId 'DEVPKEY_Device_ProblemCode'
        if ($isPresent.status -ne 'READ' -or $isPresent.value -ne $true) {
            $result.query_status = 'PRESENCE_UNVERIFIED'
            return $result
        }
        $systemDriver = $null
        if ($service.status -eq 'READ' -and $service.value) {
            try {
                $systemDriver = Get-CimInstance -ClassName Win32_SystemDriver -ErrorAction Stop |
                    Where-Object { [string]$_.Name -ieq [string]$service.value } |
                    Select-Object -First 1 Name,State,StartMode,PathName
            }
            catch { $systemDriver = [ordered]@{ query_status = 'UNAVAILABLE'; error_category = $_.CategoryInfo.Category.ToString() } }
        }
        $signedDriverStatus = 'UNAVAILABLE'
        $signedDriverError = $null
        $signedDriverInfo = $null
        try {
            $signedMatches = @(Get-CimInstance -ClassName Win32_PnPSignedDriver -ErrorAction Stop |
                Where-Object { [string]$_.DeviceID -ieq $script:ExpectedInstanceId })
            if ($signedMatches.Count -eq 0) {
                $signedDriverStatus = 'READ_NO_MATCH'
            }
            else {
                $signedDriverInfo = $signedMatches | Select-Object -First 1 DeviceName,InfName,DriverVersion,DriverProviderName,DriverDate,IsSigned
                $signedDriverStatus = 'READ'
            }
        }
        catch { $signedDriverError = $_.CategoryInfo.Category.ToString() }
        $signedDriver = [ordered]@{ query_status = $signedDriverStatus; error_category = $signedDriverError; details = $signedDriverInfo }

        $signedRowHasNoPackage = $signedDriverStatus -eq 'READ_NO_MATCH'
        if ($signedDriverStatus -eq 'READ' -and $null -ne $signedDriverInfo) {
            $signedRowHasNoPackage = -not [string]$signedDriverInfo.InfName -and -not [string]$signedDriverInfo.DriverVersion -and -not [string]$signedDriverInfo.DriverProviderName -and -not [string]$signedDriverInfo.DriverDate -and $null -eq $signedDriverInfo.IsSigned
        }
        $bindingState = 'UNVERIFIED'
        if ($infPath.status -eq 'READ' -and $infPath.value) { $bindingState = 'BOUND' }
        elseif ($infPath.status -eq 'NOT_SET' -and $service.status -eq 'NOT_SET' -and $signedRowHasNoPackage -and $problemCode.status -eq 'READ' -and [int]$problemCode.value -eq 0) { $bindingState = 'NO_DRIVER_BOUND' }

        $result.query_status = 'READ'
        $result.device = [ordered]@{
            friendly_name = [string]$device.FriendlyName; pnp_status = [string]$device.Status
            is_present = $isPresent; problem_code = $problemCode; binding_state = $bindingState; hardware_ids = $hardwareIds; compatible_ids = $compatibleIds
            service = $service; service_runtime = $systemDriver
            driver_inf_path = $infPath; driver_version = $driverVersion; driver_date = $driverDate; driver_provider = $driverProvider
            signed_driver_package = $signedDriver
        }
    }
    catch {
        if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { $result.query_status = 'NOT_FOUND' }
        else { $result.error_category = $_.CategoryInfo.Category.ToString() }
    }
    return $result
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$osRead = New-ReadResult 'UNAVAILABLE'
try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $osRead = [ordered]@{ status = 'READ'; caption = [string]$os.Caption; version = [string]$os.Version; build_number = [string]$os.BuildNumber; product_type = [int]$os.ProductType; last_boot_up_time = if ($os.LastBootUpTime) { ([datetime]$os.LastBootUpTime).ToUniversalTime().ToString('o') } else { $null }; error_category = $null }
}
catch { $osRead = [ordered]@{ status = 'UNAVAILABLE'; caption = $null; version = $null; build_number = $null; product_type = $null; last_boot_up_time = $null; error_category = $_.CategoryInfo.Category.ToString() } }

$report = [ordered]@{
    schema_version = 1
    captured_at_utc = [DateTime]::UtcNow.ToString('o')
    collector = [ordered]@{ name = 'Get-TestHostState'; version = '1.0'; mutates_system = $false }
    host = [ordered]@{ os = $osRead; sid = [string]$identity.User.Value; elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    secure_boot = Get-SecureBootState
    hvci_vbs = Get-HvciVbsState
    bitlocker = Get-BitLockerProtectionState
    boot_integrity = Get-BootIntegrityState
    aap_device = Get-SelectedAapDevice
}

$json = $report | ConvertTo-Json -Depth 12
if ($OutputPath) {
    $fullPath = [IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Set-Content -LiteralPath $fullPath -Value $json -Encoding UTF8
}
else {
    Write-Output $json
}

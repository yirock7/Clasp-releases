[CmdletBinding()]
param([string]$Compiler = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Tools\MSVC\14.50.35717\bin\Hostx64\x64\cl.exe', [string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
$driverDir = Split-Path $PSScriptRoot
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Clasp-AAP-guards-' + [guid]::NewGuid().ToString('N')) }
[void](New-Item -ItemType Directory -Force -Path $EvidenceDirectory)
$toolRoot = Split-Path (Split-Path (Split-Path (Split-Path $Compiler)))
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$sdkVersion = Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'ucrt\stdio.h') } | Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdkVersion) { throw 'A Windows SDK UCRT is required for the host-only tests.' }
$args = @('/nologo', '/W4', '/TC', '/MT', '/D_CRT_SECURE_NO_WARNINGS', ('/I' + (Join-Path $toolRoot 'include')), ('/I' + (Join-Path $sdkVersion.FullName 'ucrt')),
 (Join-Path $PSScriptRoot 'ioctl_harness.c'), ('/Fo' + (Join-Path $EvidenceDirectory 'ioctl_harness.obj')), ('/Fe' + (Join-Path $EvidenceDirectory 'ioctl_harness.exe')), '/link',
 ('/LIBPATH:' + (Join-Path $toolRoot 'lib\x64')), ('/LIBPATH:' + (Join-Path $sdkRoot ('Lib\' + $sdkVersion.Name + '\ucrt\x64'))), ('/LIBPATH:' + (Join-Path $sdkRoot ('Lib\' + $sdkVersion.Name + '\um\x64'))))
& $Compiler @args 2>&1 | Tee-Object (Join-Path $EvidenceDirectory 'compile.txt')
if ($LASTEXITCODE -ne 0) { throw "Host harness compilation failed: $LASTEXITCODE" }
& (Join-Path $EvidenceDirectory 'ioctl_harness.exe') 2>&1 | Tee-Object (Join-Path $EvidenceDirectory 'results.txt')
if ($LASTEXITCODE -ne 0) { throw "Host IOCTL tests failed: $LASTEXITCODE (evidence: $EvidenceDirectory)" }
$source = [IO.File]::ReadAllText((Join-Path $driverDir 'L2cap.c'))
$functionStart = $source.IndexOf("NTSTATUS`nLpConnect(", [StringComparison]::Ordinal)
if ($functionStart -lt 0) { $source = $source.Replace("`r`n", "`n"); $functionStart = $source.IndexOf("NTSTATUS`nLpConnect(", [StringComparison]::Ordinal) }
$functionEnd = $source.IndexOf("//`n// Close the channel", $functionStart, [StringComparison]::Ordinal)
if ($functionStart -lt 0 -or $functionEnd -lt 0) { throw 'Could not isolate production LpConnect for host compilation.' }
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'l2cap_connect_under_test.h'), $source.Substring($functionStart, $functionEnd - $functionStart))
$disconnectStart = $source.IndexOf("NTSTATUS`nLpDisconnect(", [StringComparison]::Ordinal)
$disconnectEnd = $source.IndexOf("//`n// Write bytes", $disconnectStart, [StringComparison]::Ordinal)
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'l2cap_disconnect_under_test.h'), $source.Substring($disconnectStart, $disconnectEnd - $disconnectStart))
$indicationStart = $source.IndexOf("VOID`nLpIndicationCallback(", [StringComparison]::Ordinal)
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'l2cap_indication_under_test.h'), $source.Substring($indicationStart))
$connectArgs = @('/nologo', '/W4', '/TC', '/MT', '/D_CRT_SECURE_NO_WARNINGS', ('/I' + (Join-Path $toolRoot 'include')), ('/I' + (Join-Path $sdkVersion.FullName 'ucrt')), ('/I' + $EvidenceDirectory),
 (Join-Path $PSScriptRoot 'connect_harness.c'), ('/Fo' + (Join-Path $EvidenceDirectory 'connect_harness.obj')), ('/Fe' + (Join-Path $EvidenceDirectory 'connect_harness.exe')), '/link',
 ('/LIBPATH:' + (Join-Path $toolRoot 'lib\x64')), ('/LIBPATH:' + (Join-Path $sdkRoot ('Lib\' + $sdkVersion.Name + '\ucrt\x64'))), ('/LIBPATH:' + (Join-Path $sdkRoot ('Lib\' + $sdkVersion.Name + '\um\x64'))))
& $Compiler @connectArgs 2>&1 | Tee-Object (Join-Path $EvidenceDirectory 'connect-compile.txt')
if ($LASTEXITCODE -ne 0) { throw "Production connect harness compilation failed: $LASTEXITCODE" }
& (Join-Path $EvidenceDirectory 'connect_harness.exe') 2>&1 | Tee-Object (Join-Path $EvidenceDirectory 'connect-results.txt')
if ($LASTEXITCODE -ne 0) { throw "Production connect guard tests failed: $LASTEXITCODE" }
$deviceSource = [IO.File]::ReadAllText((Join-Path $driverDir 'Device.c')).Replace("`r`n", "`n")
$queryStart = $deviceSource.IndexOf("static NTSTATUS`nLpQueryBoundAddress(", [StringComparison]::Ordinal)
$queryEnd = $deviceSource.IndexOf("NTSTATUS`nLpEvtDevicePrepareHardware(", $queryStart, [StringComparison]::Ordinal)
if ($queryStart -lt 0 -or $queryEnd -lt 0) { throw 'Could not isolate production identity query for host compilation.' }
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'bound_query_under_test.h'), $deviceSource.Substring($queryStart, $queryEnd - $queryStart))
$queryArgs = $connectArgs | ForEach-Object { $_.Replace('connect_harness', 'query_harness') }
& $Compiler @queryArgs 2>&1 | Tee-Object (Join-Path $EvidenceDirectory 'query-compile.txt')
if ($LASTEXITCODE -ne 0) { throw "Production identity query compilation failed: $LASTEXITCODE" }
& (Join-Path $EvidenceDirectory 'query_harness.exe') 2>&1 | Tee-Object (Join-Path $EvidenceDirectory 'query-results.txt')
if ($LASTEXITCODE -ne 0) { throw "Production identity query tests failed: $LASTEXITCODE" }
Write-Output "Offline evidence: $EvidenceDirectory"

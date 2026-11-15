# NTPodsAAP — Windows AAP L2CAP driver

**Clasp D1 candidate (2026-10-15):** this source now builds an AAP-only profile
with authoritative bound-device validation, restricted IOCTLs and a default
SYSTEM/Administrators device ACL. It intentionally rejects legacy ATT/hearing-aid
operations. Use `build-release.ps1` for the Microsoft-signed route (not available yet; Microsoft must sign the package). The historical upstream test-signing
instructions below are retained as upstream context; they are not the Clasp install
procedure. Clasp's submission candidate is unsigned until returned by Microsoft.

Open-source KMDF Bluetooth **profile driver** that lets Windows talk to AirPods
over Apple's Accessory Protocol (AAP). AAP runs on a classic-Bluetooth **L2CAP
channel at PSM `0x1001`**, which Windows user-mode Winsock cannot open (only
RFCOMM is exposed; raw L2CAP `connect()` fails with `WSAENETDOWN`). This driver
opens that channel in kernel mode and bridges it to user space via
`DeviceIoControl`, so a companion app can read battery, toggle ANC, etc.

## How it binds (the key trick)

AirPods advertise an SDP service with UUID `{74ec2172-0bad-4d01-8f77-997b2be0722a}`
(taken from LibrePods on Linux). Windows enumerates a devnode
`BTHENUM\{74ec2172-...}_VID&0001004c_PID&2027` for it. Our INF matches that
hardware ID, so Windows loads this driver as the **function driver for the AAP
service PDO** — with the Bluetooth stack as its parent I/O target. From there we
`BthAllocateBrb` + submit `BRB_L2CA_OPEN_CHANNEL` to open PSM `0x1001` outbound.
No filter driver is needed (unlike PS3/BthPS3), because AirPods advertise the
service. Architecture reference: MS `bthecho` sample + `nefarius/BthPS3`.

## Files

| File | Role |
|------|------|
| `NTPodsAAP.h` | IOCTL contract, device context, prototypes |
| `Driver.c` | `DriverEntry`, device creation, IOCTL queue |
| `Device.c` | PnP: query `BTH_PROFILE_DRIVER_INTERFACE`, get I/O target |
| `L2cap.c` | connect / disconnect / send / receive via BRBs |
| `Ioctl.c` | user-mode bridge (DeviceIoControl → L2CAP) |
| `NTPodsAAP.inf` | binds to the AAP service, installs KMDF service |
| `NTPodsAAP.vcxproj` | KMDF x64 project |

## IOCTL contract (for the user-mode transport)

Device interface GUID `{C0FFEE00-1337-4A5B-9E6F-A1B2C3D4E5F6}`, `FILE_DEVICE 0x8000`:
`CONNECT 0x800 {u64 addr; u16 psm}`, `DISCONNECT 0x801`, `SEND 0x802` (raw bytes),
`RECEIVE 0x803` (raw bytes out), `GET_STATUS 0x804`.

## Build

Needs VS2022/2026 with the C++ workload + Windows SDK/WDK (matching build numbers,
e.g. 28000). From a Developer prompt:

```
msbuild NTPodsAAP.vcxproj /p:Configuration=Release /p:Platform=x64
```

Then generate the catalog from a folder holding `NTPodsAAP.sys` + `.inf`:

```
inf2cat /driver:<pkg-dir> /os:10_X64
```

## Install (test-signed — requires Test Mode)

The driver is not attestation-signed, so Windows must be in test mode. **Advanced
users only; back up your BitLocker recovery key and make a restore point first.**

No compiled driver is committed to this repository. Build it first (see
`build-release.ps1`, which needs Visual Studio and the WDK), then point the legacy
installer at the folder holding `NTPodsAAP.sys`, `.inf` and `.cat` (only `signtool`
from the SDK is needed to sign it):

```powershell
.\install.ps1 -PackageDir C:\path	o\package   # creates a test cert, signs, trusts, installs
```

The old all-in-one installer (drivers, daemon and WinUI app) was removed from
this repository. A signed, supported install path is future work: Windows only loads kernel drivers that Microsoft has signed.

Manually, step by step:

```powershell
# 1. test cert + sign
$c = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Clasp AAP Test" -CertStoreLocation Cert:\LocalMachine\My
signtool sign /fd SHA256 /sha1 $c.Thumbprint NTPodsAAP.sys
signtool sign /fd SHA256 /sha1 $c.Thumbprint ntpodsaap.cat
# 2. trust the cert (Trusted Root + Trusted Publishers, LocalMachine)
# 3. enable test signing, disable Secure Boot in firmware, reboot
bcdedit /set testsigning on
# 4. install (binds to the AirPods AAP devnode)
pnputil /add-driver NTPodsAAP.inf /install
```

Uninstall: `pnputil /delete-driver NTPodsAAP.inf /uninstall`, then
`bcdedit /set testsigning off` and re-enable Secure Boot.

### Development test-driver scripts (`development/`)

`Set-TestDriverTrust.ps1` and `Install-TestDriver.ps1` are for a dedicated test PC only
and do nothing without `-Apply` plus a ShouldProcess approval. Both share
`development/DriverStaging.ps1`, which closes check-then-use races:

- The manifest is read once; the same bytes are hashed against `-ExpectedManifestSha256`
  and parsed. The certificate and package files are hashed against the manifest and copied
  into a fresh snapshot at `%ProgramData%\Clasp\driver-staging\<guid>` (SYSTEM and
  Administrators only, inheritance off, reparse points rejected). The snapshot is re-hashed
  right before the certificate import and before `pnputil /add-driver` (which is given only
  the snapshot INF), then deleted. If `pnputil` publishes a package that is not the
  expected one, exactly that `oemNN.inf` is deleted (no `/force`) and recorded in the receipt.
- Receipts live in `%ProgramData%\Clasp\driver-staging\receipts` (same protection);
  `-ReceiptPath` is a bare file name there. Each run prints the receipt's SHA-256.
  `FinalizeManualBinding`, `ConfirmDeviceRestart` and both `Rollback` actions require that
  value as `-ExpectedReceiptHash` and refuse any receipt whose exact bytes differ, which
  includes a receipt where only the saved device-security SDDL was altered.
- `Get-TestHostState.ps1` and `Install-TestDriver.ps1` have no built-in device. Pass the
  AAP Server node you selected: `-InstanceId` / `-TargetInstanceId` (Device Manager >
  Properties > Details > Device instance path, for example
  `BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_VID&0001004C_PID&2027\9&1a2b3c4d&0&001122334455_C00000000`)
  and `-BluetoothAddress` (12 hex digits, for example `001122334455`; the instance id must
  contain it). An optional `-ExcludedBluetoothAddress` / `-OtherAapAddress` names a nearby
  device that must never be selected.

Offline checks: `Invoke-Pester -Path windows\drivers\aap\development\tests` (Pester 3.4+;
mocks the file reads, ACL calls and privileged steps, touches no stores or drivers).
See `docs/driver-release/THIS-PC-TEST-SETUP.md` for the full command sequence.

## Status

Working and in daily use: the AAP channel opens on hardware and carries battery,
noise control, ear detection, the hi-res mic uplink and the rest. The channel runs
in L2CAP **Basic** mode (ERTM is not serialized to the wire by `bthport` for a
client profile driver — see the comments in `L2cap.c`).

The ATT/GATT channel (PSM `0x001F`, used by the hearing aid) is opened **lazily**,
on the first `LpAttSend`, not on connect — an idle ATT channel gets torn down by the
buds after ~30 s and that teardown stalls the AAP channel on the shared ACL.

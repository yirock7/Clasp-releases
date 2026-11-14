# AAP driver offline guard fixtures

Run from the implementation checkout in PowerShell:

```powershell
& .\windows\drivers\aap\tests\run-offline-tests.ps1
```

The runner accepts `-Compiler` and `-EvidenceDirectory`. Its default compiler is
the installed MSVC 14.50 x64 compiler. It locates an installed Windows SDK UCRT;
it does not install a compiler, load a driver, open a device, or use Bluetooth.
Compiled executables, extracted production functions, and output logs go to a
temporary directory or the explicitly supplied evidence directory.

The IOCTL fixture compiles the real `Ioctl.c` against a small host-only WDF shim.
The L2CAP fixture compiles the production `LpConnect`, `LpDisconnect`, and
`LpIndicationCallback` function bodies extracted unchanged from `L2cap.c`.
The identity fixture compiles the production `LpQueryBoundAddress` body extracted
unchanged from `Device.c`. Allocation, IRP submission, and locks are simulated.

Checks cover caller handle access; malformed sizes, buffers, and timeouts;
unsupported ATT operations; all 65,536 PSM values; invalid and mismatched
addresses before existing-channel changes; identity query failure/invalid
response; close allocation/submission failure and retry; interrupted opens;
current and retired remote-disconnect indications; and unchanged status layout.

2026-10-15 evidence:

- Original IOCTL dispatcher: 25 checks, **19 failures**, captured before driver
  edits at `%LOCALAPPDATA%\Clasp\build-cache\aap-offline-red\results.txt`.
- Hardened dispatcher: **25 checks, zero failures**.
- Hardened L2CAP connection/close/indication functions: **65,563 checks, zero failures**.
- Authoritative identity query: **11 checks, zero failures**.
- Final logs and extracted bodies:
  `%LOCALAPPDATA%\Clasp\build-cache\aap-offline-final\`.

These fixtures do not execute KMDF, Bluetooth stack callbacks, actual access
tokens/device DACLs, driver cancellation, PnP removal, sleep/resume, driver
signatures, Secure Boot, or HVCI. The separate kernel build and package validators
must also pass. Driver Verifier and actual device tests remain required before
runtime readiness claims.

This personal-release driver is AAP-only. Legacy ATT IOCTL numbers and the
32-byte status wire layout remain reserved, but ATT operations return
`STATUS_NOT_SUPPORTED`; ATT flags/counts are zero. Legacy upstream hearing-aid
features are intentionally incompatible with this profile. The restricted
Clasp engine uses only AAP IOCTLs.

API references:

- [BTHENUM authoritative remote-device query](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/bthioctl/ni-bthioctl-ioctl_internal_bthenum_get_devinfo)
- [Dynamic IOCTL access validation](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-iovalidatedeviceiocontrolaccess)
- [KMDF device security](https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/controlling-device-access-in-kmdf-drivers)
- [L2CAP callback parameters and CloseNow](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/bthddi/ns-bthddi-_indication_parameters)

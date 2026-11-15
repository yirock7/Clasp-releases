# DeviceIdentityInventory

A read-only Windows console utility for checking how an explicitly selected paired Bluetooth
Classic Association Endpoint (AEP) relates to Bluetooth LE AEP records and Windows device records.

## Usage

Print the scope and the Windows APIs, selectors, and property keys used:

```powershell
DeviceIdentityInventory.exe
```

List paired Classic AEP candidates. Each output row contains a per-run name hash and the raw
Windows id so the owner can choose the exact record for an inventory run. Listing writes no files:

```powershell
DeviceIdentityInventory.exe --list-classic-paired
```

Run the inventory for one explicitly selected id:

```powershell
DeviceIdentityInventory.exe --classic-aep-id '<id-from-list>'
```

The selected id must still resolve to a paired Bluetooth Classic AEP when the inventory starts.
Unknown, unavailable, or unpaired selections are refused before a results folder is created.

An optional `--out <dir>` selects another results folder. The default is
`%LOCALAPPDATA%\Clasp\build-cache\identity-inventory\<UTC yyyyMMdd-HHmmss>`. The folder must be outside
every Git repository and either new or empty.

## What it records

The tool enumerates `AssociationEndpoint`, `AssociationEndpointContainer`, and `Device` records with
`DeviceInformation.FindAllAsync`. It correlates AEPs, container child ids, and device records by
container id, and checks device-to-AEP links where Windows exposes them. It reports every paired
Bluetooth LE AEP and whether it shares the selected Classic AEP's container.

The JSON and text result files contain per-run keyed hashes for all names, record ids, container
ids, addresses, and other string property values. Boolean status fields, counts, property names, and
the Bluetooth Classic/LE protocol labels remain readable. The random HMAC key exists only in memory
for the run and is not written to disk.

The outcome is one of:

- `CONFIRMED`: at least one Bluetooth LE AEP shares the selected Classic AEP's container. The
  report separately shows whether each matching LE AEP is paired.
- `CANDIDATE`: the target container reports Bluetooth LE as a contributing protocol without an
  enumerated matching LE AEP, or enumeration was incomplete.
- `NONE`: no target or same-container LE evidence was found in completed enumeration.

This tool reads the Windows identity records only. It does not connect to a device or change pairing.

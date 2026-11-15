# GattDiscoveryProbe

A small Windows console tool for a GATT metadata-only feasibility check.

It is deliberately paired-device-bound. Live mode first asks Windows for devices selected by
the paired Bluetooth LE selector, requires an exact `--device-id` match in that returned list,
and resolves only that selected id. The tool never chooses a nearby device.

## Build and pure tests

From the repository root:

```powershell
dotnet build -c Release windows/hub/tools/GattDiscoveryProbe/GattDiscoveryProbe.csproj
dotnet test windows/hub/tools/GattDiscoveryProbe/TestProbe.Tests/TestProbe.Tests.csproj
```

The Bluetooth-free tests exercise argument parsing, service classification, output-folder rules,
hashing, and the production bounded-operation lifecycle helper with controlled task completions.
They do not enumerate Windows devices or call Bluetooth APIs.

## Usage

Show the scope without touching Bluetooth or the file system:

```powershell
.\GattDiscoveryProbe.exe
```

List only Windows' paired Bluetooth LE device records:

```powershell
.\GattDiscoveryProbe.exe --list-paired
```

Listing prints each device's per-run name hash and Windows device id, plus a count. It creates no
files and opens no device connection. The id is printed in the terminal so the owner can choose the
exact paired record for a later live command. The result files from live mode store the name and id
only as per-run keyed hashes.

A live run requires the exact opt-in flag and an id copied from the paired list:

```powershell
.\GattDiscoveryProbe.exe --i-authorize-gatt-metadata-discovery --device-id '<paired-device-id>'
```

An optional `--out <dir>` selects a result folder. The default is
`%LOCALAPPDATA%\Clasp\build-cache\gatt-probe\<UTC yyyyMMdd-HHmmss>`. The folder must be outside
every Git repository and either new or empty.

**The live authorization flag starts a real Windows GATT connection.** This implementation task did
not authorize using that flag. Run it only after a separate owner decision.

## What live mode records

- One `GetGattServicesAsync(Cached)` call, followed by one
  `GetGattServicesAsync(Uncached)` call.
- For each returned service in a mode, one matching-mode characteristics query.
- Service and characteristic UUIDs, attribute handles, characteristic properties, and protection level.
- Per-call cache mode, operation status, `GattCommunicationStatus`, and `ProtocolError`.
- Service outcome categories and any SIG service UUIDs outside the explicit useful-service list.
- Windows version and build, embedded source SHA-256 values, assembly SHA-256, late completion events,
  outstanding operations at exit, and a text summary.

Every asynchronous Windows call has a ten-second **caller wait** bound. A timeout or Ctrl+C means
**the probe stopped waiting; Windows activity may continue**. It does not mean Windows cancelled a
connection or discovery attempt. The operation remains observed. If it later returns a result, the
probe disposes the result and any disposable items it contains exactly once. If it later faults, the
probe observes and records that fault. At shutdown, the probe gives late work up to five seconds for
cleanup and records operation names that are still outstanding.

The probe has no retry path and opens no `GattSession`. It records metadata only; it does not read or
write characteristic or descriptor values. Returned services and characteristics are disposed after
their metadata has been copied, and the Bluetooth device is disposed during cleanup.

The `result.json` and `summary.txt` files go only into the selected result folder. They contain no
raw device name or id. The random HMAC key is kept in memory for that run and is not written to disk.

## Service categories

The category set is based on all services returned across both cache modes:

- `USEFUL_STANDARD`: at least one recognized functional Bluetooth SIG service. The explicit list is
  Battery (180F), Device Information (180A), Heart Rate (180D), Immediate Alert (1802), Link Loss
  (1803), Tx Power (1804), Current Time (1805), Glucose (1808), Health Thermometer (1809), Blood Pressure (1810), Human
  Interface Device (1812), Running Speed and Cadence (1814), Cycling Speed and Cadence (1816),
  Cycling Power (1818), Environmental Sensing (181A), and Fitness Machine (1826).
- `PLUMBING_ONLY`: every returned service is Generic Access (GAP, 1800) and/or Generic Attribute
  (GATT, 1801). If useful or vendor services are also present, this category does not apply.
- `VENDOR_DISCOVERY`: at least one 128-bit UUID outside the Bluetooth SIG base UUID. This is a
  discovery that needs interpretation. It is **not proof** that the service is Apple-owned or that it
  exposes a relevant Apple control capability.

Mixed useful and vendor results report both categories. The vendor category takes precedence for the
exit code. SIG-base services not in the useful list are listed as unclassified SIG UUIDs; they are
not silently treated as vendor or useful services.

## Exit codes and precedence

| Code | Meaning |
| ---: | --- |
| 0 | Scope display or paired-device listing completed |
| 1 | Unexpected error |
| 2 | Refused arguments, platform, or output folder |
| 10 | `VENDOR_DISCOVERY` found |
| 11 | `USEFUL_STANDARD` found and no vendor UUID was found |
| 12 | Access denied, unless a useful or vendor finding already determines the result |
| 13 | Unreachable, timeout, interrupted, incomplete, or empty discovery |
| 14 | No paired LE identity resolved |
| 15 | Complete result contains only GAP and/or GATT plumbing services |
| 16 | Complete result contains SIG-base services, but none matches the useful list or plumbing-only rule |

Positive findings use this precedence: vendor discovery (10), then useful standard (11). Access denied
(12) follows if there is no positive finding. Identity and discovery failures then map to 13 or 14
(and unexpected failures to 1). A fully completed result with only plumbing maps to 15; an otherwise
complete result with only unclassified SIG-base services maps to 16. An empty result maps to 13.

## Review checklist

- [ ] The chosen id appears in `DeviceInformation.FindAllAsync` results from the paired Bluetooth LE selector before it is resolved.
- [ ] No advertisement, RSSI, proximity, or nearest-device logic participates in target selection.
- [ ] The two service queries occur once each in Cached-then-Uncached order; characteristics use the same mode as their service query.
- [ ] Metadata collection does not request attribute values or alter device configuration.
- [ ] Every asynchronous Windows call has a ten-second caller wait bound; timeout/cancellation text does not claim that Windows activity stopped.
- [ ] Late results are disposed and late faults observed; shutdown waits no more than five seconds and reports remaining operations.
- [ ] Every returned service and the device is disposed on all exits; Ctrl+C reaches cleanup.
- [ ] No retries are performed and no GATT session is opened.
- [ ] Result folders inside Git repositories and existing non-empty folders are refused.
- [ ] Result files contain only per-run keyed hashes for device name and id.

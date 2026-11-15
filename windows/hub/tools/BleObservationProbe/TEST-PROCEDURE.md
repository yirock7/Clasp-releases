# BleObservationProbe: test procedure (packets EF-C2, EF-PROBE)

This is a one-minute test. The probe listens to Bluetooth broadcasts while you move your AirPods
Pro 3 through a few positions, and saves what it hears to a folder on your PC.

## What the probe does and does not do

- It only **listens**. Windows listens passively for Bluetooth LE broadcasts that carry Apple data
  (company id 0x004C). It sends no scan requests.
- It **never** pairs, connects, opens GATT services, or writes anything to any device. It does not
  change any Bluetooth, pairing or audio setting.
- It stops by itself after the number of seconds you give (at most 120), or when you press Ctrl+C.
- Without the authorisation flag it only prints its scope and exits. It does not touch Bluetooth
  and does not create any folder.
- It decodes the "proximity pairing" broadcast (type 0x07) using a layout taken from the LibrePods
  project (commit `4589176f7b7943a6091d5c6907c74f441bcf7780`, `linux/ble/blemanager.cpp`,
  GPL-3.0). That layout has **not** been checked on AirPods Pro 3. LibrePods lists no Pro 3 model
  id, so Pro 3 is expected to show up as "Unrecognised AirPods-family model (id 0x....)".
  Other Apple message types are counted, not decoded.

## Before you start

1. Bluetooth is on in Windows.
2. The AirPods are charged and in their case, lid **closed**, on the desk next to the PC.
3. You do **not** need to pair, unpair or connect anything. Leave Bluetooth settings as they are.
4. Other Apple devices nearby will be heard too: your iPhone, an Apple Watch, an iPad or Mac, and
   anyone else's AirPods. If you can, put other Apple devices in another room. If your phone stays
   nearby, write that down.
5. Have a clock with seconds visible (the probe prints seconds as it runs), and something to write
   the times on.
6. The probe must already be built (see "Build notes" at the end; the developer normally does this).

## The command

Open PowerShell and run (one line):

```powershell
& "<repo>\windows\hub\tools\BleObservationProbe\bin\Release\net10.0-windows10.0.19041.0\BleObservationProbe.exe" --i-authorize-a-passive-scan --seconds 60
```

Running that command is your authorisation for one passive 60-second listen. The first line it
prints is the results folder, for example
`Results folder: C:\Users\you\AppData\Local\Clasp\build-cache\ble-probe\20261015-091500`.

Optional: `--out <folder>` saves to a folder you choose instead. It must be new or empty and must
not be inside the repository.

## What to do during the 60 seconds

Start a timer when the probe prints `Passive listening for 60 s`. Write down the actual time
(seconds from start) of each change; it is fine if you are a few seconds off.

| Time    | What to do                                              | Actual time |
|---------|---------------------------------------------------------|-------------|
| 0-10 s  | Leave the case closed on the desk.                      |             |
| 10-20 s | Open the lid. Leave both buds in the case.              |             |
| 20-35 s | Take **one** bud out (note which: left or right) and hold it in your hand. | |
| 35-45 s | Take the other bud out too and put **both in your ears**. |           |
| 45-55 s | Put both buds back in the case. Keep the lid **open**.  |             |
| 55-60 s | Close the lid.                                          |             |

Also note: was your phone nearby? Was music playing or the AirPods connected to anything?

## Stopping early

Press **Ctrl+C** once. The probe stops listening, saves everything it has heard so far, prints
"Stopped by user" and the results folder, and exits normally. Closing the window also stops it,
but Ctrl+C is cleaner.

## What you will see at the end

- `Finished after 60 s.` (or `Stopped by user.`), the number of sightings, and the results folder.
- If Bluetooth is off, missing, or Windows blocks access, it prints `Bluetooth unavailable:` and
  the reason, still writes a summary that records this, and exits with code 3. Turn Bluetooth on
  (or allow access in Windows Settings > Privacy & security) and run the command again.
- Exit codes: 0 finished or stopped by you; 1 unexpected error; 2 refused (bad arguments, a
  misspelled flag, or a results folder that already has files; nothing was started); 3 Bluetooth
  unavailable. The flag must be spelled exactly `--i-authorize-a-passive-scan` (lower case, "z").

## The results folder

`%LOCALAPPDATA%\Clasp\build-cache\ble-probe\<UTC date-time>\` contains:

- `summary.txt`: the readable report. It has the run details, how the run ended, a count per model
  id, how many different addresses appeared in each 5-second slice (AirPods change their address,
  and each bud or the case may broadcast separately), a short state timeline per address, status
  and battery histograms, and every other Apple message type seen.
- `summary.json`: the same summary in machine-readable form.
- `sightings.jsonl`: one line per broadcast heard, with the raw data.

**Please send back `summary.txt`** and your table of actual times. Send `sightings.jsonl` only if
you choose to; it allows a deeper analysis.

**Privacy.** Bluetooth addresses are never saved: each address is replaced by a tag such as
`addr#1a2b3c4d` made with a random key that exists only while the probe runs, so tags cannot be
matched across runs. Device names are never saved; the files only say whether a name was present.
The raw broadcast bytes **are** saved because they are needed for analysis. Part of each AirPods
broadcast is an encrypted block, and broadcasts from other people's devices nearby may be in the
file too, so **do not publish the results folder** (no public issue, gist or repository).

## What the results can and cannot show

They can show:

- which model id the AirPods broadcast, how often, and how many addresses they use;
- whether the decoded battery, in-case, in-ear and lid values move in step with what you did, which
  supports (or contradicts) the LibrePods layout for this device and firmware.

They cannot show:

- **who** sent a broadcast. Every sighting is unconfirmed: any nearby Apple device can appear, and
  a broadcast can only be tied to your AirPods with keys that come from the AAP connection (which
  needs the unsigned driver). The summary says this at the top.
- that any **control** works. Seeing broadcasts says nothing about whether listening mode,
  Conversation Awareness or Adaptive strength can be read or changed.

## Build notes (for the developer)

1. Build: `dotnet build windows/hub/tools/BleObservationProbe/BleObservationProbe.csproj -c Release`
   (from the repository root). The exe is
   `windows\hub\tools\BleObservationProbe\bin\Release\net10.0-windows10.0.19041.0\BleObservationProbe.exe`.
2. Radio-free tests of the summary, tagging, argument and folder helpers:
   `dotnet test windows/hub/tools/BleObservationProbe/Tests/BleObservationProbe.Tests.csproj`.
3. Check the scope text without touching Bluetooth: run the exe with no arguments; it must exit 0
   and create no folder.
4. The probe compiles in the app's own listener
   (`src/AppleDeviceHub.App/Ble/WinRtAdvertisementSource.cs`) unchanged, so a rebuild picks up any
   change to it. The probe uses only its `StartAsync`, `StopAsync`, `Dispose`, `Received` and
   `StatusChanged` members.

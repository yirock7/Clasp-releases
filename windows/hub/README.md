# Clasp desktop app

This folder holds Clasp's Windows tray app (`src/AppleDeviceHub.App`), the plain .NET library with its rules and logic (`src/AppleDeviceHub.Core`), the Core tests, the per-user installer, and a few developer tools.

For what Clasp does and how it handles your data, read the [main README](../../README.md). For building and testing, see [docs/BUILDING.md](../../docs/BUILDING.md).

## How it works, briefly

- **Pairing uses Windows.** While the welcome guide's pairing page is open, Clasp runs the same search for unpaired devices that Windows Settings does and lists AirPods and Beats in pairing mode. **Pair** asks Windows to pair that one device with the "just works" ceremony; anything that asks for a code is sent to Windows Settings instead. Nothing pairs without a click. You can also pair in Windows Settings. In Clasp you pick one of the paired devices and use **Connect**, which asks Windows to reconnect it. Clasp calls the device connected only after Windows exposes an active audio endpoint. Choosing a device does not change your sound output.
- **Nearby detection** reads the approximate battery broadcasts that Apple devices send over Bluetooth Low Energy. It is passive (it never pairs or connects) and can be turned off in Settings. The source is not matched to your paired device, so another pair nearby can supply the estimate, which is why the app calls it an estimate.
- **Automatic reconnect** is off by default. If you turn it on, Clasp makes one reconnect attempt for the chosen device when it notices a case opening nearby. It never starts or resumes your music.
- **The tray icon** draws the silhouette of the device you selected in Windows (never one picked from Bluetooth broadcasts, which can be faked), dimmed when it is not connected. Its colour shows the battery: amber when low, red at your low-battery level, green while charging. There is no number on the icon; hover for the exact percentage, or click for full details.
- **Demo mode** (`--transport=demo`) uses made-up data and is labelled "Demo" on screen.
- **No driver.** Listening modes, Conversation Awareness, true ear detection and similar controls need Apple's private control channel, which needs a Microsoft-signed driver. The current app does not offer them.

## Attribution and licence

Derived from **NTPods** by arctumn (https://github.com/arctumn/ntpods), GPL-3.0, which began as the Windows port of LibrePods. New code here is GPL-3.0-or-later. The hub includes NTPods' wire-DTO file unchanged as `Core/Interop/Upstream/NtpodsMessages.cs`. The artwork is original, and no upstream product images are used.

Not affiliated with or endorsed by Apple. "AirPods", "iPhone", and "Apple Watch" are Apple trademarks, used only to describe compatibility.

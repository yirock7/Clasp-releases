# Changelog

What changed in each version of Clasp, written for people who use it. Each version's full release (with the installer) is on the [releases page](https://github.com/yirock7/Clasp-releases/releases).

## Unreleased

### New

- **A new app icon.** It shows the charging case from Clasp's own artwork, lid open with the earbuds peeking out, on dark frosted glass with a coloured edge. At the smallest sizes (taskbar and title bar) it shows the closed case with its green light, so it stays readable.
- **A coloured glass edge.** Clasp's windows now have a soft coloured glass edge, matching the new icon. It's off in High Contrast and when transparency effects are off.

## 0.5.0

A tidy-up release: the program is renamed Clasp.exe, with a safer log and a notice after a crash.

### Changed

- **The program is now called Clasp.exe, and its data folder is `%LOCALAPPDATA%\Clasp`.** Both used to carry an older internal name. The first time the new version starts it moves your settings, battery history and log across from the old `AppleDeviceHub` folder. If something in the old folder is still in use (for example the installer of the update that just ran), it copies your files instead and removes the old folder on a later start. Your settings are never deleted before they are safely in the new folder. Your "Start Clasp when I sign in" setting and the `clasp:` link setting carry over, and Setup and the app repair those entries after the rename. If you pinned the old `AppleDeviceHub.exe` to the taskbar, pin Clasp again from the Start menu.

### New

- **If Clasp crashes, it now says so the next time it starts.** A notification tells you that Clasp closed unexpectedly and where `hub.log` is. The log records what kind of error it was, but not its message.
- **The project is ready for visitors.** The README says Clasp is a preview, there is a hardware report form for telling me how your AirPods or Beats behave, and `docs/TOOLCHAIN.md` lists what building the installer needs.

### Fixed

- **`hub.log` no longer contains device names (such as your sound output) or error messages.** It never contained Bluetooth addresses; errors are now recorded by type only.
- **Starting Clasp.exe directly no longer shows made-up Demo battery numbers.** Demo mode now needs `--transport=demo`. The Start menu, desktop and sign-in entries are unchanged.

### Behind the scenes

- The app's on-screen text now lives in resource files, which prepares the way for translations. Nothing looks different.

## 0.4.2

Finishes a fix from 0.4.1.

### Fixed

- **The "Pair in Windows Settings instead" button in the welcome guide no longer looks cut off** when you point at it. The guide's page was trimming the edge of buttons that grow slightly under the pointer; it now leaves room for them and for keyboard focus outlines.

## 0.4.1

Fixes for three things you could see in 0.4.0.

### Fixed

- **AirPods no longer jump back into the case while you're wearing them.** The tray flyout's picture switched from AirPods out of the case to AirPods in it about 15 seconds after it opened, even though nothing had changed. Clasp now keeps the in-ear picture for as long as your AirPods keep reporting that they're in your ears.
- **Battery history is readable when you only use your AirPods with this PC now and then.** Long stretches with no readings shrink to a narrow break mark, so each time you used them gets room to read, labelled with when it started. A single charging reading no longer shows as a thin full-height green bar. Clasp still never fills in levels it didn't see.
- **The "Pair in Windows Settings instead" link in the welcome guide no longer looks cut off** when you point at it.

## 0.4.0

Easier first-time setup.

### New

- **Pair from the welcome guide.** While the pairing page is open, Clasp lists AirPods and Beats that are in pairing mode, each with a **Pair** button. Nothing pairs until you click. After pairing, Clasp selects your AirPods and connects them if Windows hasn't already. Headphones that ask for a code still pair through Windows Settings, and the link to it is still there.
- **Quick settings in the welcome guide.** A new last page sets your low-battery alert level and what happens when your AirPods connect: connect automatically when nearby, switch sound to them, play music when they connect, pause when they disconnect. The defaults haven't changed, and all of these are still in Settings.

## 0.3.0

The first release with the source code public, plus a new tray icon and some security hardening.

### New

- **A tray icon that shows your device.** The tray icon now draws the silhouette of your connected device (AirPods Pro, AirPods Max, Powerbeats and so on), or the last device you used, dimmed, when it is not connected. Its colour shows the battery: amber when low, red at your low-battery level, green while charging. There is no number on the icon; hover for the exact percentage, or click for full details. The Clasp logo only appears before you have picked a device.
- **Clasp is open source.** The source code is now public in this repository under the GPL-3.0, so anyone can read how it works. The projects Clasp builds on are credited in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- **A way to check a manual download.** If you install Clasp by hand, there is now a small script that checks the installer's signature before you run it. The steps are in the [README](README.md#download-and-install).

### Safer

None of these are known to have affected anyone. They close gaps that a careful reader of the code could spot.

- **Checking a download is stronger.** Before, the only check for a manual install was a checksum that sits next to the installer. Now there is a signature check that proves the installer is the one I published.
- **Clasp copes better with a crowd of Bluetooth signals.** Anyone nearby can send lots of fake Apple-style broadcasts. Clasp now limits how many it looks at per second, so a flood cannot slow your PC or the app down.
- **Fake battery readings can no longer set off low-battery alerts.** A nearby device pretending to be your AirPods cannot trick Clasp into showing a low-battery notice, and low-battery notices now have a cooldown so they cannot repeat in quick succession. Clasp now also needs stronger evidence that a broadcast is really yours (it must have seen your AirPods connect or disconnect while it was running), so you may get fewer alerts than before, and each alert is followed by a 2-hour quiet period.
- **A smoother interface under load.** When many updates arrive in quick succession, Clasp now shows only the latest one instead of queuing them all.

### Changed

- The demo mode and the screenshots now show the battery in steps of 10%, which is what most people will actually see.
- The documentation was reorganised and rewritten to be easier to read.

### Credits

Thanks to everyone whose work Clasp builds on: arctumn (NTPods), the LibrePods contributors, and the CAPod and MagicPodsCore projects.

## Earlier versions

Versions before 0.3.0 are described in the release notes on the [releases page](https://github.com/yirock7/Clasp-releases/releases).

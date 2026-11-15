# Pairing modes

How to put each model in `AppleModelCatalog` into pairing mode so Windows can pair it. The in-app wording lives in `windows/hub/src/AppleDeviceHub.Core/Ble/PairingInstructions.cs`; this file records where each step came from. Researched 2026-11-07. Only Apple Support and Beats Support pages were used. Apple's pages are the same guides across regions, and a few Beats pages redirect to Apple.

## Sources

| Short name | URL |
|---|---|
| A1 | https://support.apple.com/guide/airpods/pair-airpods-with-a-non-apple-device-dev499c9718b/web |
| B1 | https://support.apple.com/guide/beats/pair-beats-dev46c519179/web |
| B2 | https://www.beatsbydre.com/support/earbuds/studio-buds |
| B3 | https://www.beatsbydre.com/support/earbuds/studio-buds-plus |
| B4 | https://support.apple.com/en-us/102326 (Solo3 Wireless) |
| B5 | https://support.apple.com/en-us/102304 (Powerbeats Pro 2 / Powerbeats Fit; the page is labelled for the original Powerbeats Pro in search results but its content covers the newer models) |

## Steps that have a source

| Group (catalog models) | Steps | Source |
|---|---|---|
| AirPods 1st, 2nd, 3rd generation; AirPods Pro; AirPods Pro 2 (Lightning and USB-C) | Open the case lid with the AirPods inside. Press and hold the setup button on the back of the case for about 5 seconds, until the status light flashes white. | A1 |
| AirPods 4, AirPods 4 (ANC), AirPods 5 (both ids), AirPods Pro 3 | Open the case with the AirPods inside. Double-tap the front of the case until the status light flashes white. | A1 |
| AirPods Max (Lightning, USB-C), AirPods Max 2 | Press and hold the listening mode (noise control) button for about 5 seconds, until the status light flashes white. Apple's page names only "AirPods Max"; Max 2 is assumed to be the same. | A1 |
| Beats Fit Pro, Beats Studio Buds +, Powerbeats Fit, Powerbeats Pro 2 | Earbuds in the case, lid open, press and hold the system button until the light flashes white. Studio Buds + on Beats' own page: the button inside the case, the LED on the front blinks white. | B1, B3, B5 |
| Beats Studio Buds (original) | Earbuds in the case, lid open, press and hold the small round button until the LED flashes. Beats' page also says "system button on the case" in its Mac steps; the round button is what it gives for other devices. | B2 |
| Beats Solo Buds | Earbuds in the case, lid open, press and hold the button above the "b" logo on either earbud until a tone repeats. | B1 |
| Beats Flex | Press and hold the power button on the right control module until the light flashes. | B1 |
| Beats Solo 4 | Press and hold the power button on the right earcup. | B1 |
| Beats Studio Pro | Press and hold the system button on the right earcup. | B1 |
| Beats 360 | Press and hold the power button on the back of the left earcup. | B1 |
| Beats Solo 3 | Press and hold the power button for 5 seconds. The Fuel Gauge lights flash while the headphones are discoverable. | B4 |

Apple gives no hold time for the Beats rows except where noted, so the app says "until the light flashes" and nothing more.

## Models with no confirmed source (shown as "Check your model's guide")

These are in the catalog but none of the fetched primary pages cover them.

- Powerbeats 3 (0x0320), Powerbeats 4 (0x0D20), Powerbeats Pro (0x0B20), BeatsX (0x0520), Beats Solo Pro (0x0C20), Beats Studio 3 (0x0920).

Search results hinted at steps for some of these (Powerbeats3 and Powerbeats: hold the power button on the earbud for about 5 seconds until the light flashes; Powerbeats Pro: hold the system button on the case), but the pages themselves did not confirm it when fetched (the fetches returned the current Beats guide instead). They are deliberately not in the app. Apple pages worth checking by hand:

- https://support.apple.com/en-gb/102150 (Powerbeats3 Wireless)
- https://support.apple.com/en-my/101665 (Powerbeats)
- https://support.apple.com/en-us/102196 (BeatsX)
- https://support.apple.com/en-us/HT210651 (Solo Pro)
- https://www.beatsbydre.com/support/earbuds/powerbeats-pro

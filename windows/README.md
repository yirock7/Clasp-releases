# Windows sources

This folder holds everything Clasp ships or may ship on Windows. The shipped product is
**`hub/`** only; the rest is optional or future work.

| Folder | What it is | Status |
|---|---|---|
| [`hub`](hub) | The Clasp app (WinUI 3), its Core library, tests, the per-user installer and tools. | Shipped. Start with [`hub/README.md`](hub/README.md) and the repository [README](../README.md). |
| [`daemon`](daemon) | `clasp-engine`, the restricted Rust engine the app can talk to over a versioned, identity-checked pipe. | Optional hardware engine; not started by the app. See [`protocol/clasp-engine-v1`](protocol/clasp-engine-v1). |
| [`drivers/aap`](drivers/aap) | Clasp's optional AAP L2CAP kernel driver source. | Source only. It must be built from source, is optional, and is not needed for normal use of Clasp. Future driver path. |
| [`protocol`](protocol) | The `clasp-engine` wire protocol and fixtures shared by the engine and the app. | In use. |
| [`docs`](docs) | AAP protocol research notes. | Reference. |

## Removed legacy code

The retained legacy stack was removed after a security review: the legacy
daemon (fixed named pipes open to authenticated users), its IPC
crate, the legacy WinUI client, the ZIP/MSI driver installer with its helper scripts and
bundled `devcon.exe`, and the two virtual-microphone drivers. Clasp does not install
drivers. A signed, supported driver install path is future work: Windows only loads kernel drivers that Microsoft has signed.

See [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) for the projects Clasp builds on.

## Building the engine (optional)

```powershell
cd windows\daemon
cargo build --release --bin clasp-engine
cargo test
```

The engine needs no FFmpeg, SQLite or WinRT. It is never started by the Clasp app.

# Documentation

Start with the [main README](../README.md) if you just want to use Clasp.

## For users

- [Installing and updating](INSTALLING-AND-UPDATING.md): checking the download, installing, how updates work, uninstalling.
- [Pairing modes](PAIRING-MODES.md): how to put each AirPods and Beats model into pairing mode, and where each step came from.

## For people who want to build or change Clasp

- [Building](BUILDING.md): build, test, run in Demo mode, build the installer.
- [Architecture](ARCHITECTURE.md): how the app is put together.
- [Contributing](../CONTRIBUTING.md) and the [Code of Conduct](../CODE_OF_CONDUCT.md).
- [Releasing an update](RELEASING-UPDATES.md): how releases are built, signed and published (mostly of interest to the maintainer).
- [Release notes](release-notes/): the text of each GitHub release. The user-friendly summary of all versions is [CHANGELOG.md](../CHANGELOG.md).

## Protocol reference

Notes on Apple's AirPods control protocol (AAP, sometimes called AACP). They come from the [LibrePods](https://github.com/kavishdevar/librepods) project and are kept here for reference. The current Clasp app does not use this control channel; it only reads Bluetooth broadcasts (see "What it can't do yet" in the README).

- [AAP Definitions](AAP%20Definitions.md)
- [Opcodes](opcodes.md)
- [Control commands](control_commands.md)
- [Device information](device-info.md)

## Other

- [Third-party notices](../THIRD-PARTY-NOTICES.md): the projects and libraries Clasp builds on, and their licences.
- [Security policy](../SECURITY.md)
- More protocol research notes for the optional driver live in [`windows/docs`](../windows/docs/README.md).

# Windows docs

Protocol research notes that remain useful for the kept AAP driver source and the
`clasp-engine`.

- **[`heart-rate.md`](heart-rate.md)** — heart-rate monitoring research: why it needed the
  AAP channel's MTU raised to 1691, what is sent and decoded, and what was ruled out on the
  way. Written for the removed legacy daemon; kept because it explains the driver's MTU.
- **[`aap-packet-discovery.md`](aap-packet-discovery.md)** — how to capture ground-truth AAP
  traffic on macOS/iOS to confirm or discover packets (`aap_extract.py` is its helper).

The legacy daemon IPC plan and the hi-res microphone plan were removed together with the
code they described. The overview of what is in `windows/` is in the
[Windows README](../README.md).

## Protocol (OS-agnostic)
AAP/AACP notes apply to every platform:
**[`AAP Definitions.md`](../../docs/AAP%20Definitions.md)**,
**[`docs/control_commands.md`](../../docs/control_commands.md)**,
**[`docs/opcodes.md`](../../docs/opcodes.md)**,
**[`docs/device-info.md`](../../docs/device-info.md)**.

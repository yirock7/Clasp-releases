# clasp-engine protocol, version 1.1 (1.0 plus the additive section 9)

The local protocol between Clasp (the C# app, `windows/hub`) and the restricted Rust engine (`clasp-engine`, built from `windows/daemon`). It replaces the legacy daemon pipes (the event and command pipes and the raw L2CAP proxy pipes), which belonged to the removed legacy daemon. `clasp-engine` never creates them.

Both implementations must pass the fixtures in `fixtures/`. A change to anything here is a contract change: bump `minor` for additive changes and `major` for breaking ones, and update both sides and the fixtures together.

## 1. Endpoint

| Item | Rule |
|---|---|
| Name | local named pipe `Clasp.Engine.v1.<SID>`, where `<SID>` is the string SID of the user running the engine (for example `S-1-5-21-...`). The full path is the usual local pipe prefix plus that name. |
| Access | Security descriptor `D:P(A;;GA;;;<SID>)`: protected DACL, generic-all for that user only. No Authenticated Users, no Everyone, no SYSTEM entry. |
| Remote | `PIPE_REJECT_REMOTE_CLIENTS`. |
| Instances | Exactly one pipe instance (`nMaxInstances = 1`), created once with `FILE_FLAG_FIRST_PIPE_INSTANCE`. If creation fails because the name exists, the engine exits with a clear error (another engine, or a squatter, owns the name). The engine serves one client at a time and reuses the same instance (`DisconnectNamedPipe`, then wait again). |
| Client check by the engine | `GetNamedPipeClientProcessId`; the client image path is logged. With `--allowed-client <full path>`, any other client image is refused (connection closed before `hello_ack`). |
| Engine check by the client | `GetNamedPipeServerProcessId`, then the full image path of that process must equal the configured engine path (case-insensitive, full path). When an expected SHA-256 is configured, the image file's hash must match too. If either check fails, the client closes the connection without sending anything (not even `hello`). |

## 2. Framing

Each message is one frame: a 4-byte little-endian unsigned length `N`, then `N` bytes of UTF-8 JSON (one JSON object).

- Valid `N` is 1 to 65536 inclusive. `N = 0` or `N > 65536`: the receiver sends `error` (`oversized` or `malformed`) if it can, then closes the connection. It never allocates `N` bytes before checking.
- End of stream inside a frame (truncated header or body): close the connection; no partial message is ever processed.
- Invalid UTF-8, invalid JSON, a non-object, a missing or unknown `type`, or a field of the wrong type: `error` (`malformed` or `unknown_type`), then close. There is no fallback to any other protocol.
- Readers and writers handle partial reads and writes (loop until the full frame is transferred) and stay responsive to shutdown.
- Unknown extra fields in a known message are ignored (this is how `minor` versions stay compatible). A `minor` version may add optional fields freely. It may add a message type only behind negotiation, because an unknown `type` closes the connection: a client sends a new command only when `hello_ack.capabilities` lists it, and the engine sends a new event type only to a client whose `hello.minor` is at least the minor that introduced it (section 9).

## 3. Handshake

1. The first client frame must be `hello`, within 5 seconds of connecting. Anything else, or nothing, closes the connection.
2. If `major` differs from the engine's major, the engine replies `error` with code `incompatible_version` and closes. A different `minor` is accepted; each side ignores what it does not know.
3. Otherwise the engine replies `hello_ack`. Nothing else is sent before it, and the client sends nothing but `hello` before receiving it.

`engine_epoch` is a random UUID generated when the engine process starts. A client that reconnects and sees a different epoch must treat all earlier sessions, sequences and observations as gone.

## 4. Messages

All identifiers are strings. `request_id` is a client-generated UUID. Every engine-to-client message carries `seq`: an integer that starts at 1 for each engine epoch and is strictly increasing across all engine messages of that epoch (gaps allowed, for example across client reconnects). A client discards any message whose `seq` is not greater than the last one it accepted.

### Client to engine

| type | fields | meaning |
|---|---|---|
| `hello` | `protocol` = `"clasp-engine"`, `major`, `minor`, `client` | Start of every connection. |
| `list_devices` | `request_id` | Paired devices that publish the AAP service, with driver-binding status. Read-only. |
| `open_session` | `request_id`, `device_id`, `mode` = `"observe"` or `"control"` | Open the AAP control session with that device. `observe` performs only the protocol setup writes. `control` additionally permits `set_listening_mode`. |
| `close_session` | `request_id`, `session_id` | Close the AAP control session. Windows audio is not touched. |
| `set_listening_mode` | `request_id`, `session_id`, `mode` = `"off"`, `"noise_cancellation"`, `"transparency"` or `"adaptive"` | One listening-mode write. Allowed only in a `control` session, for a mode listed in that session's `writable_modes`. |
| `cancel` | `request_id`, `target_request_id` | Cancel an outstanding `open_session`. Writes cannot be cancelled (the reply is `rejected`). |

### Engine to client

| type | fields | meaning |
|---|---|---|
| `hello_ack` | `seq`, `protocol`, `major`, `minor`, `engine` (name and version), `engine_epoch`, `capabilities` (array) | Handshake accepted. Version 1.0 capabilities: `list_devices`, `observe`, `listening_mode_write`. |
| `devices` | `seq`, `request_id`, `devices` (array of device entries) | Reply to `list_devices`. |
| `result` | `seq`, `request_id`, `outcome`, `detail` (string or null), `session_id` (present only on `open_session` results: the new id when accepted, `null` when failed, cancelled or timed out; absent on all other results) | The final answer to one request. |
| `session` | `seq`, `session_id`, `device_id`, `state`, `reason` (string or null), `writable_modes` (array, may be empty) | Session lifecycle: `opening`, `open`, `closed` (by request), `lost` (link failure, driver error, device gone). |
| `observation` | `seq`, `session_id`, `device_id`, `observed_at`, `fields` (object) | Values decoded from ONE inbound AAP packet. |
| `error` | `seq`, `code`, `detail`, `request_id` (string or null) | Protocol-level error. Codes: `incompatible_version`, `malformed`, `oversized`, `unknown_type`, `not_authenticated`, `busy`, `internal`. An `error` with `request_id` null ends the connection. `busy` is the one per-request error: a second `open_session` while a session is opening, open or still draining gets `error` `busy` with that `request_id` and **no** `result`; the connection stays up. |

**`outcome` values:** `accepted`, `rejected` (invalid for this state or arguments, including a wrong or stale `session_id`), `failed` (the driver or device operation failed; `detail` says which), `unsupported` (not available in this session, mode or engine), `timed_out`, `cancelled`.

**Device entry:** `device_id` (opaque, see section 6), `name` (the Windows pairing name), `aap_service` (bool), `driver_interface` (`bound`, `not_found`, `ambiguous`, `unparsed`), `windows_connected` (bool or null when unknown). No Bluetooth address, serial number or other persistent hardware identifier is ever sent.

## 5. Observations: the rules that make readback honest

- An `observation` is emitted only for a validated inbound AAP packet from the device of the open session. It contains **only the fields that packet carried**. A field that is absent from `fields` was NOT observed; the client keeps its previous value with its own previous time.
- `observed_at` is the engine's UTC receive time of that packet (ISO 8601 with milliseconds and `Z`). It is never refreshed for fields the packet did not carry, never set when a message is merely re-sent, and never set by a command.
- **Commands never produce observations.** `set_listening_mode` returns `result` `accepted` when the write reached the driver. That is NOT confirmation. Only a later `observation` whose `listening_mode` equals the requested mode confirms it (and only within the same `session_id`).
- Field encodings in `fields`:
  - `battery.left`, `battery.right`, `battery.case`: object `{ "level": <0..100 or null>, "charging": <true, false or null> }`. `level: null` means the device reported the component as absent or unavailable (status 0x04, level 0xFF, or a level above 100). `charging` is `true` for status 0x01 or 0x05, `false` only for status 0x02, and `null` (unknown) for any other status; it is never inferred as false.
  - `listening_mode`: `"off"`, `"noise_cancellation"`, `"transparency"`, `"adaptive"`, or `"unknown"` for a value the engine does not recognise.
  - Version 1.0 defines no other fields. Conversation Awareness, Adaptive strength, ear detection, heart rate, hearing features and microphone are not reported or writable.
- On `session` `closed` or `lost`, the client must treat every value from that session as no longer live (it may keep showing it as stale) and retire every pending request tied to it.

## 6. Device identity

- `device_id` is `"dev-"` followed by the first 16 hex characters (lowercase) of SHA-256 over a 32-byte local salt followed by the 6 address bytes (big-endian). The salt is created randomly on first use and stored in the engine's local data folder. The mapping never leaves the machine.
- `open_session` must resolve `device_id` to exactly one paired device AND exactly one AAP driver interface whose interface path names that device's address. No interface, several interfaces, or an unparsable path: `result` `failed` with `detail` naming the reason; nothing is opened. Never fall back to the first interface, a name match, the strongest advertisement or a nearby unpaired device.
- The driver connect call uses only the address verified this way.
- **The engine never creates a Bluetooth link.** Immediately before connecting, a fresh read-only inventory must report the device as connected to Windows (`windows_connected` true). Otherwise `open_session` fails with a `detail` starting `not_connected:` and nothing is opened, because opening the AAP channel to a disconnected device could page it and pull it from another host (for example the iPhone).

## 7. What the engine never does

No automatic connection or reconnection, no Windows audio connect/disconnect or A2DP/HFP toggling, no volume or media control, no microphone (no FFmpeg, no mic pipe, no rename), no heart-rate or hearing-aid startup, no scheduled-task or devnode recovery, no device renaming, no raw AAP passthrough, no global shutdown command. The only AAP writes are the three setup packets (handshake, set-features, request-notifications) on `open_session`, and in a `control` session one listening-mode command per `set_listening_mode`.

## 8. Fixtures

`fixtures/messages/*.json`: valid messages. Each file holds `{ "direction", "description", "message" }`. Both sides must parse `message` and, for their own outgoing direction, produce JSON that parses back to an equal object.

`fixtures/frames/*.json`: framing cases. Each file holds `{ "description", "bytes_hex", "expect" }` with `expect` one of `ok`, `reject_oversized`, `reject_zero_length`, `reject_truncated`, `reject_malformed`, `reject_unknown_type`.

`fixtures/aap/*.json` (engine only): `{ "description", "packet_hex", "expect_fields" }`. `expect_fields` is the exact `fields` object the engine must emit for that packet, or `null` when the packet must produce no observation.

`fixtures/sequences/*.json` (client only): ordered engine messages and the state the client must end in.

## 9. Version 1.1 additions (additive; 1.0 peers keep working)

`hello_ack.minor` is the negotiated minor: the smaller of the client's `hello.minor` and the engine's (1). A 1.1 engine answering a 1.0 client reports `minor: 0` with only the 1.0 capabilities, sends none of the section 9 fields, and treats `set_conversation_awareness` as an unknown type (`error unknown_type`, then close), exactly as a 1.0 engine would.

### 9.1 New observation fields (optional fields inside `observation.fields`)

Evidence: the repository's pinned protocol notes (`AAP Definitions.md`, written for AirPods **Pro 2** firmware 7A305, plus the upstream iOS-capture section "Ear detection: two values beyond the documented three"). None of it is verified on AirPods Pro 3; the client must label these values accordingly until a real session confirms them.

| Field | Source packet | Values |
|---|---|---|
| `conversation_awareness` | control `09 00 28 [v] ...` (the same shape as the listening-mode report) | `"on"` for `0x01`, `"off"` for `0x02`, `"unknown"` for anything else |
| `ear` | ear detection `06 00 [primary] [secondary]` | object `{ "primary": <state>, "secondary": <state> }`, state one of `"in_ear"` (`0x00`), `"out_of_ear"` (`0x01`), `"in_case"` (`0x02`), `"disconnected"` (`0x03`), `"unknown"` (any other value except `0x04`) |

- `0x04` is a transitional value seen only while a bud moves between resting states. A packet in which either byte is `0x04` produces **no** `ear` field (the client keeps its previous value and time). The engine never invents a resting state for it.
- "primary" and "secondary" are the AirPods' own roles, which change; the engine does not map them to left/right.
- The upstream notes say the Conversation Awareness report is sent once after notifications are requested, not on every change. A client must therefore expect that a write may never be confirmed by a readback; it times out honestly rather than assuming success.

### 9.2 New command, behind capability `conversation_awareness_write`

| type | fields | rules |
|---|---|---|
| `set_conversation_awareness` | `request_id`, `session_id`, `enabled` (bool) | Allowed only in a `control` session, and only after the session has observed `conversation_awareness` at least once (`unsupported` otherwise, with a reason). One write: control `09 00 28 01 00 00 00` (on) or `... 28 02 ...` (off). The same one-at-a-time and in-flight rules as `set_listening_mode`. `accepted` is not confirmation. |

`hello_ack.capabilities` gains `conversation_awareness_write` (and `observe_conversation_awareness`, `observe_ear`) in 1.1. `session` gains an optional `writable_settings` array: `"listening_mode"` and/or `"conversation_awareness"` for what that session may write right now. When that set changes (for example once Conversation Awareness is first observed), a 1.1 engine re-sends `session` with the same `session_id` and state `open`. Clients treat it as an update to the open session, not a new session: request reservations and uncertain-write latches carry over.

An acknowledged `set_conversation_awareness` is not confirmation. The AirPods report Conversation Awareness once after notifications are requested, so a matching readback may never arrive. Until it does, the engine keeps that write reserved and refuses further CA writes in the session. In practice that means one unconfirmed CA write per session. Only a recognised on/off report counts as "observed" for allowing writes.

### 9.3 Deliberately not in 1.1

- **Adaptive strength**: the notes document a write (`09 00 2E [0..100]`) for Pro 2 only and no readback at all. Without a readback it could never be confirmed, so it stays unavailable. Revisit only with captured Pro 3 evidence of a report.
- Conversation-awareness speaking events (`4B`), ear-detection-driven media control (that is the app's job, from `ear` observations), anything else in section 7.

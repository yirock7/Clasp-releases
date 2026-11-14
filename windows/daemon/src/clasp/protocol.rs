//! clasp-engine protocol v1.1: message types and framing
//! (windows/protocol/clasp-engine-v1/README.md, sections 2 to 4 and 9).
//!
//! Version 1.1 is additive and negotiated: everything it adds (the
//! `set_conversation_awareness` command, the `conversation_awareness` / `ear`
//! observation fields, `session.writable_settings` and the three new
//! capabilities) exists on a connection only when `negotiated_minor` is >= 1.
//! A 1.0 client sees exactly the 1.0 behaviour.
//!
//! Everything here fails closed: a frame that is not exactly a valid, known
//! client message is an error, and the connection handler closes after it.

use serde::{Deserialize, Serialize};

pub const PROTOCOL: &str = "clasp-engine";
pub const MAJOR: u32 = 1;
/// The highest minor version this engine speaks.
pub const MINOR: u32 = 1;
/// Largest valid frame body, inclusive.
pub const MAX_FRAME: usize = 65_536;
/// Version 1.0 capabilities.
pub const CAPABILITIES: [&str; 3] = ["list_devices", "observe", "listening_mode_write"];
/// Capabilities added in 1.1 (advertised only when the negotiated minor is >= 1).
pub const CAPABILITIES_1_1: [&str; 3] =
    ["observe_conversation_awareness", "observe_ear", "conversation_awareness_write"];

/// The minor version a connection runs at: the lower of the client's and ours.
pub fn negotiated_minor(client_minor: u32) -> u32 {
    client_minor.min(MINOR)
}

/// The `hello_ack.capabilities` for a negotiated minor version.
pub fn capabilities(minor: u32) -> Vec<String> {
    let mut caps: Vec<String> = CAPABILITIES.iter().map(|c| c.to_string()).collect();
    if minor >= 1 {
        caps.extend(CAPABILITIES_1_1.iter().map(|c| c.to_string()));
    }
    caps
}

/// The client message types this engine knows (used to tell `unknown_type`
/// apart from `malformed`). `set_conversation_awareness` is 1.1: the connection
/// handler treats it as an unknown type when the negotiated minor is 0.
const CLIENT_TYPES: [&str; 7] = [
    "hello",
    "list_devices",
    "open_session",
    "close_session",
    "set_listening_mode",
    "set_conversation_awareness",
    "cancel",
];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SessionMode {
    Observe,
    Control,
}

/// A listening mode a client may request.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ListeningMode {
    Off,
    NoiseCancellation,
    Transparency,
    Adaptive,
}

impl ListeningMode {
    pub fn as_str(self) -> &'static str {
        match self {
            ListeningMode::Off => "off",
            ListeningMode::NoiseCancellation => "noise_cancellation",
            ListeningMode::Transparency => "transparency",
            ListeningMode::Adaptive => "adaptive",
        }
    }
}

/// A listening mode as observed from the device (`unknown` for a value the
/// engine does not recognise).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ObservedMode {
    Off,
    NoiseCancellation,
    Transparency,
    Adaptive,
    Unknown,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ClientMsg {
    Hello { protocol: String, major: u32, minor: u32, client: String },
    ListDevices { request_id: String },
    OpenSession { request_id: String, device_id: String, mode: SessionMode },
    CloseSession { request_id: String, session_id: u64 },
    SetListeningMode { request_id: String, session_id: u64, mode: ListeningMode },
    /// Protocol 1.1 only.
    SetConversationAwareness { request_id: String, session_id: u64, enabled: bool },
    Cancel { request_id: String, target_request_id: String },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Outcome {
    Accepted,
    Rejected,
    Failed,
    Unsupported,
    TimedOut,
    Cancelled,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SessionState {
    Opening,
    Open,
    Closed,
    Lost,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ErrorCode {
    IncompatibleVersion,
    Malformed,
    Oversized,
    UnknownType,
    NotAuthenticated,
    Busy,
    Internal,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DriverInterface {
    Bound,
    NotFound,
    Ambiguous,
    Unparsed,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DeviceEntry {
    pub device_id: String,
    pub name: String,
    pub aap_service: bool,
    pub driver_interface: DriverInterface,
    pub windows_connected: Option<bool>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct BatteryComponent {
    pub level: Option<u8>,
    pub charging: Option<bool>,
}

/// Conversation Awareness as reported by control id 0x28 (protocol 1.1).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ConversationAwareness {
    On,
    Off,
    Unknown,
}

/// One bud's ear-detection state (protocol 1.1). The transitional value 0x04 is
/// never represented: a packet carrying it produces no `ear` field at all.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum EarState {
    InEar,
    OutOfEar,
    InCase,
    Disconnected,
    Unknown,
}

/// Ear detection for the AirPods' own primary / secondary roles (not left/right).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Ear {
    pub primary: EarState,
    pub secondary: EarState,
}

/// The `fields` of one observation. A field that is `None` was not carried by the
/// packet and is not serialised at all.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Fields {
    #[serde(rename = "battery.left", default, skip_serializing_if = "Option::is_none")]
    pub battery_left: Option<BatteryComponent>,
    #[serde(rename = "battery.right", default, skip_serializing_if = "Option::is_none")]
    pub battery_right: Option<BatteryComponent>,
    #[serde(rename = "battery.case", default, skip_serializing_if = "Option::is_none")]
    pub battery_case: Option<BatteryComponent>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub listening_mode: Option<ObservedMode>,
    /// Protocol 1.1 only.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub conversation_awareness: Option<ConversationAwareness>,
    /// Protocol 1.1 only.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ear: Option<Ear>,
}

impl Fields {
    pub fn is_empty(&self) -> bool {
        *self == Fields::default()
    }

    /// Drop every field the negotiated minor version does not define.
    pub fn for_minor(mut self, minor: u32) -> Fields {
        if minor < 1 {
            self.conversation_awareness = None;
            self.ear = None;
        }
        self
    }
}

/// What a session may write right now (`session.writable_settings`, 1.1).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum WritableSetting {
    ListeningMode,
    ConversationAwareness,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum EngineMsg {
    HelloAck {
        seq: u64,
        protocol: String,
        major: u32,
        minor: u32,
        engine: String,
        engine_epoch: String,
        capabilities: Vec<String>,
    },
    Devices {
        seq: u64,
        request_id: String,
        devices: Vec<DeviceEntry>,
    },
    Result {
        seq: u64,
        request_id: String,
        outcome: Outcome,
        detail: Option<String>,
        /// Absent (`None`) for most results; `Some(Some(id))` for a successful
        /// `open_session`, `Some(None)` (JSON null) for a failed one.
        #[serde(default, skip_serializing_if = "Option::is_none", with = "double_option")]
        session_id: Option<Option<u64>>,
    },
    Session {
        seq: u64,
        session_id: u64,
        device_id: String,
        state: SessionState,
        reason: Option<String>,
        writable_modes: Vec<ListeningMode>,
        /// Protocol 1.1 only; absent (not serialised) for a 1.0 client.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        writable_settings: Option<Vec<WritableSetting>>,
    },
    Observation {
        seq: u64,
        session_id: u64,
        device_id: String,
        observed_at: String,
        fields: Fields,
    },
    Error {
        seq: u64,
        code: ErrorCode,
        detail: String,
        request_id: Option<String>,
    },
}

impl EngineMsg {
    pub fn set_seq(&mut self, n: u64) {
        match self {
            EngineMsg::HelloAck { seq, .. }
            | EngineMsg::Devices { seq, .. }
            | EngineMsg::Result { seq, .. }
            | EngineMsg::Session { seq, .. }
            | EngineMsg::Observation { seq, .. }
            | EngineMsg::Error { seq, .. } => *seq = n,
        }
    }

    pub fn result(request_id: &str, outcome: Outcome, detail: Option<String>) -> EngineMsg {
        EngineMsg::Result {
            seq: 0,
            request_id: request_id.to_string(),
            outcome,
            detail,
            session_id: None,
        }
    }

    pub fn error(code: ErrorCode, detail: &str, request_id: Option<&str>) -> EngineMsg {
        EngineMsg::Error {
            seq: 0,
            code,
            detail: detail.to_string(),
            request_id: request_id.map(str::to_string),
        }
    }
}

mod double_option {
    use serde::{Deserialize, Deserializer, Serialize, Serializer};

    pub fn serialize<S: Serializer>(v: &Option<Option<u64>>, s: S) -> Result<S::Ok, S::Error> {
        match v {
            Some(inner) => inner.serialize(s),
            None => s.serialize_none(),
        }
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Option<Option<u64>>, D::Error> {
        Option::<u64>::deserialize(d).map(Some)
    }
}

/// Why a frame was refused. Every variant closes the connection.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FrameError {
    ZeroLength,
    Oversized(u32),
    /// End of stream inside a frame.
    Truncated,
    Malformed(String),
    UnknownType(String),
}

impl FrameError {
    /// The `error` code to send before closing, or `None` when nothing can be sent
    /// (the peer is gone).
    pub fn code(&self) -> Option<ErrorCode> {
        match self {
            FrameError::ZeroLength | FrameError::Malformed(_) => Some(ErrorCode::Malformed),
            FrameError::Oversized(_) => Some(ErrorCode::Oversized),
            FrameError::UnknownType(_) => Some(ErrorCode::UnknownType),
            FrameError::Truncated => None,
        }
    }

    pub fn detail(&self) -> String {
        match self {
            FrameError::ZeroLength => "frame length 0".to_string(),
            FrameError::Oversized(n) => format!("frame length {n} exceeds {MAX_FRAME}"),
            FrameError::Truncated => "end of stream inside a frame".to_string(),
            FrameError::Malformed(d) => d.clone(),
            FrameError::UnknownType(t) => format!("unknown message type {t:?}"),
        }
    }
}

/// Incremental frame reader. The length header is checked as soon as its four
/// bytes are in, so an oversized frame is refused before any body is buffered.
#[derive(Default)]
pub struct FrameDecoder {
    buf: Vec<u8>,
}

impl FrameDecoder {
    pub fn push(&mut self, data: &[u8]) {
        self.buf.extend_from_slice(data);
    }

    /// The next complete frame body, `Ok(None)` if more bytes are needed.
    pub fn next_frame(&mut self) -> Result<Option<Vec<u8>>, FrameError> {
        if self.buf.len() < 4 {
            return Ok(None);
        }
        let n = u32::from_le_bytes([self.buf[0], self.buf[1], self.buf[2], self.buf[3]]);
        if n == 0 {
            return Err(FrameError::ZeroLength);
        }
        if n as usize > MAX_FRAME {
            return Err(FrameError::Oversized(n));
        }
        let total = 4 + n as usize;
        if self.buf.len() < total {
            return Ok(None);
        }
        let body = self.buf[4..total].to_vec();
        self.buf.drain(..total);
        Ok(Some(body))
    }

    /// Called at end of stream: any buffered partial frame is a truncation.
    pub fn finish(&self) -> Result<(), FrameError> {
        if self.buf.is_empty() {
            Ok(())
        } else {
            Err(FrameError::Truncated)
        }
    }
}

/// Parse one frame body as a client message.
pub fn parse_client_message(body: &[u8]) -> Result<ClientMsg, FrameError> {
    let text = std::str::from_utf8(body)
        .map_err(|_| FrameError::Malformed("frame body is not valid UTF-8".to_string()))?;
    let value: serde_json::Value = serde_json::from_str(text)
        .map_err(|e| FrameError::Malformed(format!("invalid JSON: {e}")))?;
    let obj = value
        .as_object()
        .ok_or_else(|| FrameError::Malformed("frame body is not a JSON object".to_string()))?;
    let ty = match obj.get("type") {
        Some(serde_json::Value::String(t)) => t.clone(),
        Some(_) => return Err(FrameError::Malformed("type is not a string".to_string())),
        None => return Err(FrameError::Malformed("missing type".to_string())),
    };
    if !CLIENT_TYPES.contains(&ty.as_str()) {
        return Err(FrameError::UnknownType(ty));
    }
    serde_json::from_value(value)
        .map_err(|e| FrameError::Malformed(format!("invalid {ty} message: {e}")))
}

/// Encode one engine message as a frame (length prefix + JSON).
pub fn encode_frame(msg: &EngineMsg) -> std::io::Result<Vec<u8>> {
    let body = serde_json::to_vec(msg).map_err(std::io::Error::other)?;
    if body.is_empty() || body.len() > MAX_FRAME {
        return Err(std::io::Error::other("outgoing frame exceeds the protocol limit"));
    }
    let mut out = Vec::with_capacity(4 + body.len());
    out.extend_from_slice(&(body.len() as u32).to_le_bytes());
    out.extend_from_slice(&body);
    Ok(out)
}

#[cfg(test)]
pub mod tests {
    use super::*;
    use serde_json::Value;
    use std::path::PathBuf;

    pub fn fixtures_dir(kind: &str) -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("protocol")
            .join("clasp-engine-v1")
            .join("fixtures")
            .join(kind)
    }

    pub fn load_fixtures(kind: &str) -> Vec<(String, Value)> {
        let mut out = Vec::new();
        for entry in std::fs::read_dir(fixtures_dir(kind)).expect("fixtures dir") {
            let p = entry.unwrap().path();
            if p.extension().and_then(|e| e.to_str()) == Some("json") {
                let text = std::fs::read_to_string(&p).unwrap();
                let v: Value = serde_json::from_str(&text).unwrap();
                out.push((p.file_name().unwrap().to_string_lossy().into_owned(), v));
            }
        }
        out.sort_by(|a, b| a.0.cmp(&b.0));
        assert!(!out.is_empty(), "no {kind} fixtures found");
        out
    }

    pub fn unhex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }

    fn frame_outcome(bytes: &[u8]) -> &'static str {
        let mut d = FrameDecoder::default();
        d.push(bytes);
        match d.next_frame() {
            Err(FrameError::ZeroLength) => "reject_zero_length",
            Err(FrameError::Oversized(_)) => "reject_oversized",
            Err(FrameError::Truncated) => "reject_truncated",
            Err(FrameError::Malformed(_)) => "reject_malformed",
            Err(FrameError::UnknownType(_)) => "reject_unknown_type",
            Ok(None) => match d.finish() {
                Err(FrameError::Truncated) => "reject_truncated",
                _ => "no_frame",
            },
            Ok(Some(body)) => match parse_client_message(&body) {
                Ok(_) => "ok",
                Err(FrameError::Malformed(_)) => "reject_malformed",
                Err(FrameError::UnknownType(_)) => "reject_unknown_type",
                Err(_) => "unexpected",
            },
        }
    }

    #[test]
    fn frame_fixtures() {
        let fixtures = load_fixtures("frames");
        for (name, f) in &fixtures {
            let bytes = unhex(f["bytes_hex"].as_str().unwrap());
            let expect = f["expect"].as_str().unwrap();
            assert_eq!(frame_outcome(&bytes), expect, "frame fixture {name}");
        }
        assert!(fixtures.len() >= 12);
    }

    #[test]
    fn oversized_is_refused_from_the_header_alone() {
        let mut d = FrameDecoder::default();
        d.push(&65_537u32.to_le_bytes());
        assert_eq!(d.next_frame(), Err(FrameError::Oversized(65_537)));
        let mut d = FrameDecoder::default();
        d.push(&65_536u32.to_le_bytes());
        assert_eq!(d.next_frame(), Ok(None));
    }

    #[test]
    fn frames_split_across_reads() {
        let msg = serde_json::json!({"type": "list_devices", "request_id": "r1"});
        let body = serde_json::to_vec(&msg).unwrap();
        let mut bytes = (body.len() as u32).to_le_bytes().to_vec();
        bytes.extend_from_slice(&body);
        bytes.extend_from_slice(&bytes.clone());
        let mut d = FrameDecoder::default();
        let mut got = 0;
        for b in &bytes {
            d.push(std::slice::from_ref(b));
            while let Some(f) = d.next_frame().unwrap() {
                assert!(matches!(parse_client_message(&f), Ok(ClientMsg::ListDevices { .. })));
                got += 1;
            }
        }
        assert_eq!(got, 2);
        assert!(d.finish().is_ok());
    }

    #[test]
    fn message_fixtures() {
        let fixtures = load_fixtures("messages");
        let mut engine_side = 0;
        let mut client_side = 0;
        for (name, f) in &fixtures {
            let msg = &f["message"];
            match f["direction"].as_str().unwrap() {
                "client_to_engine" => {
                    let body = serde_json::to_vec(msg).unwrap();
                    parse_client_message(&body)
                        .unwrap_or_else(|e| panic!("fixture {name} did not parse: {e:?}"));
                    client_side += 1;
                }
                "engine_to_client" => {
                    let parsed: EngineMsg = serde_json::from_value(msg.clone())
                        .unwrap_or_else(|e| panic!("fixture {name} did not parse: {e}"));
                    let reserialised = serde_json::to_value(&parsed).unwrap();
                    assert_eq!(&reserialised, msg, "fixture {name}: serialize-then-parse differs");
                    let frame = encode_frame(&parsed).unwrap();
                    let back: Value = serde_json::from_slice(&frame[4..]).unwrap();
                    assert_eq!(&back, msg, "fixture {name}: framed JSON differs");
                    engine_side += 1;
                }
                other => panic!("fixture {name}: unknown direction {other}"),
            }
        }
        assert!(client_side >= 8);
        assert!(engine_side >= 17);
    }

    #[test]
    fn hello_newer_minor_keeps_known_fields() {
        let body = serde_json::to_vec(&serde_json::json!({
            "type": "hello", "protocol": "clasp-engine", "major": 1, "minor": 7,
            "client": "Clasp 9", "future_field": true
        }))
        .unwrap();
        assert_eq!(
            parse_client_message(&body).unwrap(),
            ClientMsg::Hello {
                protocol: "clasp-engine".into(),
                major: 1,
                minor: 7,
                client: "Clasp 9".into()
            }
        );
    }

    #[test]
    fn capabilities_by_minor() {
        assert_eq!(negotiated_minor(0), 0);
        assert_eq!(negotiated_minor(1), 1);
        assert_eq!(negotiated_minor(7), MINOR);
        assert_eq!(capabilities(0), CAPABILITIES.to_vec());
        let mut v11 = CAPABILITIES.to_vec();
        v11.extend(CAPABILITIES_1_1);
        assert_eq!(capabilities(1), v11);
        // No adaptive-strength write exists in any version.
        let body = serde_json::to_vec(&serde_json::json!({
            "type": "set_adaptive_strength", "request_id": "r", "session_id": 1, "level": 50
        }))
        .unwrap();
        assert!(matches!(parse_client_message(&body), Err(FrameError::UnknownType(_))));
    }

    #[test]
    fn wrong_values_are_malformed() {
        for v in [
            serde_json::json!({"type": "open_session", "request_id": "r", "device_id": "d", "mode": "spy"}),
            serde_json::json!({"type": "set_listening_mode", "request_id": "r", "session_id": 1, "mode": "loud"}),
            serde_json::json!({"type": "close_session", "request_id": "r", "session_id": -1}),
            serde_json::json!({"type": "close_session", "request_id": "r"}),
            serde_json::json!({"type": "set_conversation_awareness", "request_id": "r", "session_id": 1, "enabled": "yes"}),
            serde_json::json!({"type": "set_conversation_awareness", "request_id": "r", "session_id": 1}),
            serde_json::json!({"type": 5}),
        ] {
            let body = serde_json::to_vec(&v).unwrap();
            assert!(matches!(parse_client_message(&body), Err(FrameError::Malformed(_))), "{v}");
        }
    }
}

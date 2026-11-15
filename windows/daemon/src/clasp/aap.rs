//! The only AAP the restricted engine speaks: three setup writes, one
//! listening-mode write, one Conversation Awareness write (protocol 1.1), and
//! decoding of battery / listening-mode / Conversation Awareness / ear packets.
//!
//! The packet bytes are the upstream ones (`src/aap.rs`); a test checks they stay
//! byte-identical. They are restated here so the engine does not compile the
//! upstream module (which also carries mic, heart-rate and rename packets).
//!
//! `check_write` is THE write allowlist: every byte sequence that reaches the
//! driver goes through `GuardedLink::send`, which calls it first.

use std::io;

use super::link::{AapLink, LinkStatus};
use super::log;
use super::protocol::{
    BatteryComponent, ConversationAwareness, Ear, EarState, Fields, ListeningMode, ObservedMode, SessionMode,
};

pub const PSM_AACP: u16 = 0x1001;

pub const HANDSHAKE: [u8; 16] = [
    0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
];
pub const SET_FEATURES: [u8; 14] = [
    0x04, 0x00, 0x04, 0x00, 0x4D, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
];
pub const REQUEST_NOTIFS: [u8; 10] = [0x04, 0x00, 0x04, 0x00, 0x0F, 0x00, 0xFF, 0xFF, 0xFF, 0xFF];

const HEADER: [u8; 4] = [0x04, 0x00, 0x04, 0x00];
const OP_BATTERY: u8 = 0x04;
const OP_EAR: u8 = 0x06;
const OP_CONTROL: u8 = 0x09;
const CONTROL_LISTENING_MODE: u8 = 0x0D;
const CONTROL_CONVERSATION_AWARENESS: u8 = 0x28;
/// Ear-detection value seen only while a bud moves between resting states.
const EAR_TRANSITIONAL: u8 = 0x04;

/// Modes a `control` session may write in protocol v1.0. `off` is deliberately
/// not writable in v1.0 (it is still reported when observed).
pub const WRITABLE_MODES: [ListeningMode; 3] =
    [ListeningMode::NoiseCancellation, ListeningMode::Transparency, ListeningMode::Adaptive];

pub fn writable_modes(mode: SessionMode) -> Vec<ListeningMode> {
    match mode {
        SessionMode::Control => WRITABLE_MODES.to_vec(),
        SessionMode::Observe => Vec::new(),
    }
}

fn mode_value(m: ListeningMode) -> u8 {
    match m {
        ListeningMode::Off => 1,
        ListeningMode::NoiseCancellation => 2,
        ListeningMode::Transparency => 3,
        ListeningMode::Adaptive => 4,
    }
}

/// Listening-mode control command (opcode 0x09, id 0x0D).
pub fn listening_mode_command(m: ListeningMode) -> [u8; 11] {
    [0x04, 0x00, 0x04, 0x00, OP_CONTROL, 0x00, CONTROL_LISTENING_MODE, mode_value(m), 0x00, 0x00, 0x00]
}

/// Conversation Awareness control command (opcode 0x09, id 0x28): 0x01 on, 0x02 off.
pub fn conversation_awareness_command(enabled: bool) -> [u8; 11] {
    let v = if enabled { 0x01 } else { 0x02 };
    [0x04, 0x00, 0x04, 0x00, OP_CONTROL, 0x00, CONTROL_CONVERSATION_AWARENESS, v, 0x00, 0x00, 0x00]
}

/// Where a session is: setup writes are allowed only while setting up, the
/// listening-mode write only once open (and only in `control`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Phase {
    Setup,
    Open,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AllowedWrite {
    Handshake,
    SetFeatures,
    RequestNotifs,
    ListeningMode(ListeningMode),
    ConversationAwareness(bool),
}

/// The write allowlist. Returns what the bytes are, or why they are refused.
pub fn check_write(mode: SessionMode, phase: Phase, bytes: &[u8]) -> Result<AllowedWrite, String> {
    let setup = if bytes == HANDSHAKE {
        Some(AllowedWrite::Handshake)
    } else if bytes == SET_FEATURES {
        Some(AllowedWrite::SetFeatures)
    } else if bytes == REQUEST_NOTIFS {
        Some(AllowedWrite::RequestNotifs)
    } else {
        None
    };
    if let Some(w) = setup {
        return if phase == Phase::Setup {
            Ok(w)
        } else {
            Err("setup packet outside session setup".to_string())
        };
    }
    if let Some(m) = WRITABLE_MODES.iter().copied().find(|m| bytes == listening_mode_command(*m)) {
        return match (mode, phase) {
            (SessionMode::Control, Phase::Open) => Ok(AllowedWrite::ListeningMode(m)),
            (SessionMode::Observe, _) => Err("listening-mode write in an observe session".to_string()),
            (_, Phase::Setup) => Err("listening-mode write before the session is open".to_string()),
        };
    }
    if let Some(on) = [true, false].into_iter().find(|on| bytes == conversation_awareness_command(*on)) {
        return match (mode, phase) {
            (SessionMode::Control, Phase::Open) => Ok(AllowedWrite::ConversationAwareness(on)),
            (SessionMode::Observe, _) => Err("conversation-awareness write in an observe session".to_string()),
            (_, Phase::Setup) => Err("conversation-awareness write before the session is open".to_string()),
        };
    }
    Err(format!("packet not on the write allowlist ({} bytes)", bytes.len()))
}

#[derive(Debug)]
pub enum WriteError {
    Refused(String),
    Failed(io::Error),
}

impl std::fmt::Display for WriteError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            WriteError::Refused(r) => write!(f, "refused: {r}"),
            WriteError::Failed(e) => write!(f, "driver write failed: {e}"),
        }
    }
}

/// The driver link with the allowlist in front of every write. The inner link is
/// private, so nothing can write around the check.
pub struct GuardedLink {
    link: Box<dyn AapLink>,
    mode: SessionMode,
    phase: Phase,
    device_id: String,
    closed: bool,
}

impl GuardedLink {
    pub fn new(link: Box<dyn AapLink>, mode: SessionMode, device_id: &str) -> GuardedLink {
        GuardedLink { link, mode, phase: Phase::Setup, device_id: device_id.to_string(), closed: false }
    }

    pub fn enter_open_phase(&mut self) {
        self.phase = Phase::Open;
    }

    pub fn connect(&mut self, addr: u64) -> io::Result<bool> {
        self.link.connect(addr, PSM_AACP)
    }

    pub fn send(&mut self, bytes: &[u8]) -> Result<AllowedWrite, WriteError> {
        if self.closed {
            return Err(WriteError::Refused("link closed".to_string()));
        }
        match check_write(self.mode, self.phase, bytes) {
            Ok(w) => {
                self.link.send(bytes).map_err(WriteError::Failed)?;
                Ok(w)
            }
            Err(reason) => {
                log::info(&format!("write refused for {}: {reason}", self.device_id));
                Err(WriteError::Refused(reason))
            }
        }
    }

    pub fn send_listening_mode(&mut self, m: ListeningMode) -> Result<AllowedWrite, WriteError> {
        if !WRITABLE_MODES.contains(&m) {
            log::info(&format!("write refused for {}: mode {} not writable", self.device_id, m.as_str()));
            return Err(WriteError::Refused(format!("mode {} is not writable", m.as_str())));
        }
        self.send(&listening_mode_command(m))
    }

    pub fn send_conversation_awareness(&mut self, enabled: bool) -> Result<AllowedWrite, WriteError> {
        self.send(&conversation_awareness_command(enabled))
    }

    pub fn recv(&mut self, timeout_ms: u32, buf: &mut [u8]) -> io::Result<usize> {
        self.link.recv(timeout_ms, buf)
    }

    pub fn status(&mut self) -> io::Result<LinkStatus> {
        self.link.status()
    }

    pub fn close(&mut self) {
        if !self.closed {
            self.closed = true;
            self.link.close();
        }
    }
}

impl Drop for GuardedLink {
    fn drop(&mut self) {
        self.close();
    }
}

/// Decode the observation carried by ONE inbound packet, or `None` when the
/// packet carries nothing protocol v1.1 reports. The connection strips the 1.1
/// fields again for a 1.0 client (`Fields::for_minor`).
pub fn decode_observation(pkt: &[u8]) -> Option<Fields> {
    if pkt.len() < 6 || pkt[..4] != HEADER {
        return None;
    }
    let fields = match pkt[4] {
        OP_BATTERY => decode_battery(pkt),
        OP_CONTROL => decode_control(pkt),
        OP_EAR => decode_ear(pkt),
        _ => Fields::default(),
    };
    if fields.is_empty() {
        None
    } else {
        Some(fields)
    }
}

/// Battery packet: `HEADER, 0x04, 0x00, count, count x [type, 0x01, level, status, 0x01]`.
/// Only components fully present in the packet are reported. Unlike the upstream
/// `aap::parse_battery`, charging is `null` (unknown) for an absent component,
/// never `false`.
fn decode_battery(pkt: &[u8]) -> Fields {
    let mut f = Fields::default();
    if pkt.len() < 7 {
        return f;
    }
    let count = pkt[6] as usize;
    for i in 0..count {
        let base = 7 + i * 5;
        if base + 5 > pkt.len() {
            break; // incomplete trailing component: skipped
        }
        let kind = pkt[base];
        let raw = pkt[base + 2];
        let status = pkt[base + 3];
        let comp = if status == 0x04 || raw == 0xFF || raw > 100 {
            BatteryComponent { level: None, charging: None }
        } else {
            // 0x01 charging, 0x05 charging in the case, 0x02 not charging (the only
            // not-charging status observed). Any other status: charging unknown.
            let charging = match status {
                0x01 | 0x05 => Some(true),
                0x02 => Some(false),
                _ => None,
            };
            BatteryComponent { level: Some(raw), charging }
        };
        let slot = match kind {
            0x02 => &mut f.battery_right,
            0x04 => &mut f.battery_left,
            0x08 => &mut f.battery_case,
            _ => continue, // 0x01 (single headphone) and others: not in v1.0
        };
        if slot.is_none() {
            *slot = Some(comp);
        }
    }
    f
}

/// Control report `HEADER, 0x09, 0x00, id, value, ...`. Only the listening mode
/// (0x0D) and Conversation Awareness (0x28) are reported; every other id
/// (adaptive strength 0x2E included) produces nothing.
fn decode_control(pkt: &[u8]) -> Fields {
    let mut f = Fields::default();
    if pkt.len() < 8 {
        return f;
    }
    match pkt[6] {
        CONTROL_LISTENING_MODE => {
            f.listening_mode = Some(match pkt[7] {
                1 => ObservedMode::Off,
                2 => ObservedMode::NoiseCancellation,
                3 => ObservedMode::Transparency,
                4 => ObservedMode::Adaptive,
                _ => ObservedMode::Unknown,
            });
        }
        CONTROL_CONVERSATION_AWARENESS => {
            f.conversation_awareness = Some(match pkt[7] {
                0x01 => ConversationAwareness::On,
                0x02 => ConversationAwareness::Off,
                _ => ConversationAwareness::Unknown,
            });
        }
        _ => {}
    }
    f
}

/// One ear-detection byte. `None` for the transitional value 0x04.
pub fn ear_state(v: u8) -> Option<EarState> {
    match v {
        0x00 => Some(EarState::InEar),
        0x01 => Some(EarState::OutOfEar),
        0x02 => Some(EarState::InCase),
        0x03 => Some(EarState::Disconnected),
        EAR_TRANSITIONAL => None,
        _ => Some(EarState::Unknown),
    }
}

/// Ear detection `HEADER, 0x06, 0x00, primary, secondary`. A truncated packet,
/// or one where either bud is transitional, produces no `ear` field.
fn decode_ear(pkt: &[u8]) -> Fields {
    let mut f = Fields::default();
    if pkt.len() >= 8 {
        if let (Some(primary), Some(secondary)) = (ear_state(pkt[6]), ear_state(pkt[7])) {
            f.ear = Some(Ear { primary, secondary });
        }
    }
    f
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::clasp::link::fake::FakeLink;
    use crate::clasp::protocol::tests::{load_fixtures, unhex};

    #[allow(dead_code)]
    #[path = "../../../aap.rs"]
    mod upstream_aap;

    #[test]
    fn aap_fixtures() {
        let fixtures = load_fixtures("aap");
        for (name, f) in &fixtures {
            let pkt = unhex(f["packet_hex"].as_str().unwrap());
            let got = decode_observation(&pkt).map(|fl| serde_json::to_value(fl).unwrap());
            let want = if f["expect_fields"].is_null() { None } else { Some(f["expect_fields"].clone()) };
            assert_eq!(got, want, "aap fixture {name}");
        }
        assert!(fixtures.len() >= 14, "expected the shared aap fixtures");
    }

    #[test]
    fn packets_match_upstream() {
        assert_eq!(HANDSHAKE, upstream_aap::HANDSHAKE);
        assert_eq!(SET_FEATURES, upstream_aap::SET_FEATURES);
        assert_eq!(REQUEST_NOTIFS, upstream_aap::REQUEST_NOTIFS);
        assert_eq!(PSM_AACP, upstream_aap::PSM_AACP);
        for m in [ListeningMode::Off, ListeningMode::NoiseCancellation, ListeningMode::Transparency, ListeningMode::Adaptive] {
            assert_eq!(listening_mode_command(m), upstream_aap::anc_command(mode_value(m)));
        }
        for v in 0..=255u8 {
            let pkt = upstream_aap::anc_command(v);
            let ours = decode_observation(&pkt).and_then(|f| f.listening_mode);
            assert_eq!(ours.is_some(), upstream_aap::parse_anc_mode(&pkt).is_some());
        }
    }

    #[test]
    fn allowlist_setup_phase() {
        for mode in [SessionMode::Observe, SessionMode::Control] {
            assert_eq!(check_write(mode, Phase::Setup, &HANDSHAKE), Ok(AllowedWrite::Handshake));
            assert_eq!(check_write(mode, Phase::Setup, &SET_FEATURES), Ok(AllowedWrite::SetFeatures));
            assert_eq!(check_write(mode, Phase::Setup, &REQUEST_NOTIFS), Ok(AllowedWrite::RequestNotifs));
            // Setup packets are refused once the session is open.
            assert!(check_write(mode, Phase::Open, &HANDSHAKE).is_err());
            assert!(check_write(mode, Phase::Open, &REQUEST_NOTIFS).is_err());
            // No listening-mode write during setup.
            assert!(check_write(mode, Phase::Setup, &listening_mode_command(ListeningMode::Transparency)).is_err());
        }
    }

    #[test]
    fn allowlist_listening_modes() {
        for m in WRITABLE_MODES {
            assert_eq!(
                check_write(SessionMode::Control, Phase::Open, &listening_mode_command(m)),
                Ok(AllowedWrite::ListeningMode(m))
            );
            assert!(check_write(SessionMode::Observe, Phase::Open, &listening_mode_command(m)).is_err());
        }
        // Off is not writable in v1.0.
        let off = listening_mode_command(ListeningMode::Off);
        assert!(check_write(SessionMode::Control, Phase::Open, &off).is_err());
    }

    #[test]
    fn allowlist_refuses_everything_else() {
        let others: Vec<Vec<u8>> = vec![
            upstream_aap::START_AUDIO.to_vec(),
            upstream_aap::STOP_AUDIO.to_vec(),
            upstream_aap::HR_ENABLE.to_vec(),
            upstream_aap::HR_CONNECT_SERVICE_0.to_vec(),
            upstream_aap::build_rename("x").to_vec(),
            upstream_aap::feature_command(0x2E, true).to_vec(),
            upstream_aap::control_command(0x2E, 50).to_vec(),
            upstream_aap::control_command(0x28, 0).to_vec(),
            upstream_aap::control_command(0x28, 3).to_vec(),
            upstream_aap::control_command(0x0D, 9).to_vec(),
            upstream_aap::control_command(0x0D, 0).to_vec(),
            vec![],
            vec![0x04, 0x00, 0x04, 0x00],
            [listening_mode_command(ListeningMode::Transparency).as_slice(), &[0x00]].concat(),
        ];
        for bytes in &others {
            for mode in [SessionMode::Observe, SessionMode::Control] {
                for phase in [Phase::Setup, Phase::Open] {
                    assert!(check_write(mode, phase, bytes).is_err(), "{bytes:02x?} allowed");
                }
            }
        }
        // Every 11-byte control command except the three writable modes and the
        // two Conversation Awareness values is refused (adaptive strength 0x2E too).
        for id in 0..=255u8 {
            for v in 0..=255u8 {
                let pkt = upstream_aap::control_command(id, v);
                let ok = check_write(SessionMode::Control, Phase::Open, &pkt).is_ok();
                let want = (id == 0x0D && (2..=4).contains(&v)) || (id == 0x28 && (v == 1 || v == 2));
                assert_eq!(ok, want, "id {id:#x} value {v}");
            }
        }
    }

    #[test]
    fn allowlist_conversation_awareness() {
        for on in [true, false] {
            let cmd = conversation_awareness_command(on);
            // Byte-identical to the upstream boolean feature toggle for id 0x28.
            assert_eq!(cmd, upstream_aap::feature_command(0x28, on));
            assert_eq!(check_write(SessionMode::Control, Phase::Open, &cmd), Ok(AllowedWrite::ConversationAwareness(on)));
            assert!(check_write(SessionMode::Observe, Phase::Open, &cmd).is_err());
            for mode in [SessionMode::Observe, SessionMode::Control] {
                assert!(check_write(mode, Phase::Setup, &cmd).is_err());
            }
        }
        let fake = FakeLink::new();
        let sent = fake.sent.clone();
        let mut g = GuardedLink::new(Box::new(fake), SessionMode::Observe, "dev-test");
        g.enter_open_phase();
        assert!(matches!(g.send_conversation_awareness(true), Err(WriteError::Refused(_))));
        assert!(sent.lock().unwrap().is_empty());
    }

    #[test]
    fn ear_decode_table() {
        let want = |v: u8| match v {
            0x00 => Some(EarState::InEar),
            0x01 => Some(EarState::OutOfEar),
            0x02 => Some(EarState::InCase),
            0x03 => Some(EarState::Disconnected),
            0x04 => None,
            _ => Some(EarState::Unknown),
        };
        for p in 0..=255u8 {
            assert_eq!(ear_state(p), want(p), "value {p:#x}");
            for s in 0..=255u8 {
                let got = decode_observation(&[0x04, 0x00, 0x04, 0x00, 0x06, 0x00, p, s]);
                match (want(p), want(s)) {
                    (Some(primary), Some(secondary)) => {
                        assert_eq!(got, Some(Fields { ear: Some(Ear { primary, secondary }), ..Fields::default() }));
                    }
                    // Either bud transitional: no ear field, so no observation at all.
                    _ => assert_eq!(got, None, "{p:#x} {s:#x}"),
                }
            }
        }
        // Truncated: no observation.
        assert_eq!(decode_observation(&[0x04, 0x00, 0x04, 0x00, 0x06, 0x00, 0x00]), None);
        assert_eq!(decode_observation(&[0x04, 0x00, 0x04, 0x00, 0x06, 0x00]), None);
    }

    #[test]
    fn conversation_awareness_decode() {
        for v in 0..=255u8 {
            let f = decode_observation(&upstream_aap::control_command(0x28, v)).unwrap();
            let want = match v {
                1 => ConversationAwareness::On,
                2 => ConversationAwareness::Off,
                _ => ConversationAwareness::Unknown,
            };
            assert_eq!(f, Fields { conversation_awareness: Some(want), ..Fields::default() });
            // Adaptive strength (0x2E) is never reported.
            assert_eq!(decode_observation(&upstream_aap::control_command(0x2E, v)), None);
        }
        // Truncated control report: nothing.
        assert_eq!(decode_observation(&[0x04, 0x00, 0x04, 0x00, 0x09, 0x00, 0x28]), None);
        // A 1.0 connection never sees the 1.1 fields.
        let f = decode_observation(&upstream_aap::control_command(0x28, 1)).unwrap();
        assert!(f.for_minor(0).is_empty());
    }

    #[test]
    fn guarded_link_never_writes_refused_bytes() {
        let fake = FakeLink::new();
        let sent = fake.sent.clone();
        let mut g = GuardedLink::new(Box::new(fake), SessionMode::Observe, "dev-test");
        assert!(matches!(g.send(&upstream_aap::START_AUDIO), Err(WriteError::Refused(_))));
        assert!(g.send(&HANDSHAKE).is_ok());
        g.enter_open_phase();
        assert!(matches!(g.send_listening_mode(ListeningMode::Transparency), Err(WriteError::Refused(_))));
        assert!(matches!(g.send(&HANDSHAKE), Err(WriteError::Refused(_))));
        assert_eq!(*sent.lock().unwrap(), vec![HANDSHAKE.to_vec()]);

        let fake = FakeLink::new();
        let sent = fake.sent.clone();
        let mut g = GuardedLink::new(Box::new(fake), SessionMode::Control, "dev-test");
        g.enter_open_phase();
        assert!(matches!(g.send_listening_mode(ListeningMode::Off), Err(WriteError::Refused(_))));
        assert!(g.send_listening_mode(ListeningMode::Adaptive).is_ok());
        g.close();
        assert!(matches!(g.send_listening_mode(ListeningMode::Adaptive), Err(WriteError::Refused(_))));
        assert_eq!(*sent.lock().unwrap(), vec![listening_mode_command(ListeningMode::Adaptive).to_vec()]);
    }

    #[test]
    fn battery_edge_cases() {
        // Unknown component type only: no observation.
        assert_eq!(decode_observation(&unhex("04000400040001010132020100")), None);
        // Level above 100 is not a valid level.
        let f = decode_observation(&unhex("04000400040001020165010100")).unwrap();
        assert_eq!(f.battery_right, Some(BatteryComponent { level: None, charging: None }));
        // Unknown status byte with a valid level: charging is unknown, not false.
        let f = decode_observation(&unhex("04000400040001020132030100")).unwrap();
        assert_eq!(f.battery_right, Some(BatteryComponent { level: Some(50), charging: None }));
        let f = decode_observation(&unhex("04000400040001040132000100")).unwrap();
        assert_eq!(f.battery_left, Some(BatteryComponent { level: Some(50), charging: None }));
        // Component present but last byte missing: skipped.
        assert_eq!(decode_observation(&unhex("0400040004000102013202")), None);
    }
}

//! One client connection: handshake, request dispatch and seq numbering
//! (protocol sections 2 to 4). Transport-agnostic so it is tested both over an
//! in-memory transport and over a real (test-named) local pipe.

use std::io;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use super::identity;
use super::log;
use super::protocol::{
    capabilities, encode_frame, negotiated_minor, parse_client_message, ClientMsg, EngineMsg, ErrorCode,
    FrameDecoder, Outcome, MAJOR, PROTOCOL,
};
use super::session::{EngineCtx, SessionManager};

pub const HELLO_DEADLINE: Duration = Duration::from_secs(5);

pub enum ReadOutcome {
    Data(usize),
    /// Nothing available right now.
    Nothing,
    Eof,
}

pub trait Transport {
    /// Non-blocking read of whatever is available.
    fn read_some(&mut self, buf: &mut [u8]) -> io::Result<ReadOutcome>;
    /// Write all of `data` (loops over partial writes).
    fn write_all(&mut self, data: &[u8]) -> io::Result<()>;
}

/// Process-wide engine state: epoch, seq counter, shutdown flag, sessions.
pub struct Engine {
    pub ctx: Arc<EngineCtx>,
    pub epoch: String,
    pub shutdown: Arc<AtomicBool>,
    seq: AtomicU64,
}

impl Engine {
    pub fn new(ctx: Arc<EngineCtx>, epoch: String, shutdown: Arc<AtomicBool>) -> Engine {
        Engine { ctx, epoch, shutdown, seq: AtomicU64::new(0) }
    }

    /// Strictly increasing per epoch, starting at 1.
    fn next_seq(&self) -> u64 {
        self.seq.fetch_add(1, Ordering::SeqCst) + 1
    }

    pub fn engine_name() -> String {
        format!("clasp-engine {}", env!("CARGO_PKG_VERSION"))
    }
}

struct Conn<'a> {
    engine: &'a Engine,
    t: &'a mut dyn Transport,
    sessions: SessionManager,
    hello_done: bool,
    /// min(client minor, engine minor); everything 1.1 adds needs >= 1.
    minor: u32,
}

/// Why a connection ended (for the log).
type Closed = String;

impl Conn<'_> {
    fn send(&mut self, mut msg: EngineMsg) -> Result<(), Closed> {
        msg.set_seq(self.engine.next_seq());
        let frame = encode_frame(&msg).map_err(|e| format!("encode failed: {e}"))?;
        self.t.write_all(&frame).map_err(|e| format!("write failed: {e}"))
    }

    fn send_all(&mut self, msgs: Vec<EngineMsg>) -> Result<(), Closed> {
        for m in msgs {
            self.send(m)?;
        }
        Ok(())
    }

    fn fatal(&mut self, code: ErrorCode, detail: &str) -> Closed {
        let _ = self.send(EngineMsg::error(code, detail, None));
        format!("{code:?}: {detail}")
    }

    fn handle(&mut self, msg: ClientMsg) -> Result<(), Closed> {
        if !self.hello_done {
            let ClientMsg::Hello { protocol, major, minor, client } = msg else {
                return Err(self.fatal(ErrorCode::NotAuthenticated, "the first message must be hello"));
            };
            if protocol != PROTOCOL {
                return Err(self.fatal(ErrorCode::IncompatibleVersion, "unknown protocol"));
            }
            if major != MAJOR {
                let d = format!("engine speaks major {MAJOR}, client asked for {major}");
                return Err(self.fatal(ErrorCode::IncompatibleVersion, &d));
            }
            let negotiated = negotiated_minor(minor);
            log::info(&format!("hello from {client:?} (protocol {major}.{minor}; using {major}.{negotiated})"));
            self.hello_done = true;
            self.minor = negotiated;
            self.sessions.set_minor(negotiated);
            // A 1.0 client gets exactly the 1.0 hello_ack (minor 0, 1.0 capabilities).
            return self.send(EngineMsg::HelloAck {
                seq: 0,
                protocol: PROTOCOL.to_string(),
                major: MAJOR,
                minor: negotiated,
                engine: Engine::engine_name(),
                engine_epoch: self.engine.epoch.clone(),
                capabilities: capabilities(negotiated),
            });
        }
        let out = match msg {
            ClientMsg::Hello { .. } => return Err(self.fatal(ErrorCode::Malformed, "duplicate hello")),
            ClientMsg::ListDevices { request_id } => vec![self.list_devices(&request_id)],
            ClientMsg::OpenSession { request_id, device_id, mode } => {
                self.sessions.open(&request_id, &device_id, mode)
            }
            ClientMsg::CloseSession { request_id, session_id } => self.sessions.close(&request_id, session_id),
            ClientMsg::SetListeningMode { request_id, session_id, mode } => {
                self.sessions.set_listening_mode(&request_id, session_id, mode)
            }
            ClientMsg::SetConversationAwareness { request_id, session_id, enabled } => {
                if self.minor < 1 {
                    // Not a 1.0 message type: exactly what a 1.0 engine would do.
                    return Err(self.fatal(ErrorCode::UnknownType, "unknown message type \"set_conversation_awareness\""));
                }
                self.sessions.set_conversation_awareness(&request_id, session_id, enabled)
            }
            ClientMsg::Cancel { request_id, target_request_id } => {
                self.sessions.cancel(&request_id, &target_request_id)
            }
        };
        self.send_all(out)
    }

    fn list_devices(&self, request_id: &str) -> EngineMsg {
        let ctx = &self.engine.ctx;
        let listed = ctx
            .source
            .inventory()
            .and_then(|inv| identity::list_devices(&inv, &ctx.salt));
        match listed {
            Ok(devices) => {
                log::info(&format!("list_devices: {} device(s)", devices.len()));
                EngineMsg::Devices { seq: 0, request_id: request_id.to_string(), devices }
            }
            Err(e) => EngineMsg::result(request_id, Outcome::Failed, Some(format!("device enumeration failed: {e}"))),
        }
    }

    fn run(&mut self, decoder: &mut FrameDecoder) -> Result<(), Closed> {
        let started = Instant::now();
        let mut buf = vec![0u8; 16 * 1024];
        loop {
            if self.engine.shutdown.load(Ordering::SeqCst) {
                return Err("engine shutting down".into());
            }
            let mut idle = true;
            match self.t.read_some(&mut buf) {
                Ok(ReadOutcome::Data(n)) => {
                    idle = false;
                    decoder.push(&buf[..n]);
                }
                Ok(ReadOutcome::Nothing) => {}
                Ok(ReadOutcome::Eof) => {
                    return Err(if decoder.finish().is_err() {
                        "end of stream inside a frame".into()
                    } else {
                        "client disconnected".into()
                    });
                }
                Err(e) => return Err(format!("read failed: {e}")),
            }
            loop {
                let body = match decoder.next_frame() {
                    Ok(Some(b)) => b,
                    Ok(None) => break,
                    Err(fe) => return Err(self.frame_error(&fe)),
                };
                match parse_client_message(&body) {
                    Ok(msg) => self.handle(msg)?,
                    Err(fe) => return Err(self.frame_error(&fe)),
                }
            }
            if !self.hello_done && started.elapsed() >= HELLO_DEADLINE {
                return Err("no hello within 5 s".into());
            }
            let events = self.sessions.poll();
            if !events.is_empty() {
                idle = false;
                self.send_all(events)?;
            }
            if idle {
                std::thread::sleep(Duration::from_millis(10));
            }
        }
    }

    fn frame_error(&mut self, fe: &super::protocol::FrameError) -> Closed {
        match fe.code() {
            Some(code) => self.fatal(code, &fe.detail()),
            None => fe.detail(),
        }
    }
}

/// Serve one client until it disconnects, breaks the protocol, or the engine
/// stops. Any session it opened is closed before this returns.
pub fn serve_connection(engine: &Engine, t: &mut dyn Transport) -> String {
    let mut conn = Conn {
        engine,
        t,
        sessions: SessionManager::new(engine.ctx.clone()),
        hello_done: false,
        minor: 0,
    };
    let mut decoder = FrameDecoder::default();
    let reason = conn.run(&mut decoder).err().unwrap_or_default();
    let closing = conn.sessions.shutdown(if engine.shutdown.load(Ordering::SeqCst) {
        "engine shutting down"
    } else {
        "client disconnected"
    });
    if conn.hello_done {
        let _ = conn.send_all(closing);
    }
    reason
}

#[cfg(test)]
pub mod tests {
    use super::*;
    use crate::clasp::identity::tests::{inventory, SALT};
    use crate::clasp::protocol::tests::{load_fixtures, unhex};
    use crate::clasp::session::tests::{fast_timings, FakeSource, PanicOpener};
    use serde_json::{json, Value};
    use std::collections::VecDeque;

    pub fn test_engine() -> Engine {
        let ctx = EngineCtx::new(Arc::new(FakeSource(inventory())), Arc::new(PanicOpener), SALT, fast_timings());
        Engine::new(Arc::new(ctx), "6f1c2a9e-3b7d-4c11-9a52-0d8e4f7b2c10".into(), Arc::new(AtomicBool::new(false)))
    }

    /// Scripted transport: hands out the input in one chunk, then EOF.
    struct Script {
        input: VecDeque<Vec<u8>>,
        output: Vec<u8>,
    }

    impl Transport for Script {
        fn read_some(&mut self, buf: &mut [u8]) -> io::Result<ReadOutcome> {
            match self.input.pop_front() {
                Some(c) => {
                    buf[..c.len()].copy_from_slice(&c);
                    Ok(ReadOutcome::Data(c.len()))
                }
                None => Ok(ReadOutcome::Eof),
            }
        }
        fn write_all(&mut self, data: &[u8]) -> io::Result<()> {
            self.output.extend_from_slice(data);
            Ok(())
        }
    }

    pub fn frame(v: &Value) -> Vec<u8> {
        let body = serde_json::to_vec(v).unwrap();
        let mut f = (body.len() as u32).to_le_bytes().to_vec();
        f.extend_from_slice(&body);
        f
    }

    pub fn decode_all(mut bytes: &[u8]) -> Vec<Value> {
        let mut out = Vec::new();
        while bytes.len() >= 4 {
            let n = u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]) as usize;
            out.push(serde_json::from_slice(&bytes[4..4 + n]).unwrap());
            bytes = &bytes[4 + n..];
        }
        out
    }

    fn run(engine: &Engine, chunks: Vec<Vec<u8>>) -> (Vec<Value>, String) {
        let mut s = Script { input: chunks.into(), output: Vec::new() };
        let reason = serve_connection(engine, &mut s);
        (decode_all(&s.output), reason)
    }

    pub fn hello(major: u32) -> Value {
        json!({"type": "hello", "protocol": "clasp-engine", "major": major, "minor": 0, "client": "test"})
    }

    #[test]
    fn handshake_and_seq() {
        let e = test_engine();
        let (out, _) = run(&e, vec![frame(&hello(1)), frame(&json!({"type": "list_devices", "request_id": "r1"}))]);
        assert_eq!(out.len(), 2);
        assert_eq!(out[0]["type"], "hello_ack");
        assert_eq!(out[0]["seq"], 1);
        assert_eq!(out[0]["engine"], "clasp-engine 0.1.0");
        assert_eq!(out[0]["capabilities"], json!(["list_devices", "observe", "listening_mode_write"]));
        assert_eq!(out[1]["type"], "devices");
        assert_eq!(out[1]["seq"], 2);
        assert_eq!(out[1]["devices"][0]["driver_interface"], "bound");
        // Seq keeps increasing across connections of the same epoch.
        let (out2, _) = run(&e, vec![frame(&hello(1))]);
        assert_eq!(out2[0]["seq"], 3);
    }

    fn hello_minor(minor: u32) -> Value {
        json!({"type": "hello", "protocol": "clasp-engine", "major": 1, "minor": minor, "client": "test"})
    }

    fn set_ca() -> Value {
        json!({"type": "set_conversation_awareness", "request_id": "w", "session_id": 1, "enabled": true})
    }

    #[test]
    fn negotiation_v10_sees_exactly_v10() {
        let (out, reason) = run(&test_engine(), vec![frame(&hello_minor(0)), frame(&set_ca()), frame(&json!({"type": "list_devices", "request_id": "r"}))]);
        assert_eq!(out[0]["type"], "hello_ack");
        assert_eq!(out[0]["minor"], 0);
        assert_eq!(out[0]["capabilities"], json!(["list_devices", "observe", "listening_mode_write"]));
        // The 1.1 command is an unknown type for a 1.0 connection: error, then close.
        assert_eq!(out.len(), 2, "{out:?}");
        assert_eq!(out[1]["type"], "error");
        assert_eq!(out[1]["code"], "unknown_type");
        assert_eq!(out[1]["request_id"], Value::Null);
        assert!(reason.contains("UnknownType"), "{reason}");
    }

    #[test]
    fn negotiation_v11_and_newer() {
        let fixture = load_fixtures("messages").into_iter().find(|(n, _)| n == "hello_ack_v11.json").unwrap().1;
        for minor in [1, 7] {
            let (out, _) = run(&test_engine(), vec![frame(&hello_minor(minor)), frame(&set_ca())]);
            assert_eq!(out[0]["minor"], 1);
            assert_eq!(out[0]["capabilities"], fixture["message"]["capabilities"]);
            // Accepted as a command; with no session it is a stale-id rejection, connection stays up.
            assert_eq!(out[1]["type"], "result");
            assert_eq!(out[1]["request_id"], "w");
            assert_eq!(out[1]["outcome"], "rejected");
            assert!(out[1].get("session_id").is_none());
        }
    }

    #[test]
    fn wrong_major_is_refused() {
        let (out, reason) = run(&test_engine(), vec![frame(&hello(2)), frame(&json!({"type": "list_devices", "request_id": "r"}))]);
        assert_eq!(out.len(), 1);
        assert_eq!(out[0]["code"], "incompatible_version");
        assert_eq!(out[0]["detail"], "engine speaks major 1, client asked for 2");
        assert_eq!(out[0]["request_id"], Value::Null);
        assert!(reason.contains("IncompatibleVersion"));
    }

    #[test]
    fn first_message_must_be_hello() {
        let (out, _) = run(&test_engine(), vec![frame(&json!({"type": "list_devices", "request_id": "r"}))]);
        assert_eq!(out.len(), 1);
        assert_eq!(out[0]["code"], "not_authenticated");
    }

    #[test]
    fn frame_fixtures_close_the_connection() {
        for (name, f) in load_fixtures("frames") {
            let expect = f["expect"].as_str().unwrap();
            let bytes = unhex(f["bytes_hex"].as_str().unwrap());
            // As the first frame:
            let (out, reason) = run(&test_engine(), vec![bytes.clone()]);
            let want_code = match expect {
                "ok" => None,
                "reject_oversized" => Some("oversized"),
                "reject_zero_length" | "reject_malformed" => Some("malformed"),
                "reject_unknown_type" => Some("unknown_type"),
                "reject_truncated" => None,
                other => panic!("{other}"),
            };
            match want_code {
                Some(code) => {
                    assert_eq!(out.len(), 1, "{name}");
                    assert_eq!(out[0]["code"], code, "{name}");
                }
                None if expect == "ok" => assert_eq!(out[0]["type"], "hello_ack", "{name}"),
                None => {
                    assert!(out.is_empty(), "{name}");
                    assert!(reason.contains("inside a frame"), "{name}: {reason}");
                }
            }
            // After a valid handshake, the same bytes:
            let (out, _) = run(&test_engine(), vec![frame(&hello(1)), bytes]);
            assert_eq!(out[0]["type"], "hello_ack");
            if let Some(code) = want_code {
                assert_eq!(out.len(), 2, "{name}");
                assert_eq!(out[1]["code"], code, "{name}");
            }
        }
    }

    #[test]
    fn messages_after_an_error_are_not_processed() {
        let (out, _) = run(
            &test_engine(),
            vec![frame(&hello(1)), [frame(&json!({"type": "raw_aap"})), frame(&json!({"type": "list_devices", "request_id": "r"}))].concat()],
        );
        assert_eq!(out.len(), 2);
        assert_eq!(out[1]["code"], "unknown_type");
    }

    #[test]
    fn requests_without_a_session() {
        let (out, _) = run(
            &test_engine(),
            vec![
                frame(&hello(1)),
                frame(&json!({"type": "set_listening_mode", "request_id": "w", "session_id": 1, "mode": "transparency"})),
                frame(&json!({"type": "close_session", "request_id": "c", "session_id": 1})),
                frame(&json!({"type": "cancel", "request_id": "x", "target_request_id": "nope"})),
            ],
        );
        let outcomes: Vec<&Value> = out[1..].iter().map(|m| &m["outcome"]).collect();
        assert_eq!(outcomes, vec!["rejected", "rejected", "rejected"]);
        assert!(out.iter().all(|m| m["type"] != "observation"));
    }

    #[test]
    fn hello_deadline() {
        struct Silent;
        impl Transport for Silent {
            fn read_some(&mut self, _: &mut [u8]) -> io::Result<ReadOutcome> {
                Ok(ReadOutcome::Nothing)
            }
            fn write_all(&mut self, _: &[u8]) -> io::Result<()> {
                panic!("nothing may be sent before hello");
            }
        }
        let t0 = Instant::now();
        let reason = serve_connection(&test_engine(), &mut Silent);
        assert!(reason.contains("no hello"));
        assert!(t0.elapsed() >= HELLO_DEADLINE && t0.elapsed() < HELLO_DEADLINE + Duration::from_secs(2));
    }
}

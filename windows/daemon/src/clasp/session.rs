//! The guarded AAP session (review findings HR-01 / HR-02).
//!
//! One session at a time. `open_session` -> `opening` -> identity check -> open
//! the exact driver interface -> connect(verified address, PSM_AACP) -> the three
//! setup writes (~300 ms apart) -> `open` -> receive loop. The worker thread owns
//! the link; the connection handler talks to it through channels, so the link is
//! never used from two threads. There is no automatic reconnect: a failure ends
//! the session (`lost`) and only a new `open_session` starts another.
//!
//! Commands never produce observations: observations come only from
//! `aap::decode_observation` on received packets, stamped with the receive time.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, Sender, TryRecvError};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use super::aap::{self, GuardedLink, WriteError};
use super::identity::{self, DeviceSource};
use super::link::{LinkOpener, LP_CONNECTED};
use super::log;
use super::protocol::{
    ConversationAwareness, EngineMsg, ErrorCode, Fields, ListeningMode, Outcome, SessionMode, SessionState,
    WritableSetting,
};
use super::util::utc_now_iso_ms;

#[derive(Debug, Clone)]
pub struct Timings {
    /// Gap between the three setup writes.
    pub setup_gap: Duration,
    /// Driver receive timeout; bounds how fast close/cancel take effect.
    pub recv_timeout_ms: u32,
    /// Overall budget for `open_session` before it reports `timed_out`.
    pub open_timeout: Duration,
    /// How often the driver connection state is checked while open.
    pub status_interval: Duration,
    /// Consecutive "not connected" status reads before the session is `lost`.
    pub lost_after_down_checks: u32,
    /// How long close/shutdown wait for the worker to leave a driver call. After
    /// that the session is reported closed and the worker keeps draining (the
    /// engine answers `busy` until it exits).
    pub join_timeout: Duration,
}

impl Default for Timings {
    fn default() -> Self {
        Timings {
            setup_gap: Duration::from_millis(300),
            recv_timeout_ms: 250,
            open_timeout: Duration::from_secs(20),
            status_interval: Duration::from_secs(1),
            lost_after_down_checks: 3,
            join_timeout: Duration::from_secs(2),
        }
    }
}

/// Engine-wide session state shared by every client connection.
pub struct EngineCtx {
    pub source: Arc<dyn DeviceSource>,
    pub opener: Arc<dyn LinkOpener>,
    pub salt: [u8; 32],
    pub timings: Timings,
    next_session_id: AtomicU64,
    /// Workers that were told to stop but may still be inside a driver call
    /// (for example a slow connect after `cancel`). While any is alive the engine
    /// is `busy`, so two driver handles are never open at once.
    draining: Mutex<Vec<JoinHandle<()>>>,
}

impl EngineCtx {
    pub fn new(
        source: Arc<dyn DeviceSource>,
        opener: Arc<dyn LinkOpener>,
        salt: [u8; 32],
        timings: Timings,
    ) -> EngineCtx {
        EngineCtx {
            source,
            opener,
            salt,
            timings,
            next_session_id: AtomicU64::new(0),
            draining: Mutex::new(Vec::new()),
        }
    }

    fn alloc_session_id(&self) -> u64 {
        self.next_session_id.fetch_add(1, Ordering::SeqCst) + 1
    }

    /// Joins finished draining workers; true if any is still running.
    fn draining_busy(&self) -> bool {
        let mut d = self.draining.lock().unwrap_or_else(|e| e.into_inner());
        let mut still = Vec::new();
        for h in d.drain(..) {
            if h.is_finished() {
                let _ = h.join();
            } else {
                still.push(h);
            }
        }
        *d = still;
        !d.is_empty()
    }

    /// Wait up to `timeout` for draining workers to finish (engine shutdown).
    pub fn wait_drained(&self, timeout: Duration) -> bool {
        let deadline = Instant::now() + timeout;
        while self.draining_busy() {
            if Instant::now() >= deadline {
                return false;
            }
            std::thread::sleep(Duration::from_millis(20));
        }
        true
    }
}

enum WorkerEvent {
    Opened,
    OpenFailed(String),
    Observation { fields: Fields, observed_at: String },
    WriteResult { request_id: String, outcome: Outcome, detail: Option<String> },
    Lost(String),
}

enum WorkerCmd {
    Write { request_id: String, mode: ListeningMode },
    /// Protocol 1.1: one Conversation Awareness write.
    WriteCa { request_id: String, enabled: bool },
}

impl WorkerCmd {
    fn request_id(&self) -> &str {
        match self {
            WorkerCmd::Write { request_id, .. } | WorkerCmd::WriteCa { request_id, .. } => request_id,
        }
    }
}

enum Phase {
    Opening { since: Instant, request_id: String },
    Open,
}

/// The session's Conversation Awareness write reservation (protocol 1.1). While
/// it exists no other CA write is accepted in this session. It is released only
/// by an explicit refusal (`rejected` / `unsupported`: nothing was sent) or by a
/// readback, received AFTER the driver acknowledged the write, that equals the
/// requested value. An ack alone never releases it: the device reports CA once
/// after REQUEST_NOTIFS, so a later write may never be read back, and it then
/// stays unconfirmed for the rest of the session. A failed or uncertain write
/// stays reserved until the session ends.
struct CaWrite {
    request_id: String,
    enabled: bool,
    state: CaWriteState,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum CaWriteState {
    /// Queued for, or inside, the driver call.
    Pending,
    /// The driver accepted the write; no matching readback yet.
    AckedUnconfirmed,
    /// The driver call failed or its outcome is unknown; it may have reached the device.
    Uncertain,
}

pub const CA_NOT_OBSERVED: &str = "conversation awareness has not been reported by the device in this session";
pub const CA_WRITE_IN_FLIGHT: &str = "a conversation awareness write is still in flight";
pub const CA_WRITE_UNCONFIRMED: &str =
    "the previous conversation awareness write was acknowledged but not confirmed by a readback; end the session and start a new one before writing again";
pub const CA_WRITE_UNCERTAIN: &str =
    "the previous conversation awareness write is uncertain; end the session and start a new one before writing again";
pub const WRITE_IN_FLIGHT: &str = "another write is still in flight";

struct Active {
    id: u64,
    device_id: String,
    mode: SessionMode,
    /// Negotiated protocol minor of the connection that opened this session.
    minor: u32,
    phase: Phase,
    stop: Arc<AtomicBool>,
    cmd_tx: Sender<WorkerCmd>,
    ev_rx: Receiver<WorkerEvent>,
    thread: Option<JoinHandle<()>>,
    pending_writes: Vec<String>,
    /// The write currently inside the driver call, if any. Set and cleared by the
    /// worker under this lock together with its stop check (see `worker`).
    in_flight: Arc<Mutex<Option<String>>>,
    /// A recognised (`on` / `off`) Conversation Awareness report was received in
    /// this session.
    ca_observed: bool,
    ca_write: Option<CaWrite>,
    /// The `writable_settings` last sent for the open session (1.1 only).
    sent_settings: Option<Vec<WritableSetting>>,
}

impl Active {
    /// `session.writable_settings`: `None` (field absent) for a 1.0 connection.
    fn writable_settings(&self, state: SessionState) -> Option<Vec<WritableSetting>> {
        if self.minor < 1 {
            return None;
        }
        let mut s = Vec::new();
        if state == SessionState::Open && matches!(self.phase, Phase::Open) && self.mode == SessionMode::Control {
            // set_listening_mode is refused while a CA write is on its way to the driver, so do
            // not advertise it then (the list is re-sent when this changes).
            if !matches!(self.ca_write, Some(CaWrite { state: CaWriteState::Pending, .. })) {
                s.push(WritableSetting::ListeningMode);
            }
            if self.ca_observed && self.ca_write.is_none() {
                s.push(WritableSetting::ConversationAwareness);
            }
        }
        Some(s)
    }

    fn session_msg(&self, state: SessionState, reason: Option<String>) -> EngineMsg {
        let writable = if state == SessionState::Open { aap::writable_modes(self.mode) } else { Vec::new() };
        EngineMsg::Session {
            seq: 0,
            session_id: self.id,
            device_id: self.device_id.clone(),
            state,
            reason,
            writable_modes: writable,
            writable_settings: self.writable_settings(state),
        }
    }

    /// One received packet's fields: updates the CA state, then returns the
    /// observation to send (only the fields the connection's minor defines; no
    /// message when none remain).
    fn on_observation(&mut self, fields: Fields, observed_at: String) -> Option<EngineMsg> {
        if let Some(ca) = fields.conversation_awareness {
            let known = match ca {
                ConversationAwareness::On => Some(true),
                ConversationAwareness::Off => Some(false),
                ConversationAwareness::Unknown => None,
            };
            if let Some(on) = known {
                self.ca_observed = true;
                // Events arrive in order from the worker, so a readback processed
                // after the ack was received after the write was sent.
                if let Some(w) = &self.ca_write {
                    if w.state == CaWriteState::AckedUnconfirmed && w.enabled == on {
                        log::info(&format!("session {}: conversation-awareness write confirmed by readback", self.id));
                        self.ca_write = None;
                    }
                }
            }
        }
        let fields = fields.for_minor(self.minor);
        if fields.is_empty() {
            return None;
        }
        Some(EngineMsg::Observation {
            seq: 0,
            session_id: self.id,
            device_id: self.device_id.clone(),
            observed_at,
            fields,
        })
    }

    /// The worker's answer to one write. The result is the ack only; it never
    /// confirms the value.
    fn on_write_result(&mut self, request_id: String, outcome: Outcome, detail: Option<String>) -> EngineMsg {
        self.pending_writes.retain(|p| *p != request_id);
        let is_ca = self.ca_write.as_ref().is_some_and(|w| w.request_id == request_id);
        if is_ca {
            match outcome {
                Outcome::Accepted => {
                    if let Some(w) = self.ca_write.as_mut() {
                        w.state = CaWriteState::AckedUnconfirmed;
                    }
                }
                Outcome::Rejected | Outcome::Unsupported => self.ca_write = None,
                _ => {
                    if let Some(w) = self.ca_write.as_mut() {
                        w.state = CaWriteState::Uncertain;
                    }
                }
            }
            log::info(&format!("session {}: conversation-awareness write {outcome:?} (not a readback)", self.id));
        } else {
            log::info(&format!("session {}: listening-mode write {outcome:?}", self.id));
        }
        EngineMsg::result(&request_id, outcome, detail)
    }
}

/// Per-connection session handling. Methods return the messages to send, in order.
pub struct SessionManager {
    ctx: Arc<EngineCtx>,
    active: Option<Active>,
    /// Negotiated protocol minor of this connection (0 until set after `hello`).
    minor: u32,
}

const STALE: &str = "unknown or stale session_id";

impl SessionManager {
    pub fn new(ctx: Arc<EngineCtx>) -> SessionManager {
        SessionManager { ctx, active: None, minor: 0 }
    }

    /// Set once the handshake negotiated the connection's minor version.
    pub fn set_minor(&mut self, minor: u32) {
        self.minor = minor;
    }

    pub fn open(&mut self, request_id: &str, device_id: &str, mode: SessionMode) -> Vec<EngineMsg> {
        if self.active.is_some() || self.ctx.draining_busy() {
            return vec![EngineMsg::error(
                ErrorCode::Busy,
                "a session is already open, opening or closing",
                Some(request_id),
            )];
        }
        let id = self.ctx.alloc_session_id();
        let stop = Arc::new(AtomicBool::new(false));
        let (cmd_tx, cmd_rx) = mpsc::channel();
        let (ev_tx, ev_rx) = mpsc::channel();
        let ctx = self.ctx.clone();
        let (dev, st) = (device_id.to_string(), stop.clone());
        let in_flight: Arc<Mutex<Option<String>>> = Arc::default();
        let fl = in_flight.clone();
        let spawned = std::thread::Builder::new()
            .name(format!("clasp-session-{id}"))
            .spawn(move || worker(ctx, id, dev, mode, st, fl, cmd_rx, ev_tx));
        let thread = match spawned {
            Ok(t) => t,
            Err(e) => {
                let mut r = EngineMsg::result(request_id, Outcome::Failed, Some(format!("internal: {e}")));
                if let EngineMsg::Result { session_id, .. } = &mut r {
                    *session_id = Some(None);
                }
                return vec![r];
            }
        };
        log::info(&format!("session {id}: opening {device_id} ({mode:?})"));
        let active = Active {
            id,
            device_id: device_id.to_string(),
            mode,
            minor: self.minor,
            phase: Phase::Opening { since: Instant::now(), request_id: request_id.to_string() },
            stop,
            cmd_tx,
            ev_rx,
            thread: Some(thread),
            pending_writes: Vec::new(),
            in_flight,
            ca_observed: false,
            ca_write: None,
            sent_settings: None,
        };
        let msg = active.session_msg(SessionState::Opening, None);
        self.active = Some(active);
        vec![msg]
    }

    pub fn close(&mut self, request_id: &str, session_id: u64) -> Vec<EngineMsg> {
        let Some(a) = self.active.as_ref().filter(|a| a.id == session_id) else {
            return vec![EngineMsg::result(request_id, Outcome::Rejected, Some(STALE.into()))];
        };
        let mut out = Vec::new();
        if let Phase::Opening { request_id: open_req, .. } = &a.phase {
            out.push(open_result(open_req, Outcome::Cancelled, Some("closed before the session opened".into()), None));
            out.push(a.session_msg(SessionState::Closed, Some("closed by request".into())));
            out.push(EngineMsg::result(request_id, Outcome::Accepted, None));
            self.end(false);
            log::info(&format!("session {session_id}: closed while opening"));
            return out;
        }
        // Open: stop the loop, wait for the worker to close the driver handle, then
        // report what it received before it stopped.
        let mut a = self.active.take().expect("checked above");
        let exited = stop_worker(&self.ctx, &mut a, true);
        // Snapshot after the stop flag is set: from here on the worker starts no
        // new write, and a write that finishes now has already queued its result.
        let in_flight = in_flight_snapshot(&a);
        while let Ok(ev) = a.ev_rx.try_recv() {
            match ev {
                WorkerEvent::Observation { fields, observed_at } => out.extend(a.on_observation(fields, observed_at)),
                WorkerEvent::WriteResult { request_id: rid, outcome, detail } => {
                    out.push(a.on_write_result(rid, outcome, detail));
                }
                _ => {}
            }
        }
        out.extend(fail_pending(&mut a, "session closed before the write was sent", in_flight));
        out.push(EngineMsg::result(request_id, Outcome::Accepted, None));
        let reason = (!exited).then(|| STILL_FINISHING.to_string());
        out.push(a.session_msg(SessionState::Closed, reason));
        log::info(&format!(
            "session {session_id}: closed by request{}",
            if exited { "" } else { " (driver call still finishing)" }
        ));
        out
    }

    pub fn set_listening_mode(&mut self, request_id: &str, session_id: u64, mode: ListeningMode) -> Vec<EngineMsg> {
        let Some(a) = self.active.as_mut().filter(|a| a.id == session_id) else {
            return vec![EngineMsg::result(request_id, Outcome::Rejected, Some(STALE.into()))];
        };
        if let Phase::Opening { .. } = a.phase {
            return vec![EngineMsg::result(request_id, Outcome::Rejected, Some("session is not open yet".into()))];
        }
        if a.mode == SessionMode::Observe {
            return vec![EngineMsg::result(
                request_id,
                Outcome::Unsupported,
                Some("listening-mode writes are disabled in an observe session".into()),
            )];
        }
        if !aap::writable_modes(a.mode).contains(&mode) {
            return vec![EngineMsg::result(
                request_id,
                Outcome::Unsupported,
                Some(format!("mode {} is not writable in protocol 1.0", mode.as_str())),
            )];
        }
        // One write at a time across kinds: never queue behind a pending CA write
        // (only reachable on a 1.1 connection; 1.0 listening-mode behaviour is unchanged).
        if a.ca_write.as_ref().is_some_and(|w| w.state == CaWriteState::Pending) {
            return vec![EngineMsg::result(request_id, Outcome::Rejected, Some(CA_WRITE_IN_FLIGHT.into()))];
        }
        if a.cmd_tx.send(WorkerCmd::Write { request_id: request_id.to_string(), mode }).is_err() {
            return vec![EngineMsg::result(request_id, Outcome::Failed, Some("session worker stopped".into()))];
        }
        a.pending_writes.push(request_id.to_string());
        Vec::new()
    }

    /// Protocol 1.1 `set_conversation_awareness`. Allowed only in an open
    /// `control` session that has observed a recognised CA report, with no other
    /// write outstanding and no earlier CA write reserved (see `CaWrite`). The
    /// `result` that follows is the driver ack only, never confirmation.
    pub fn set_conversation_awareness(&mut self, request_id: &str, session_id: u64, enabled: bool) -> Vec<EngineMsg> {
        let Some(a) = self.active.as_mut().filter(|a| a.id == session_id) else {
            return vec![EngineMsg::result(request_id, Outcome::Rejected, Some(STALE.into()))];
        };
        if a.minor < 1 {
            // The connection handler never routes this for a 1.0 client; fail closed anyway.
            return vec![EngineMsg::result(
                request_id,
                Outcome::Unsupported,
                Some("conversation awareness writes need protocol 1.1".into()),
            )];
        }
        if let Phase::Opening { .. } = a.phase {
            return vec![EngineMsg::result(request_id, Outcome::Rejected, Some("session is not open yet".into()))];
        }
        if a.mode == SessionMode::Observe {
            return vec![EngineMsg::result(
                request_id,
                Outcome::Unsupported,
                Some("conversation awareness writes are disabled in an observe session".into()),
            )];
        }
        if !a.ca_observed {
            return vec![EngineMsg::result(request_id, Outcome::Unsupported, Some(CA_NOT_OBSERVED.into()))];
        }
        if let Some(w) = &a.ca_write {
            let detail = match w.state {
                CaWriteState::Pending => CA_WRITE_IN_FLIGHT,
                CaWriteState::AckedUnconfirmed => CA_WRITE_UNCONFIRMED,
                CaWriteState::Uncertain => CA_WRITE_UNCERTAIN,
            };
            return vec![EngineMsg::result(request_id, Outcome::Rejected, Some(detail.into()))];
        }
        if !a.pending_writes.is_empty() {
            return vec![EngineMsg::result(request_id, Outcome::Rejected, Some(WRITE_IN_FLIGHT.into()))];
        }
        if a.cmd_tx.send(WorkerCmd::WriteCa { request_id: request_id.to_string(), enabled }).is_err() {
            return vec![EngineMsg::result(request_id, Outcome::Failed, Some("session worker stopped".into()))];
        }
        a.pending_writes.push(request_id.to_string());
        a.ca_write = Some(CaWrite { request_id: request_id.to_string(), enabled, state: CaWriteState::Pending });
        Vec::new()
    }

    pub fn cancel(&mut self, request_id: &str, target: &str) -> Vec<EngineMsg> {
        if let Some(a) = self.active.as_ref() {
            if let Phase::Opening { request_id: open_req, .. } = &a.phase {
                if open_req == target {
                    let out = vec![
                        open_result(open_req, Outcome::Cancelled, Some("cancelled by client".into()), None),
                        a.session_msg(SessionState::Closed, Some("cancelled".into())),
                        EngineMsg::result(request_id, Outcome::Accepted, None),
                    ];
                    log::info(&format!("session {}: open cancelled", a.id));
                    self.end(false);
                    return out;
                }
            }
            if a.pending_writes.iter().any(|p| p == target) {
                return vec![EngineMsg::result(request_id, Outcome::Rejected, Some("writes cannot be cancelled".into()))];
            }
        }
        vec![EngineMsg::result(
            request_id,
            Outcome::Rejected,
            Some("no outstanding open_session with this request_id".into()),
        )]
    }

    /// Collect worker events and enforce the open timeout.
    pub fn poll(&mut self) -> Vec<EngineMsg> {
        let mut out = Vec::new();
        let Some(a) = self.active.as_mut() else { return out };
        let mut end: Option<bool> = None; // Some(join)
        loop {
            match a.ev_rx.try_recv() {
                Ok(WorkerEvent::Opened) => {
                    if let Phase::Opening { request_id, .. } = &a.phase {
                        out.push(open_result(request_id, Outcome::Accepted, None, Some(a.id)));
                        a.phase = Phase::Open;
                        out.push(a.session_msg(SessionState::Open, None));
                        a.sent_settings = a.writable_settings(SessionState::Open);
                        log::info(&format!("session {}: open ({:?})", a.id, a.mode));
                    }
                }
                Ok(WorkerEvent::OpenFailed(detail)) => {
                    if let Phase::Opening { request_id, .. } = &a.phase {
                        out.push(open_result(request_id, Outcome::Failed, Some(detail.clone()), None));
                        out.push(a.session_msg(SessionState::Closed, Some(format!("open failed: {detail}"))));
                    }
                    log::info(&format!("session {}: open failed: {detail}", a.id));
                    end = Some(true);
                    break;
                }
                Ok(WorkerEvent::Observation { fields, observed_at }) => {
                    if let Phase::Open = a.phase {
                        out.extend(a.on_observation(fields, observed_at));
                    }
                }
                Ok(WorkerEvent::WriteResult { request_id, outcome, detail }) => {
                    out.push(a.on_write_result(request_id, outcome, detail));
                }
                Ok(WorkerEvent::Lost(reason)) => {
                    out.extend({
                        let fl = in_flight_snapshot(a);
                        fail_pending(a, "session lost before the write was sent", fl)
                    });
                    out.push(a.session_msg(SessionState::Lost, Some(reason.clone())));
                    log::info(&format!("session {}: lost: {reason}", a.id));
                    end = Some(true);
                    break;
                }
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => {
                    // The worker ended without saying why (should not happen).
                    if let Phase::Opening { request_id, .. } = &a.phase {
                        out.push(open_result(request_id, Outcome::Failed, Some("internal: session worker stopped".into()), None));
                        out.push(a.session_msg(SessionState::Closed, Some("open failed: internal".into())));
                    } else {
                        out.extend({
                            let fl = in_flight_snapshot(a);
                            fail_pending(a, "session lost before the write was sent", fl)
                        });
                        out.push(a.session_msg(SessionState::Lost, Some("internal: session worker stopped".into())));
                    }
                    end = Some(true);
                    break;
                }
            }
        }
        if end.is_none() {
            if let Phase::Opening { since, request_id } = &a.phase {
                if since.elapsed() >= self.ctx.timings.open_timeout {
                    out.push(open_result(request_id, Outcome::TimedOut, Some("open_session timed out".into()), None));
                    out.push(a.session_msg(SessionState::Closed, Some("open timed out".into())));
                    log::info(&format!("session {}: open timed out", a.id));
                    end = Some(false);
                }
            }
        }
        if end.is_none() && matches!(a.phase, Phase::Open) {
            // 1.1: tell the client when what it may write changes (CA first
            // observed, a CA write reserved or released). Never for 1.0.
            let now = a.writable_settings(SessionState::Open);
            if now.is_some() && now != a.sent_settings {
                out.push(a.session_msg(SessionState::Open, None));
                a.sent_settings = now;
            }
        }
        if let Some(join) = end {
            self.end(join);
        }
        out
    }

    /// Close whatever session exists (client gone or engine stopping).
    pub fn shutdown(&mut self, reason: &str) -> Vec<EngineMsg> {
        let Some(mut a) = self.active.take() else { return Vec::new() };
        let opening = matches!(a.phase, Phase::Opening { .. });
        log::info(&format!("session {}: closing ({reason})", a.id));
        let mut out = Vec::new();
        if let Phase::Opening { request_id, .. } = &a.phase {
            out.push(open_result(request_id, Outcome::Cancelled, Some(reason.to_string()), None));
        }
        let exited = stop_worker(&self.ctx, &mut a, !opening);
        let in_flight = in_flight_snapshot(&a);
        out.extend(fail_pending(&mut a, "session closed before the write was sent", in_flight));
        let reason = if exited || opening {
            reason.to_string()
        } else {
            format!("{reason}; driver call still finishing")
        };
        out.push(a.session_msg(SessionState::Closed, Some(reason)));
        out
    }

    /// Stop the current worker; wait for it (bounded) or leave it draining.
    fn end(&mut self, wait: bool) {
        if let Some(mut a) = self.active.take() {
            stop_worker(&self.ctx, &mut a, wait);
        }
    }
}

const STILL_FINISHING: &str = "closed; driver call still finishing";

/// Tell the worker to stop. With `wait`, give it up to `join_timeout` to exit
/// (it closes the driver handle on its way out). A worker that has not exited is
/// moved to the engine's draining list, which keeps new opens `busy` until it
/// does. Never blocks longer than `join_timeout`. True if the worker exited.
fn stop_worker(ctx: &EngineCtx, a: &mut Active, wait: bool) -> bool {
    a.stop.store(true, Ordering::SeqCst);
    let Some(t) = a.thread.take() else { return true };
    if wait {
        let deadline = Instant::now() + ctx.timings.join_timeout;
        while !t.is_finished() && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(5));
        }
        if t.is_finished() {
            let _ = t.join();
            return true;
        }
        log::info(&format!("session {}: worker still inside a driver call; draining", a.id));
    }
    ctx.draining.lock().unwrap_or_else(|e| e.into_inner()).push(t);
    false
}

fn open_result(request_id: &str, outcome: Outcome, detail: Option<String>, id: Option<u64>) -> EngineMsg {
    EngineMsg::Result {
        seq: 0,
        request_id: request_id.to_string(),
        outcome,
        detail,
        session_id: Some(id),
    }
}

pub const WRITE_UNCERTAIN: &str =
    "the session closed while this write was in progress; it may have reached the device";

fn in_flight_snapshot(a: &Active) -> Option<String> {
    a.in_flight.lock().unwrap_or_else(|e| e.into_inner()).clone()
}

/// Answer every write still pending when a session ends. The one that was inside
/// the driver call is uncertain (`timed_out`); the rest were never started (and
/// never will be: the worker checks the stop flag before each write).
fn fail_pending(a: &mut Active, detail: &str, in_flight: Option<String>) -> Vec<EngineMsg> {
    a.pending_writes
        .drain(..)
        .map(|rid| {
            if in_flight.as_deref() == Some(rid.as_str()) {
                EngineMsg::result(&rid, Outcome::TimedOut, Some(WRITE_UNCERTAIN.to_string()))
            } else {
                EngineMsg::result(&rid, Outcome::Failed, Some(detail.to_string()))
            }
        })
        .collect()
}

/// Sleep up to `d`, waking early if `stop` is set. True if not stopped.
fn sleep_unless_stopped(d: Duration, stop: &AtomicBool) -> bool {
    let deadline = Instant::now() + d;
    while Instant::now() < deadline {
        if stop.load(Ordering::SeqCst) {
            return false;
        }
        let left = deadline.saturating_duration_since(Instant::now());
        std::thread::sleep(left.min(Duration::from_millis(10)));
    }
    !stop.load(Ordering::SeqCst)
}

#[allow(clippy::too_many_arguments)]
fn worker(
    ctx: Arc<EngineCtx>,
    id: u64,
    device_id: String,
    mode: SessionMode,
    stop: Arc<AtomicBool>,
    in_flight: Arc<Mutex<Option<String>>>,
    cmd_rx: Receiver<WorkerCmd>,
    ev_tx: Sender<WorkerEvent>,
) {
    let send = |e: WorkerEvent| {
        let _ = ev_tx.send(e);
    };
    let fail = |d: String| send(WorkerEvent::OpenFailed(d));
    let stopped = || stop.load(Ordering::SeqCst);

    // 1. Identity: exactly one paired AAP device and exactly one interface naming it.
    let inv = match ctx.source.inventory() {
        Ok(i) => i,
        Err(e) => return fail(format!("device enumeration failed: {e}")),
    };
    let resolved = match identity::resolve(&inv, &ctx.salt, &device_id) {
        Ok(r) => r,
        Err(d) => return fail(d),
    };
    if stopped() {
        return;
    }

    // 2. Open that exact interface path.
    let link = match ctx.opener.open(&resolved.interface_path) {
        Ok(l) => l,
        Err(e) => return fail(format!("driver open failed: {e}")),
    };
    let mut g = GuardedLink::new(link, mode, &device_id);
    if stopped() {
        g.close();
        return;
    }

    // 3. Connect, only to the verified address.
    match g.connect(resolved.address) {
        Ok(true) => {}
        Ok(false) => {
            g.close();
            return fail("driver connect failed".into());
        }
        Err(e) => {
            g.close();
            return fail(format!("driver connect failed: {e}"));
        }
    }
    if stopped() {
        g.close();
        return;
    }
    // Sanity check only: the driver reports back the address it was given in the
    // connect call (Ioctl.c / L2cap.c), so this does NOT prove which device
    // answered. It only catches a driver that reports some other address.
    if let Ok(st) = g.status() {
        if st.address != 0 && st.address != resolved.address {
            g.close();
            return fail("driver channel is connected to a different device".into());
        }
    }

    // 4. The three setup writes.
    let setup: [&[u8]; 3] = [&aap::HANDSHAKE, &aap::SET_FEATURES, &aap::REQUEST_NOTIFS];
    for (i, pkt) in setup.iter().enumerate() {
        if i > 0 && !sleep_unless_stopped(ctx.timings.setup_gap, &stop) {
            g.close();
            return;
        }
        if let Err(e) = g.send(pkt) {
            g.close();
            return fail(format!("setup write failed: {e}"));
        }
    }
    if stopped() {
        g.close();
        return;
    }
    g.enter_open_phase();
    log::info(&format!("session {id}: setup writes done"));
    send(WorkerEvent::Opened);

    // 5. Receive loop.
    let mut buf = vec![0u8; 8192];
    let mut last_status = Instant::now();
    let mut down = 0u32;
    let recv_ms = ctx.timings.recv_timeout_ms.max(1);
    loop {
        if stopped() {
            g.close();
            return;
        }
        while let Ok(cmd) = cmd_rx.try_recv() {
            let request_id = cmd.request_id().to_string();
            {
                // Check-and-mark under the lock the manager reads after setting
                // the stop flag: either we see the stop and never send, or the
                // manager sees this write as in flight.
                let mut fl = in_flight.lock().unwrap_or_else(|e| e.into_inner());
                if stopped() {
                    drop(fl);
                    g.close();
                    return; // queued writes stay unsent; the manager answers them
                }
                *fl = Some(request_id.clone());
            }
            let sent = match cmd {
                WorkerCmd::Write { mode, .. } => g.send_listening_mode(mode),
                WorkerCmd::WriteCa { enabled, .. } => g.send_conversation_awareness(enabled),
            };
            let (outcome, detail) = match sent {
                Ok(_) => (Outcome::Accepted, None),
                Err(WriteError::Refused(r)) => (Outcome::Unsupported, Some(r)),
                Err(e @ WriteError::Failed(_)) => (Outcome::Failed, Some(e.to_string())),
            };
            // Result first, then clear: a manager that sees no in-flight write
            // finds this result in the channel.
            send(WorkerEvent::WriteResult { request_id, outcome, detail });
            *in_flight.lock().unwrap_or_else(|e| e.into_inner()) = None;
        }
        let t0 = Instant::now();
        match g.recv(recv_ms, &mut buf) {
            Ok(n) if n > 0 => {
                let observed_at = utc_now_iso_ms();
                if let Some(fields) = aap::decode_observation(&buf[..n.min(buf.len())]) {
                    send(WorkerEvent::Observation { fields, observed_at });
                }
            }
            _ => {
                // Timeout or error: avoid spinning if the driver returns at once.
                if t0.elapsed() < Duration::from_millis(u64::from(recv_ms) / 2) {
                    sleep_unless_stopped(Duration::from_millis(u64::from(recv_ms) / 2), &stop);
                }
            }
        }
        if last_status.elapsed() >= ctx.timings.status_interval {
            last_status = Instant::now();
            match g.status() {
                Err(e) => {
                    g.close();
                    return send(WorkerEvent::Lost(format!("driver status failed: {e}")));
                }
                Ok(st) if st.state == LP_CONNECTED => down = 0,
                Ok(st) => {
                    down += 1;
                    if down >= ctx.timings.lost_after_down_checks {
                        g.close();
                        return send(WorkerEvent::Lost(format!("link down (driver state {})", st.state)));
                    }
                }
            }
        }
    }
}

#[cfg(test)]
pub mod tests {
    use super::*;
    use crate::clasp::identity::tests::{inventory, ADDR_A, SALT};
    use crate::clasp::identity::{device_id, Inventory};
    use crate::clasp::link::fake::FakeLink;
    use crate::clasp::link::{AapLink, LinkStatus};
    use crate::clasp::protocol::{BatteryComponent, ObservedMode};
    use std::io;

    pub struct FakeSource(pub Inventory);
    impl DeviceSource for FakeSource {
        fn inventory(&self) -> io::Result<Inventory> {
            Ok(self.0.clone())
        }
    }

    pub struct FakeOpener {
        pub link: FakeLink,
        pub opened: Arc<Mutex<Vec<String>>>,
    }
    impl LinkOpener for FakeOpener {
        fn open(&self, path: &str) -> io::Result<Box<dyn AapLink>> {
            self.opened.lock().unwrap().push(path.to_string());
            Ok(Box::new(self.link.share()))
        }
    }

    /// An opener that must never be used (protocol-only tests).
    pub struct PanicOpener;
    impl LinkOpener for PanicOpener {
        fn open(&self, _: &str) -> io::Result<Box<dyn AapLink>> {
            panic!("a driver open was attempted");
        }
    }

    pub fn fast_timings() -> Timings {
        Timings {
            setup_gap: Duration::from_millis(1),
            recv_timeout_ms: 5,
            open_timeout: Duration::from_secs(5),
            status_interval: Duration::from_millis(10),
            lost_after_down_checks: 3,
            join_timeout: Duration::from_millis(200),
        }
    }

    struct Rig {
        mgr: SessionManager,
        link: FakeLink,
        opened: Arc<Mutex<Vec<String>>>,
        dev: String,
    }

    fn rig_with(inv: Inventory, timings: Timings) -> Rig {
        let link = FakeLink::new();
        let opened = Arc::new(Mutex::new(Vec::new()));
        let opener = FakeOpener { link: link.share(), opened: opened.clone() };
        let ctx = Arc::new(EngineCtx::new(Arc::new(FakeSource(inv)), Arc::new(opener), SALT, timings));
        Rig { mgr: SessionManager::new(ctx), link, opened, dev: device_id(&SALT, ADDR_A).unwrap() }
    }

    fn rig() -> Rig {
        rig_with(inventory(), fast_timings())
    }

    /// Poll until `pred` holds on the collected messages (or panic after 5 s).
    fn poll_until(mgr: &mut SessionManager, mut pred: impl FnMut(&[EngineMsg]) -> bool) -> Vec<EngineMsg> {
        let deadline = Instant::now() + Duration::from_secs(5);
        let mut all = Vec::new();
        loop {
            all.extend(mgr.poll());
            if pred(&all) {
                return all;
            }
            assert!(Instant::now() < deadline, "timed out; got {all:?}");
            std::thread::sleep(Duration::from_millis(2));
        }
    }

    fn has_state(msgs: &[EngineMsg], want: SessionState) -> bool {
        msgs.iter().any(|m| matches!(m, EngineMsg::Session { state, .. } if *state == want))
    }

    fn result_for<'a>(msgs: &'a [EngineMsg], rid: &str) -> Option<(&'a Outcome, &'a Option<String>)> {
        msgs.iter().find_map(|m| match m {
            EngineMsg::Result { request_id, outcome, detail, .. } if request_id == rid => Some((outcome, detail)),
            _ => None,
        })
    }

    fn open(r: &mut Rig, mode: SessionMode) -> u64 {
        let first = r.mgr.open("open-1", &r.dev.clone(), mode);
        assert!(matches!(&first[..], [EngineMsg::Session { state: SessionState::Opening, writable_modes, .. }] if writable_modes.is_empty()));
        let msgs = poll_until(&mut r.mgr, |m| has_state(m, SessionState::Open));
        match &msgs[..] {
            [EngineMsg::Result { outcome: Outcome::Accepted, session_id: Some(Some(id)), .. }, EngineMsg::Session { state: SessionState::Open, session_id, writable_modes, .. }] => {
                assert_eq!(id, session_id);
                assert_eq!(*writable_modes, aap::writable_modes(mode));
                *id
            }
            other => panic!("unexpected {other:?}"),
        }
    }

    fn setup_writes() -> Vec<Vec<u8>> {
        vec![aap::HANDSHAKE.to_vec(), aap::SET_FEATURES.to_vec(), aap::REQUEST_NOTIFS.to_vec()]
    }

    #[test]
    fn observe_session_does_only_setup_writes() {
        let mut r = rig();
        let id = open(&mut r, SessionMode::Observe);
        assert_eq!(*r.opened.lock().unwrap(), vec![crate::clasp::identity::tests::iface("282D7FB83C8B")]);
        assert_eq!(*r.link.connects.lock().unwrap(), vec![(ADDR_A, aap::PSM_AACP)]);
        assert_eq!(*r.link.sent.lock().unwrap(), setup_writes());

        let out = r.mgr.set_listening_mode("w1", id, ListeningMode::Transparency);
        assert_eq!(result_for(&out, "w1").unwrap().0, &Outcome::Unsupported);
        std::thread::sleep(Duration::from_millis(30));
        assert!(r.mgr.poll().is_empty());
        assert_eq!(*r.link.sent.lock().unwrap(), setup_writes());
    }

    #[test]
    fn control_session_writes_and_never_observes_its_own_command() {
        let mut r = rig();
        let id = open(&mut r, SessionMode::Control);
        assert!(r.mgr.set_listening_mode("w1", id, ListeningMode::Transparency).is_empty());
        let msgs = poll_until(&mut r.mgr, |m| result_for(m, "w1").is_some());
        assert_eq!(result_for(&msgs, "w1").unwrap().0, &Outcome::Accepted);
        assert!(!msgs.iter().any(|m| matches!(m, EngineMsg::Observation { .. })));
        let mut want = setup_writes();
        want.push(aap::listening_mode_command(ListeningMode::Transparency).to_vec());
        assert_eq!(*r.link.sent.lock().unwrap(), want);

        // Off is not writable in v1.0.
        let out = r.mgr.set_listening_mode("w2", id, ListeningMode::Off);
        assert_eq!(result_for(&out, "w2").unwrap().0, &Outcome::Unsupported);
        std::thread::sleep(Duration::from_millis(30));
        let later = r.mgr.poll();
        assert!(!later.iter().any(|m| matches!(m, EngineMsg::Observation { .. })));
        assert_eq!(r.link.sent.lock().unwrap().len(), 4);
    }

    #[test]
    fn write_failure_is_failed_not_accepted() {
        let mut r = rig();
        let id = open(&mut r, SessionMode::Control);
        r.link.state.lock().unwrap().fail_sends = true;
        r.mgr.set_listening_mode("w1", id, ListeningMode::Adaptive);
        let msgs = poll_until(&mut r.mgr, |m| result_for(m, "w1").is_some());
        let (outcome, detail) = result_for(&msgs, "w1").unwrap();
        assert_eq!(outcome, &Outcome::Failed);
        assert!(detail.as_deref().unwrap().contains("driver write failed"));
    }

    #[test]
    fn stale_session_id_rejected() {
        let mut r = rig();
        let id = open(&mut r, SessionMode::Control);
        let out = r.mgr.set_listening_mode("w1", id + 1, ListeningMode::Transparency);
        assert_eq!(result_for(&out, "w1").unwrap().0, &Outcome::Rejected);
        assert_eq!(result_for(&r.mgr.close("c0", id + 7), "c0").unwrap().0, &Outcome::Rejected);
        let closed = r.mgr.close("c1", id);
        assert_eq!(result_for(&closed, "c1").unwrap().0, &Outcome::Accepted);
        // After close the old id is stale.
        let out = r.mgr.set_listening_mode("w2", id, ListeningMode::Transparency);
        assert_eq!(result_for(&out, "w2").unwrap().0, &Outcome::Rejected);
        assert_eq!(result_for(&r.mgr.close("c2", id), "c2").unwrap().0, &Outcome::Rejected);
        assert_eq!(r.link.sent.lock().unwrap().len(), 3);
    }

    #[test]
    fn inbound_packets_become_observations() {
        let mut r = rig();
        let id = open(&mut r, SessionMode::Observe);
        {
            let mut s = r.link.state.lock().unwrap();
            for hex in ["0400040006000000", "0400040009000d03000000", "0400040004000302014f020104015202010801ff0401"] {
                s.inbound.push_back(Ok(crate::clasp::protocol::tests::unhex(hex)));
            }
        }
        let msgs = poll_until(&mut r.mgr, |m| m.iter().filter(|x| matches!(x, EngineMsg::Observation { .. })).count() == 2);
        let obs: Vec<&Fields> = msgs
            .iter()
            .filter_map(|m| match m {
                EngineMsg::Observation { session_id, device_id, observed_at, fields, .. } => {
                    assert_eq!(*session_id, id);
                    assert_eq!(device_id, &r.dev);
                    assert!(observed_at.ends_with('Z') && observed_at.len() == 24);
                    Some(fields)
                }
                _ => None,
            })
            .collect();
        assert_eq!(obs[0].listening_mode, Some(ObservedMode::Transparency));
        assert_eq!(obs[0].battery_left, None);
        assert_eq!(obs[1].battery_case, Some(BatteryComponent { level: None, charging: None }));
        assert_eq!(obs[1].listening_mode, None);
    }

    #[test]
    fn close_stops_and_closes_the_link() {
        let mut r = rig();
        let id = open(&mut r, SessionMode::Control);
        let out = r.mgr.close("c1", id);
        assert_eq!(result_for(&out, "c1").unwrap().0, &Outcome::Accepted);
        assert!(matches!(out.last(), Some(EngineMsg::Session { state: SessionState::Closed, .. })));
        assert!(*r.link.closed.lock().unwrap());
        // A new session can be opened afterwards (no automatic one was).
        assert_eq!(r.link.connects.lock().unwrap().len(), 1);
    }

    #[test]
    fn status_error_is_lost_and_pending_write_answered() {
        let mut r = rig();
        let id = open(&mut r, SessionMode::Control);
        r.link.state.lock().unwrap().status_error = true;
        let msgs = poll_until(&mut r.mgr, |m| has_state(m, SessionState::Lost));
        match msgs.last().unwrap() {
            EngineMsg::Session { state: SessionState::Lost, reason, session_id, writable_modes, .. } => {
                assert_eq!(*session_id, id);
                assert!(reason.as_deref().unwrap().contains("driver status failed"));
                assert!(writable_modes.is_empty());
            }
            other => panic!("{other:?}"),
        }
        assert!(*r.link.closed.lock().unwrap());
        let out = r.mgr.set_listening_mode("w1", id, ListeningMode::Transparency);
        assert_eq!(result_for(&out, "w1").unwrap().0, &Outcome::Rejected);
        // No reconnect happened.
        std::thread::sleep(Duration::from_millis(50));
        assert_eq!(r.link.connects.lock().unwrap().len(), 1);
    }

    #[test]
    fn link_down_is_lost() {
        let mut r = rig();
        open(&mut r, SessionMode::Observe);
        r.link.state.lock().unwrap().status = Some(LinkStatus { state: 0, address: 0 });
        let msgs = poll_until(&mut r.mgr, |m| has_state(m, SessionState::Lost));
        assert!(format!("{msgs:?}").contains("link down"));
    }

    #[test]
    fn cancel_outstanding_open() {
        let mut r = rig();
        r.link.state.lock().unwrap().connect_delay = Duration::from_millis(300);
        r.mgr.open("open-1", &r.dev.clone(), SessionMode::Control);
        std::thread::sleep(Duration::from_millis(50));
        let out = r.mgr.cancel("x1", "open-1");
        assert_eq!(result_for(&out, "open-1").unwrap().0, &Outcome::Cancelled);
        assert_eq!(result_for(&out, "x1").unwrap().0, &Outcome::Accepted);
        assert!(has_state(&out, SessionState::Closed));
        // While the cancelled worker is still inside connect, the engine is busy.
        let busy = r.mgr.open("open-2", &r.dev.clone(), SessionMode::Control);
        assert!(matches!(&busy[..], [EngineMsg::Error { code: ErrorCode::Busy, .. }]));
        assert!(r.mgr.ctx.wait_drained(Duration::from_secs(5)));
        assert!(*r.link.closed.lock().unwrap());
        assert!(r.link.sent.lock().unwrap().is_empty(), "no setup write after cancel");
        // Cancelling something that is not outstanding is rejected.
        assert_eq!(result_for(&r.mgr.cancel("x2", "open-1"), "x2").unwrap().0, &Outcome::Rejected);
    }

    #[test]
    fn writes_cannot_be_cancelled() {
        let mut r = rig();
        let id = open(&mut r, SessionMode::Control);
        // Hold the worker in recv so the write stays pending briefly.
        r.mgr.set_listening_mode("w1", id, ListeningMode::Transparency);
        let out = r.mgr.cancel("x1", "w1");
        // Either still pending (rejected: writes cannot be cancelled) or already
        // answered (rejected: not outstanding). Never cancelled.
        assert_eq!(result_for(&out, "x1").unwrap().0, &Outcome::Rejected);
    }

    #[test]
    fn open_timeout() {
        let mut t = fast_timings();
        t.open_timeout = Duration::from_millis(50);
        let mut r = rig_with(inventory(), t);
        r.link.state.lock().unwrap().connect_delay = Duration::from_millis(400);
        r.mgr.open("open-1", &r.dev.clone(), SessionMode::Observe);
        let msgs = poll_until(&mut r.mgr, |m| result_for(m, "open-1").is_some());
        assert_eq!(result_for(&msgs, "open-1").unwrap().0, &Outcome::TimedOut);
        assert!(r.mgr.ctx.wait_drained(Duration::from_secs(5)));
        assert!(r.link.sent.lock().unwrap().is_empty());
        assert!(*r.link.closed.lock().unwrap());
    }

    #[test]
    fn identity_failure_opens_nothing() {
        let mut inv = inventory();
        inv.interfaces.push(crate::clasp::identity::tests::iface("282D7FB83C8B"));
        let mut r = rig_with(inv, fast_timings());
        r.mgr.open("open-1", &r.dev.clone(), SessionMode::Control);
        let msgs = poll_until(&mut r.mgr, |m| result_for(m, "open-1").is_some());
        match &msgs[0] {
            EngineMsg::Result { outcome: Outcome::Failed, detail, session_id: Some(None), .. } => {
                assert_eq!(detail.as_deref(), Some("ambiguous: 2 AAP driver interfaces name this device"));
            }
            other => panic!("{other:?}"),
        }
        assert!(has_state(&msgs, SessionState::Closed));
        assert!(r.opened.lock().unwrap().is_empty());
        assert!(r.link.connects.lock().unwrap().is_empty());
    }

    #[test]
    fn connect_failure_and_wrong_device() {
        let mut r = rig();
        r.link.state.lock().unwrap().connect_result = Some(false);
        r.mgr.open("open-1", &r.dev.clone(), SessionMode::Observe);
        let msgs = poll_until(&mut r.mgr, |m| result_for(m, "open-1").is_some());
        assert_eq!(result_for(&msgs, "open-1").unwrap().0, &Outcome::Failed);
        assert!(r.link.sent.lock().unwrap().is_empty());
        assert!(*r.link.closed.lock().unwrap());

        let mut r = rig();
        r.link.state.lock().unwrap().status = Some(LinkStatus { state: 2, address: 0x1111_2222_3333 });
        r.mgr.open("open-1", &r.dev.clone(), SessionMode::Observe);
        let msgs = poll_until(&mut r.mgr, |m| result_for(m, "open-1").is_some());
        let (o, d) = result_for(&msgs, "open-1").unwrap();
        assert_eq!(o, &Outcome::Failed);
        assert!(d.as_deref().unwrap().contains("different device"));
        assert!(r.link.sent.lock().unwrap().is_empty());
    }

    #[test]
    fn one_session_at_a_time() {
        let mut r = rig();
        open(&mut r, SessionMode::Observe);
        let out = r.mgr.open("open-2", &r.dev.clone(), SessionMode::Control);
        assert!(matches!(&out[..], [EngineMsg::Error { code: ErrorCode::Busy, request_id: Some(rid), .. }] if rid == "open-2"));
        assert_eq!(r.link.connects.lock().unwrap().len(), 1);
    }

    #[test]
    fn close_and_shutdown_are_bounded_when_the_driver_blocks() {
        for use_shutdown in [false, true] {
            let mut r = rig();
            let id = open(&mut r, SessionMode::Control);
            r.link.state.lock().unwrap().block = Duration::from_secs(30);
            std::thread::sleep(Duration::from_millis(30)); // worker now inside recv
            let t0 = Instant::now();
            let out = if use_shutdown { r.mgr.shutdown("client disconnected") } else { r.mgr.close("c1", id) };
            assert!(t0.elapsed() < Duration::from_secs(2), "took {:?}", t0.elapsed());
            match out.last() {
                Some(EngineMsg::Session { state: SessionState::Closed, reason, .. }) => {
                    assert!(reason.as_deref().unwrap().contains("driver call still finishing"), "{reason:?}");
                }
                other => panic!("{other:?}"),
            }
            if !use_shutdown {
                assert_eq!(result_for(&out, "c1").unwrap().0, &Outcome::Accepted);
            }
            // The worker is still inside the driver call: the engine is busy.
            let busy = r.mgr.open("open-2", &r.dev.clone(), SessionMode::Control);
            assert!(matches!(&busy[..], [EngineMsg::Error { code: ErrorCode::Busy, .. }]));
            assert_eq!(r.link.connects.lock().unwrap().len(), 1);
        }
    }

    #[test]
    fn queued_write_is_never_sent_after_close() {
        let mut r = rig();
        let id = open(&mut r, SessionMode::Control);
        r.link.state.lock().unwrap().block = Duration::from_millis(800);
        std::thread::sleep(Duration::from_millis(30)); // worker inside a blocking recv
        assert!(r.mgr.set_listening_mode("wa", id, ListeningMode::Transparency).is_empty());
        // Wait until write A is inside the (blocking) driver send.
        let deadline = Instant::now() + Duration::from_secs(5);
        while r.mgr.active.as_ref().and_then(in_flight_snapshot).is_none() {
            assert!(Instant::now() < deadline, "write A never started");
            std::thread::sleep(Duration::from_millis(5));
        }
        assert!(r.mgr.set_listening_mode("wb", id, ListeningMode::Adaptive).is_empty());
        let out = r.mgr.close("c1", id);
        let (oa, da) = result_for(&out, "wa").unwrap();
        assert_eq!(oa, &Outcome::TimedOut);
        assert_eq!(da.as_deref(), Some(WRITE_UNCERTAIN));
        assert!(da.as_deref().unwrap().contains("may have reached"));
        let (ob, db) = result_for(&out, "wb").unwrap();
        assert_eq!(ob, &Outcome::Failed);
        assert!(db.as_deref().unwrap().contains("before the write was sent"));
        assert_eq!(result_for(&out, "c1").unwrap().0, &Outcome::Accepted);
        // Let the stuck worker finish: B must never reach the driver.
        assert!(r.mgr.ctx.wait_drained(Duration::from_secs(10)));
        let sent = r.link.sent.lock().unwrap().clone();
        let a = aap::listening_mode_command(ListeningMode::Transparency).to_vec();
        let b = aap::listening_mode_command(ListeningMode::Adaptive).to_vec();
        assert!(!sent.contains(&b), "write B was sent after close");
        assert!(sent.iter().filter(|p| **p == a).count() <= 1);
        assert_eq!(sent.len(), 4, "setup writes + at most write A");
        assert!(*r.link.closed.lock().unwrap());
    }

    #[test]
    fn draining_worker_frees_the_engine_when_it_exits() {
        let mut r = rig();
        let id = open(&mut r, SessionMode::Control);
        r.link.state.lock().unwrap().block = Duration::from_millis(800);
        std::thread::sleep(Duration::from_millis(30));
        let out = r.mgr.close("c1", id);
        assert!(format!("{out:?}").contains("driver call still finishing"));
        let busy = r.mgr.open("open-2", &r.dev.clone(), SessionMode::Control);
        assert!(matches!(&busy[..], [EngineMsg::Error { code: ErrorCode::Busy, .. }]));
        assert!(r.mgr.ctx.wait_drained(Duration::from_secs(5)));
        assert!(*r.link.closed.lock().unwrap());
        r.link.state.lock().unwrap().block = Duration::ZERO;
        let again = r.mgr.open("open-3", &r.dev.clone(), SessionMode::Control);
        assert!(matches!(&again[..], [EngineMsg::Session { state: SessionState::Opening, .. }]));
    }

    #[test]
    fn not_connected_device_is_never_opened() {
        for connected in [Some(false), None] {
            let mut inv = inventory();
            inv.paired[0].connected = connected;
            let ctx = Arc::new(EngineCtx::new(
                Arc::new(FakeSource(inv)),
                Arc::new(PanicOpener),
                SALT,
                fast_timings(),
            ));
            let mut mgr = SessionManager::new(ctx);
            mgr.open("open-1", &device_id(&SALT, ADDR_A).unwrap(), SessionMode::Observe);
            let msgs = poll_until(&mut mgr, |m| result_for(m, "open-1").is_some());
            let (o, d) = result_for(&msgs, "open-1").unwrap();
            assert_eq!(o, &Outcome::Failed);
            assert!(d.as_deref().unwrap().starts_with("not_connected:"), "{d:?}");
        }
    }

    // ---- protocol 1.1: Conversation Awareness and ear ----

    fn rig11() -> Rig {
        let mut r = rig();
        r.mgr.set_minor(1);
        r
    }

    fn push_inbound(r: &Rig, hex: &str) {
        r.link.state.lock().unwrap().inbound.push_back(Ok(crate::clasp::protocol::tests::unhex(hex)));
    }

    const CA_ON: &str = "0400040009002801000000";
    const CA_OFF: &str = "0400040009002802000000";
    const CA_ODD: &str = "0400040009002807000000";

    fn observations(msgs: &[EngineMsg]) -> Vec<&Fields> {
        msgs.iter()
            .filter_map(|m| match m {
                EngineMsg::Observation { fields, .. } => Some(fields),
                _ => None,
            })
            .collect()
    }

    fn last_settings(msgs: &[EngineMsg]) -> Option<Vec<WritableSetting>> {
        msgs.iter().rev().find_map(|m| match m {
            EngineMsg::Session { state: SessionState::Open, writable_settings, .. } => Some(writable_settings.clone().unwrap()),
            _ => None,
        })
    }

    fn ca_writes(r: &Rig) -> usize {
        let on = aap::conversation_awareness_command(true).to_vec();
        let off = aap::conversation_awareness_command(false).to_vec();
        r.link.sent.lock().unwrap().iter().filter(|p| **p == on || **p == off).count()
    }

    #[test]
    fn v10_session_has_no_11_fields() {
        let mut r = rig();
        let first = r.mgr.open("open-1", &r.dev.clone(), SessionMode::Control);
        assert!(matches!(&first[..], [EngineMsg::Session { writable_settings: None, .. }]));
        let msgs = poll_until(&mut r.mgr, |m| has_state(m, SessionState::Open));
        assert!(msgs.iter().all(|m| !matches!(m, EngineMsg::Session { writable_settings: Some(_), .. })));
        // CA and ear packets produce nothing for a 1.0 connection; the mode still does.
        for hex in [CA_ON, "0400040006000001", "0400040009000d02000000"] {
            push_inbound(&r, hex);
        }
        let msgs = poll_until(&mut r.mgr, |m| !observations(m).is_empty());
        std::thread::sleep(Duration::from_millis(30));
        let mut all = msgs;
        all.extend(r.mgr.poll());
        let obs = observations(&all);
        assert_eq!(obs.len(), 1, "{all:?}");
        assert_eq!(obs[0].listening_mode, Some(crate::clasp::protocol::ObservedMode::NoiseCancellation));
        assert!(all.iter().all(|m| !matches!(m, EngineMsg::Session { .. })), "no session update for 1.0");
        let json = serde_json::to_string(&all).unwrap();
        assert!(!json.contains("conversation_awareness") && !json.contains("\"ear\"") && !json.contains("writable_settings"));
    }

    #[test]
    fn v11_session_reports_ca_and_ear() {
        let mut r = rig11();
        let id = open(&mut r, SessionMode::Observe);
        for hex in ["0400040006000001", "0400040006000104", CA_OFF] {
            push_inbound(&r, hex);
        }
        let msgs = poll_until(&mut r.mgr, |m| observations(m).len() == 2);
        let obs = observations(&msgs);
        assert_eq!(
            obs[0].ear,
            Some(crate::clasp::protocol::Ear {
                primary: crate::clasp::protocol::EarState::InEar,
                secondary: crate::clasp::protocol::EarState::OutOfEar
            })
        );
        assert_eq!(obs[1].conversation_awareness, Some(ConversationAwareness::Off));
        // Observe session: CA observed, but nothing becomes writable and writes are refused.
        let out = r.mgr.set_conversation_awareness("w1", id, true);
        assert_eq!(result_for(&out, "w1").unwrap().0, &Outcome::Unsupported);
        assert_eq!(ca_writes(&r), 0);
    }

    #[test]
    fn ca_write_refused_before_observation() {
        let mut r = rig11();
        let id = open(&mut r, SessionMode::Control);
        let out = r.mgr.set_conversation_awareness("w1", id, false);
        let (o, d) = result_for(&out, "w1").unwrap();
        assert_eq!(o, &Outcome::Unsupported);
        assert_eq!(d.as_deref(), Some(CA_NOT_OBSERVED));
        // Exactly the fixture's reply (no session_id field).
        let fixture = crate::clasp::protocol::tests::load_fixtures("messages")
            .into_iter()
            .find(|(n, _)| n == "result_unsupported_ca_not_observed.json")
            .unwrap()
            .1;
        let mut got = serde_json::to_value(&out[0]).unwrap();
        got["seq"] = fixture["message"]["seq"].clone();
        got["request_id"] = fixture["message"]["request_id"].clone();
        assert_eq!(got, fixture["message"]);
        // An unrecognised CA value is reported but does not count as observed.
        push_inbound(&r, CA_ODD);
        let msgs = poll_until(&mut r.mgr, |m| !observations(m).is_empty());
        assert_eq!(observations(&msgs)[0].conversation_awareness, Some(ConversationAwareness::Unknown));
        assert!(!msgs.iter().any(|m| matches!(m, EngineMsg::Session { .. })));
        let out = r.mgr.set_conversation_awareness("w2", id, true);
        assert_eq!(result_for(&out, "w2").unwrap().1.as_deref(), Some(CA_NOT_OBSERVED));
        // Stale id and nothing written.
        let out = r.mgr.set_conversation_awareness("w3", id + 1, true);
        assert_eq!(result_for(&out, "w3").unwrap().0, &Outcome::Rejected);
        std::thread::sleep(Duration::from_millis(30));
        r.mgr.poll();
        assert_eq!(*r.link.sent.lock().unwrap(), setup_writes());
    }

    #[test]
    fn ca_write_overlap_and_unconfirmed_ack_are_refused() {
        let mut r = rig11();
        let id = open(&mut r, SessionMode::Control);
        push_inbound(&r, CA_OFF);
        let msgs = poll_until(&mut r.mgr, |m| last_settings(m).is_some_and(|s| s.len() == 2));
        assert_eq!(
            last_settings(&msgs).unwrap(),
            vec![WritableSetting::ListeningMode, WritableSetting::ConversationAwareness]
        );

        assert!(r.mgr.set_conversation_awareness("w1", id, true).is_empty());
        // While w1 is pending, listening mode is not advertised as writable (review EF-REVIEW P3)...
        let msgs = poll_until(&mut r.mgr, |m| last_settings(m).is_some());
        assert_eq!(last_settings(&msgs).unwrap(), Vec::<WritableSetting>::new());
        // ...and a second CA write and a listening-mode write are refused.
        let out = r.mgr.set_conversation_awareness("w2", id, false);
        assert_eq!(result_for(&out, "w2"), Some((&Outcome::Rejected, &Some(CA_WRITE_IN_FLIGHT.to_string()))));
        let out = r.mgr.set_listening_mode("m1", id, ListeningMode::Transparency);
        assert_eq!(result_for(&out, "m1").unwrap().0, &Outcome::Rejected);

        let msgs = poll_until(&mut r.mgr, |m| result_for(m, "w1").is_some());
        // The ack is the result; the command itself is never an observation.
        assert_eq!(result_for(&msgs, "w1").unwrap().0, &Outcome::Accepted);
        assert!(observations(&msgs).is_empty());
        assert_eq!(last_settings(&msgs).unwrap(), vec![WritableSetting::ListeningMode]);
        assert_eq!(ca_writes(&r), 1);
        assert_eq!(r.link.sent.lock().unwrap().last().unwrap(), &aap::conversation_awareness_command(true).to_vec());

        // Acknowledged but not read back: still reserved, even after a while.
        std::thread::sleep(Duration::from_millis(40));
        r.mgr.poll();
        let out = r.mgr.set_conversation_awareness("w3", id, true);
        assert_eq!(result_for(&out, "w3"), Some((&Outcome::Rejected, &Some(CA_WRITE_UNCONFIRMED.to_string()))));
        // A non-matching readback does not release it either.
        push_inbound(&r, CA_OFF);
        poll_until(&mut r.mgr, |m| !observations(m).is_empty());
        let out = r.mgr.set_conversation_awareness("w4", id, true);
        assert_eq!(result_for(&out, "w4").unwrap().0, &Outcome::Rejected);
        // Listening-mode writes are not blocked by an acknowledged CA write.
        assert!(r.mgr.set_listening_mode("m2", id, ListeningMode::Adaptive).is_empty());
        poll_until(&mut r.mgr, |m| result_for(m, "m2").is_some());

        // A matching readback after the ack releases the reservation.
        push_inbound(&r, CA_ON);
        let msgs = poll_until(&mut r.mgr, |m| last_settings(m).is_some_and(|s| s.len() == 2));
        assert_eq!(observations(&msgs)[0].conversation_awareness, Some(ConversationAwareness::On));
        assert!(r.mgr.set_conversation_awareness("w5", id, false).is_empty());
        let msgs = poll_until(&mut r.mgr, |m| result_for(m, "w5").is_some());
        assert_eq!(result_for(&msgs, "w5").unwrap().0, &Outcome::Accepted);
        assert_eq!(ca_writes(&r), 2);
    }

    #[test]
    fn ca_write_failure_stays_uncertain_for_the_session() {
        let mut r = rig11();
        let id = open(&mut r, SessionMode::Control);
        push_inbound(&r, CA_OFF);
        poll_until(&mut r.mgr, |m| !observations(m).is_empty());
        r.link.state.lock().unwrap().fail_sends = true;
        assert!(r.mgr.set_conversation_awareness("w1", id, true).is_empty());
        let msgs = poll_until(&mut r.mgr, |m| result_for(m, "w1").is_some());
        assert_eq!(result_for(&msgs, "w1").unwrap().0, &Outcome::Failed);
        r.link.state.lock().unwrap().fail_sends = false;
        // Even a matching readback does not clear an uncertain write.
        push_inbound(&r, CA_ON);
        poll_until(&mut r.mgr, |m| !observations(m).is_empty());
        let out = r.mgr.set_conversation_awareness("w2", id, true);
        assert_eq!(result_for(&out, "w2"), Some((&Outcome::Rejected, &Some(CA_WRITE_UNCERTAIN.to_string()))));
        // Only a new session clears it.
        r.mgr.close("c1", id);
        *r.link.closed.lock().unwrap() = false; // the fake shares one link across sessions
        let id2 = {
            let first = r.mgr.open("open-2", &r.dev.clone(), SessionMode::Control);
            assert!(matches!(&first[..], [EngineMsg::Session { state: SessionState::Opening, .. }]));
            let msgs = poll_until(&mut r.mgr, |m| has_state(m, SessionState::Open));
            match msgs.last() {
                Some(EngineMsg::Session { session_id, .. }) => *session_id,
                other => panic!("{other:?}"),
            }
        };
        // ...and the new session must observe CA again before writing.
        let out = r.mgr.set_conversation_awareness("w3", id2, true);
        assert_eq!(result_for(&out, "w3").unwrap().1.as_deref(), Some(CA_NOT_OBSERVED));
    }

    #[test]
    fn ca_write_in_flight_at_close_is_uncertain() {
        let mut r = rig11();
        let id = open(&mut r, SessionMode::Control);
        push_inbound(&r, CA_OFF);
        poll_until(&mut r.mgr, |m| !observations(m).is_empty());
        r.link.state.lock().unwrap().block = Duration::from_millis(800);
        std::thread::sleep(Duration::from_millis(30));
        assert!(r.mgr.set_conversation_awareness("w1", id, true).is_empty());
        let deadline = Instant::now() + Duration::from_secs(5);
        while r.mgr.active.as_ref().and_then(in_flight_snapshot).is_none() {
            assert!(Instant::now() < deadline, "CA write never started");
            std::thread::sleep(Duration::from_millis(5));
        }
        let out = r.mgr.close("c1", id);
        assert_eq!(result_for(&out, "w1"), Some((&Outcome::TimedOut, &Some(WRITE_UNCERTAIN.to_string()))));
        assert!(r.mgr.ctx.wait_drained(Duration::from_secs(10)));
    }

    #[test]
    fn shutdown_closes_the_session() {
        let mut r = rig();
        open(&mut r, SessionMode::Control);
        let out = r.mgr.shutdown("client disconnected");
        assert!(has_state(&out, SessionState::Closed));
        assert!(*r.link.closed.lock().unwrap());
    }
}

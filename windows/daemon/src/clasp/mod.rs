//! Restricted Clasp engine (protocol `windows/protocol/clasp-engine-v1`).
//!
//! Contains only: framing/protocol, the local pipe server, read-only device
//! listing + identity, the guarded driver session and AAP observation decoding.
//! It does not compile or call any upstream mic/ELD, heart-rate, hearing, GATT,
//! rename, media, volume, A2DP, LE, devnode, audio-toggling, scheduled-task or
//! auto-connect code.

pub mod aap;
pub mod devices;
pub mod identity;
pub mod link;
pub mod log;
pub mod pipe;
pub mod protocol;
pub mod server;
pub mod session;
pub mod util;

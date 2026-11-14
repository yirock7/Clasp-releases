//! Minimal append-only engine log. Callers log `device_id`s only, never Bluetooth
//! addresses, serial numbers or driver interface paths (which contain addresses).

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use super::util::utc_now_iso_ms;

static LOG: Mutex<Option<File>> = Mutex::new(None);
static ECHO: Mutex<bool> = Mutex::new(false);

pub const LOG_FILE: &str = "engine.log";

/// Open (append) `<dir>/engine.log`. `echo` also writes each line to stderr.
pub fn init(dir: &Path, echo: bool) -> std::io::Result<PathBuf> {
    std::fs::create_dir_all(dir)?;
    let path = dir.join(LOG_FILE);
    let f = OpenOptions::new().create(true).append(true).open(&path)?;
    *LOG.lock().unwrap_or_else(|e| e.into_inner()) = Some(f);
    *ECHO.lock().unwrap_or_else(|e| e.into_inner()) = echo;
    Ok(path)
}

pub fn info(msg: &str) {
    let line = format!("{} {}", utc_now_iso_ms(), msg);
    if let Some(f) = LOG.lock().unwrap_or_else(|e| e.into_inner()).as_mut() {
        let _ = writeln!(f, "{line}");
    }
    if *ECHO.lock().unwrap_or_else(|e| e.into_inner()) {
        eprintln!("{line}");
    }
}

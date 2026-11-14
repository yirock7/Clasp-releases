//! `clasp-engine`: the restricted AAP engine for Clasp.
//!
//! ```text
//! clasp-engine --serve [--allowed-client <path>] [--log-dir <dir>]
//! clasp-engine --list-devices
//! clasp-engine --version
//! ```
//!
//! Build: `cargo build --release --bin clasp-engine`.

#[path = "../clasp/mod.rs"]
mod clasp;

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant};

use windows_sys::Win32::System::Console::{
    SetConsoleCtrlHandler, CTRL_BREAK_EVENT, CTRL_CLOSE_EVENT, CTRL_C_EVENT, CTRL_LOGOFF_EVENT,
    CTRL_SHUTDOWN_EVENT,
};

use clasp::devices::WindowsDevices;
use clasp::identity::{self, DeviceSource};
use clasp::link::DriverOpener;
use clasp::protocol::{MAJOR, MINOR, PROTOCOL};
use clasp::server::Engine;
use clasp::session::{EngineCtx, Timings};
use clasp::{log, pipe, util};

const USAGE: &str = "usage:
  clasp-engine --serve [--allowed-client <path>] [--log-dir <dir>]
  clasp-engine --list-devices
  clasp-engine --version";

enum Command {
    Serve { allowed_client: Option<String>, log_dir: Option<PathBuf> },
    ListDevices,
    Version,
}

fn parse_args(args: &[String]) -> Result<Command, String> {
    let mut command: Option<&str> = None;
    let mut allowed_client = None;
    let mut log_dir = None;
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            c @ ("--serve" | "--list-devices" | "--version") => {
                if command.replace(c).is_some() {
                    return Err("only one of --serve, --list-devices, --version".into());
                }
            }
            "--allowed-client" => {
                i += 1;
                allowed_client = Some(args.get(i).ok_or("--allowed-client needs a path")?.clone());
            }
            "--log-dir" => {
                i += 1;
                log_dir = Some(PathBuf::from(args.get(i).ok_or("--log-dir needs a directory")?));
            }
            other => return Err(format!("unknown argument {other:?}")),
        }
        i += 1;
    }
    match command {
        Some("--serve") => Ok(Command::Serve { allowed_client, log_dir }),
        Some(c) if allowed_client.is_some() || log_dir.is_some() => {
            Err(format!("--allowed-client and --log-dir only apply to --serve, not {c}"))
        }
        Some("--list-devices") => Ok(Command::ListDevices),
        Some(_) => Ok(Command::Version),
        None => Err("no command given".into()),
    }
}

/// `%LOCALAPPDATA%\Clasp\engine`.
fn data_dir() -> Result<PathBuf, String> {
    let base = std::env::var_os("LOCALAPPDATA").ok_or("LOCALAPPDATA is not set")?;
    Ok(PathBuf::from(base).join("Clasp").join("engine"))
}

static SHUTDOWN: OnceLock<Arc<AtomicBool>> = OnceLock::new();
static STOPPED: AtomicBool = AtomicBool::new(false);

unsafe extern "system" fn on_console_event(kind: u32) -> i32 {
    match kind {
        CTRL_C_EVENT | CTRL_BREAK_EVENT | CTRL_CLOSE_EVENT | CTRL_LOGOFF_EVENT | CTRL_SHUTDOWN_EVENT => {
            if let Some(flag) = SHUTDOWN.get() {
                flag.store(true, Ordering::SeqCst);
            }
            if kind != CTRL_C_EVENT && kind != CTRL_BREAK_EVENT {
                // The process is terminated when this returns: give the session
                // a moment to close the driver handle cleanly first.
                let deadline = Instant::now() + Duration::from_millis(4500);
                while !STOPPED.load(Ordering::SeqCst) && Instant::now() < deadline {
                    std::thread::sleep(Duration::from_millis(20));
                }
            }
            1
        }
        _ => 0,
    }
}

fn serve(allowed_client: Option<String>, log_dir: Option<PathBuf>) -> Result<(), String> {
    let data = data_dir()?;
    let log_dir = log_dir.unwrap_or_else(|| data.clone());
    let log_path = log::init(&log_dir, true).map_err(|e| format!("cannot open log in {}: {e}", log_dir.display()))?;
    log::info(&format!("{} starting (protocol {PROTOCOL} {MAJOR}.{MINOR}), log {}", Engine::engine_name(), log_path.display()));

    let salt = identity::load_or_create_salt(&data).map_err(|e| format!("device salt: {e}"))?;
    let epoch = util::new_uuid_v4().map_err(|e| format!("engine epoch: {e}"))?;
    let shutdown = Arc::new(AtomicBool::new(false));
    let _ = SHUTDOWN.set(shutdown.clone());
    if unsafe { SetConsoleCtrlHandler(Some(on_console_event), 1) } == 0 {
        log::info("warning: could not install the console control handler");
    }

    let sid = pipe::current_user_sid().map_err(|e| format!("cannot read the user SID: {e}"))?;
    let name = pipe::pipe_name(&sid, None);
    let server = pipe::PipeServer::create(&name, &sid).map_err(|e| format!("cannot create the pipe: {e}"))?;
    if let Some(a) = &allowed_client {
        log::info(&format!("only client image allowed: {a}"));
    }
    log::info(&format!("engine epoch {epoch}"));

    let ctx = EngineCtx::new(Arc::new(WindowsDevices), Arc::new(DriverOpener), salt, Timings::default());
    let engine = Engine::new(Arc::new(ctx), epoch, shutdown);
    let result = pipe::run(&server, &engine, allowed_client.as_deref());
    if !engine.ctx.wait_drained(Duration::from_secs(15)) {
        log::info("warning: a session worker was still inside a driver call at exit");
    }
    drop(server);
    log::info(&format!("stopped{}", result.as_ref().err().map(|e| format!(": {e}")).unwrap_or_default()));
    STOPPED.store(true, Ordering::SeqCst);
    result.map_err(|e| format!("pipe server failed: {e}"))
}

fn list_devices() -> Result<(), String> {
    let data = data_dir()?;
    let salt = identity::load_or_create_salt(&data).map_err(|e| format!("device salt: {e}"))?;
    let inv = WindowsDevices.inventory().map_err(|e| format!("device enumeration failed: {e}"))?;
    let devices = identity::list_devices(&inv, &salt).map_err(|e| format!("device ids: {e}"))?;
    let json = serde_json::to_string_pretty(&devices).map_err(|e| e.to_string())?;
    println!("{json}");
    Ok(())
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let command = match parse_args(&args) {
        Ok(c) => c,
        Err(e) => {
            eprintln!("clasp-engine: {e}");
            eprintln!("{USAGE}");
            std::process::exit(2);
        }
    };
    let result = match command {
        Command::Version => {
            println!("{} (protocol {PROTOCOL} {MAJOR}.{MINOR})", Engine::engine_name());
            Ok(())
        }
        Command::ListDevices => list_devices(),
        Command::Serve { allowed_client, log_dir } => serve(allowed_client, log_dir),
    };
    if let Err(e) = result {
        log::info(&format!("fatal: {e}"));
        eprintln!("clasp-engine: {e}");
        std::process::exit(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn args(a: &[&str]) -> Vec<String> {
        a.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn cli_parsing() {
        assert!(matches!(parse_args(&args(&["--version"])), Ok(Command::Version)));
        assert!(matches!(parse_args(&args(&["--list-devices"])), Ok(Command::ListDevices)));
        match parse_args(&args(&["--serve", "--allowed-client", "C:/x.exe", "--log-dir", "D:/l"])) {
            Ok(Command::Serve { allowed_client, log_dir }) => {
                assert_eq!(allowed_client.as_deref(), Some("C:/x.exe"));
                assert_eq!(log_dir, Some(PathBuf::from("D:/l")));
            }
            _ => panic!(),
        }
        assert!(parse_args(&args(&[])).is_err());
        assert!(parse_args(&args(&["--serve", "--version"])).is_err());
        assert!(parse_args(&args(&["--serve", "--allowed-client"])).is_err());
        assert!(parse_args(&args(&["--list-devices", "--log-dir", "x"])).is_err());
        assert!(parse_args(&args(&["--shutdown"])).is_err());
    }
}

//! The engine's local pipe endpoint (protocol section 1).
//!
//! - name `Clasp.Engine.v1.<SID>` (SID of the process token's user);
//! - DACL `D:P(A;;GA;;;<SID>)` only;
//! - `PIPE_REJECT_REMOTE_CLIENTS`, one instance, `FILE_FLAG_FIRST_PIPE_INSTANCE`;
//! - one client at a time; the instance is reused (`DisconnectNamedPipe`);
//! - the client's process image is logged, and with `--allowed-client` any other
//!   image is refused before anything is read or sent.
//!
//! The handle is overlapped so accept, read and write all stay responsive to
//! shutdown; reads only take what `PeekNamedPipe` says is available.

use std::io;
use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, LocalFree, ERROR_ACCESS_DENIED, ERROR_BROKEN_PIPE, ERROR_IO_PENDING,
    ERROR_NO_DATA, ERROR_PIPE_BUSY, ERROR_PIPE_CONNECTED, ERROR_PIPE_NOT_CONNECTED, HANDLE,
    INVALID_HANDLE_VALUE, WAIT_OBJECT_0,
};
use windows_sys::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
};
use windows_sys::Win32::Security::{
    GetTokenInformation, TokenUser, PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES, TOKEN_QUERY, TOKEN_USER,
};
use windows_sys::Win32::Storage::FileSystem::{
    ReadFile, WriteFile, FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, PIPE_ACCESS_DUPLEX,
};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, GetNamedPipeClientProcessId,
    PeekNamedPipe, PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE, PIPE_WAIT,
};
use windows_sys::Win32::System::Threading::{
    CreateEventW, GetCurrentProcess, OpenProcess, OpenProcessToken, QueryFullProcessImageNameW,
    WaitForSingleObject, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows_sys::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};

use super::log;
use super::server::{serve_connection, Engine, ReadOutcome, Transport};
use super::util::{bs, from_wide, wide};

pub const PIPE_NAME_PREFIX: &str = "Clasp.Engine.v1.";
const BUFFER_SIZE: u32 = 4 + 65_536;
const WRITE_TIMEOUT: Duration = Duration::from_secs(5);
const LINGER: Duration = Duration::from_secs(2);

/// `Clasp.Engine.v1.<SID>`, plus an optional suffix that only tests use so they
/// never collide with a real engine.
pub fn pipe_name(sid: &str, test_suffix: Option<&str>) -> String {
    match test_suffix {
        Some(s) => format!("{PIPE_NAME_PREFIX}{sid}.{s}"),
        None => format!("{PIPE_NAME_PREFIX}{sid}"),
    }
}

/// The full local pipe path for `name`.
pub fn pipe_path(name: &str) -> String {
    let b = bs();
    format!("{b}{b}.{b}pipe{b}{name}")
}

/// Protected DACL, generic-all for that one user, nothing else.
pub fn sddl_for(sid: &str) -> String {
    format!("D:P(A;;GA;;;{sid})")
}

struct OwnedHandle(HANDLE);
// Kernel handles are not tied to a thread; each is used by one thread at a time.
unsafe impl Send for OwnedHandle {}
impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if !self.0.is_null() && self.0 != INVALID_HANDLE_VALUE {
            unsafe { CloseHandle(self.0) };
        }
    }
}

/// String SID of the user in this process's token.
pub fn current_user_sid() -> io::Result<String> {
    unsafe {
        let mut token: HANDLE = ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return Err(io::Error::last_os_error());
        }
        let token = OwnedHandle(token);
        let mut len = 0u32;
        GetTokenInformation(token.0, TokenUser, ptr::null_mut(), 0, &mut len);
        if len == 0 {
            return Err(io::Error::last_os_error());
        }
        let mut buf = vec![0u64; (len as usize).div_ceil(8)];
        if GetTokenInformation(token.0, TokenUser, buf.as_mut_ptr().cast(), len, &mut len) == 0 {
            return Err(io::Error::last_os_error());
        }
        let user = &*(buf.as_ptr() as *const TOKEN_USER);
        let mut s: *mut u16 = ptr::null_mut();
        if ConvertSidToStringSidW(user.User.Sid, &mut s) == 0 {
            return Err(io::Error::last_os_error());
        }
        let mut n = 0;
        while *s.add(n) != 0 {
            n += 1;
        }
        let sid = String::from_utf16_lossy(std::slice::from_raw_parts(s, n));
        LocalFree(s.cast());
        Ok(sid)
    }
}

/// Full image path of a process, if it can be queried.
pub fn process_image(pid: u32) -> Option<String> {
    unsafe {
        let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if h.is_null() {
            return None;
        }
        let h = OwnedHandle(h);
        let mut buf = [0u16; 1024];
        let mut len = buf.len() as u32;
        if QueryFullProcessImageNameW(h.0, PROCESS_NAME_WIN32, buf.as_mut_ptr(), &mut len) == 0 {
            return None;
        }
        Some(from_wide(&buf[..len as usize]))
    }
}

/// Case-insensitive full-path comparison (forward slashes count as separators).
pub fn same_image(a: &str, b: &str) -> bool {
    let norm = |s: &str| s.replace('/', &bs().to_string()).to_lowercase();
    norm(a) == norm(b)
}

pub struct PipeServer {
    handle: OwnedHandle,
    event: OwnedHandle,
    name: String,
}

impl PipeServer {
    /// Create the single pipe instance. Fails if the name already exists.
    pub fn create(name: &str, sid: &str) -> io::Result<PipeServer> {
        unsafe {
            let sddl = wide(&sddl_for(sid));
            let mut sd: PSECURITY_DESCRIPTOR = ptr::null_mut();
            if ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                SDDL_REVISION_1,
                &mut sd,
                ptr::null_mut(),
            ) == 0
            {
                return Err(io::Error::last_os_error());
            }
            let sa = SECURITY_ATTRIBUTES {
                nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
                lpSecurityDescriptor: sd,
                bInheritHandle: 0,
            };
            let path = wide(&pipe_path(name));
            let h = CreateNamedPipeW(
                path.as_ptr(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1,
                BUFFER_SIZE,
                BUFFER_SIZE,
                0,
                &sa,
            );
            let err = GetLastError();
            LocalFree(sd);
            if h == INVALID_HANDLE_VALUE {
                if err == ERROR_ACCESS_DENIED || err == ERROR_PIPE_BUSY {
                    return Err(io::Error::new(
                        io::ErrorKind::AddrInUse,
                        format!("pipe {name} already exists (another engine, or a squatter, owns the name)"),
                    ));
                }
                return Err(io::Error::from_raw_os_error(err as i32));
            }
            let handle = OwnedHandle(h);
            let ev = CreateEventW(ptr::null(), 1, 0, ptr::null());
            if ev.is_null() {
                return Err(io::Error::last_os_error());
            }
            Ok(PipeServer { handle, event: OwnedHandle(ev), name: name.to_string() })
        }
    }

    fn overlapped(&self) -> OVERLAPPED {
        let mut ov: OVERLAPPED = unsafe { std::mem::zeroed() };
        ov.hEvent = self.event.0;
        ov
    }

    /// Wait for a client. `Ok(false)` if `shutdown` was set first.
    pub fn accept(&self, shutdown: &AtomicBool) -> io::Result<bool> {
        unsafe {
            let mut ov = self.overlapped();
            if ConnectNamedPipe(self.handle.0, &mut ov) != 0 {
                return Ok(true);
            }
            match GetLastError() {
                ERROR_PIPE_CONNECTED => return Ok(true),
                ERROR_IO_PENDING => {}
                e => return Err(io::Error::from_raw_os_error(e as i32)),
            }
            loop {
                if WaitForSingleObject(self.event.0, 100) == WAIT_OBJECT_0 {
                    let mut n = 0u32;
                    if GetOverlappedResult(self.handle.0, &ov, &mut n, 0) == 0 {
                        return Err(io::Error::last_os_error());
                    }
                    return Ok(true);
                }
                if shutdown.load(Ordering::SeqCst) {
                    CancelIoEx(self.handle.0, &ov);
                    let mut n = 0u32;
                    GetOverlappedResult(self.handle.0, &ov, &mut n, 1);
                    return Ok(false);
                }
            }
        }
    }

    pub fn client_pid(&self) -> Option<u32> {
        let mut pid = 0u32;
        (unsafe { GetNamedPipeClientProcessId(self.handle.0, &mut pid) } != 0).then_some(pid)
    }

    /// Give the client a moment to read what was sent and close its end, then
    /// disconnect so the instance can be reused.
    pub fn disconnect(&self) {
        let deadline = Instant::now() + LINGER;
        let mut t = PipeTransport { server: self };
        let mut scratch = [0u8; 4096];
        while Instant::now() < deadline {
            match t.read_some(&mut scratch) {
                Ok(ReadOutcome::Nothing) => std::thread::sleep(Duration::from_millis(10)),
                Ok(ReadOutcome::Data(_)) => {}
                _ => break,
            }
        }
        unsafe { DisconnectNamedPipe(self.handle.0) };
    }
}

/// Byte transport over the connected pipe instance.
pub struct PipeTransport<'a> {
    server: &'a PipeServer,
}

fn is_disconnect(err: u32) -> bool {
    err == ERROR_BROKEN_PIPE || err == ERROR_PIPE_NOT_CONNECTED || err == ERROR_NO_DATA
}

impl Transport for PipeTransport<'_> {
    fn read_some(&mut self, buf: &mut [u8]) -> io::Result<ReadOutcome> {
        unsafe {
            let h = self.server.handle.0;
            let mut avail = 0u32;
            if PeekNamedPipe(h, ptr::null_mut(), 0, ptr::null_mut(), &mut avail, ptr::null_mut()) == 0 {
                let e = GetLastError();
                return if is_disconnect(e) { Ok(ReadOutcome::Eof) } else { Err(io::Error::from_raw_os_error(e as i32)) };
            }
            if avail == 0 {
                return Ok(ReadOutcome::Nothing);
            }
            let want = (avail as usize).min(buf.len()) as u32;
            let mut ov = self.server.overlapped();
            let mut n = 0u32;
            if ReadFile(h, buf.as_mut_ptr(), want, &mut n, &mut ov) == 0 {
                let e = GetLastError();
                if e != ERROR_IO_PENDING {
                    return if is_disconnect(e) { Ok(ReadOutcome::Eof) } else { Err(io::Error::from_raw_os_error(e as i32)) };
                }
            }
            // The bytes are already in the pipe, so this completes at once.
            if GetOverlappedResult(h, &ov, &mut n, 1) == 0 {
                let e = GetLastError();
                return if is_disconnect(e) { Ok(ReadOutcome::Eof) } else { Err(io::Error::from_raw_os_error(e as i32)) };
            }
            Ok(if n == 0 { ReadOutcome::Nothing } else { ReadOutcome::Data(n as usize) })
        }
    }

    fn write_all(&mut self, mut data: &[u8]) -> io::Result<()> {
        let h = self.server.handle.0;
        let deadline = Instant::now() + WRITE_TIMEOUT;
        while !data.is_empty() {
            unsafe {
                let mut ov = self.server.overlapped();
                let mut n = 0u32;
                if WriteFile(h, data.as_ptr(), data.len() as u32, &mut n, &mut ov) == 0 {
                    let e = GetLastError();
                    if e != ERROR_IO_PENDING {
                        return Err(io::Error::from_raw_os_error(e as i32));
                    }
                    loop {
                        if WaitForSingleObject(self.server.event.0, 100) == WAIT_OBJECT_0 {
                            break;
                        }
                        if Instant::now() >= deadline {
                            CancelIoEx(h, &ov);
                            GetOverlappedResult(h, &ov, &mut n, 1);
                            return Err(io::Error::new(io::ErrorKind::TimedOut, "client is not reading"));
                        }
                    }
                }
                if GetOverlappedResult(h, &ov, &mut n, 1) == 0 {
                    return Err(io::Error::last_os_error());
                }
                data = &data[(n as usize).min(data.len())..];
            }
        }
        Ok(())
    }
}

/// Serve clients one at a time until `engine.shutdown` is set.
pub fn run(server: &PipeServer, engine: &Engine, allowed_client: Option<&str>) -> io::Result<()> {
    log::info(&format!("listening on pipe {}", server.name));
    while !engine.shutdown.load(Ordering::SeqCst) {
        match server.accept(&engine.shutdown) {
            Ok(true) => {}
            Ok(false) => break,
            // A client connected and left before we saw it: reset and wait again.
            Err(e) if e.raw_os_error() == Some(ERROR_NO_DATA as i32) => {
                unsafe { DisconnectNamedPipe(server.handle.0) };
                continue;
            }
            Err(e) => return Err(e),
        }
        let pid = server.client_pid();
        let image = pid.and_then(process_image);
        log::info(&format!(
            "client connected: pid {} image {}",
            pid.map(|p| p.to_string()).unwrap_or_else(|| "?".into()),
            image.as_deref().unwrap_or("?")
        ));
        if let Some(allowed) = allowed_client {
            let ok = image.as_deref().map(|i| same_image(i, allowed)).unwrap_or(false);
            if !ok {
                log::info("client refused: image is not the allowed client");
                unsafe { DisconnectNamedPipe(server.handle.0) };
                continue;
            }
        }
        let mut t = PipeTransport { server };
        let reason = serve_connection(engine, &mut t);
        log::info(&format!("client connection closed: {reason}"));
        server.disconnect();
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::clasp::server::tests::{frame, hello, test_engine};
    use serde_json::{json, Value};
    use std::fs::{File, OpenOptions};
    use std::io::{Read, Write};
    use std::sync::Arc;

    #[test]
    fn names_and_sddl() {
        let sid = "S-1-5-21-1-2-3-1001";
        assert_eq!(pipe_name(sid, None), "Clasp.Engine.v1.S-1-5-21-1-2-3-1001");
        assert_eq!(pipe_name(sid, Some("t1")), "Clasp.Engine.v1.S-1-5-21-1-2-3-1001.t1");
        assert_eq!(sddl_for(sid), "D:P(A;;GA;;;S-1-5-21-1-2-3-1001)");
        let p = pipe_path("X");
        assert_eq!(p.as_bytes(), &[0x5c, 0x5c, b'.', 0x5c, b'p', b'i', b'p', b'e', 0x5c, b'X']);
        assert!(same_image("C:/Program Files/Clasp/Clasp.exe", "c:\u{5c}program files\u{5c}clasp\u{5c}CLASP.EXE"));
        assert!(!same_image("C:/a.exe", "C:/b.exe"));
    }

    #[test]
    fn current_sid_parses_into_a_valid_descriptor() {
        let sid = current_user_sid().unwrap();
        assert!(sid.starts_with("S-1-"), "{sid}");
        let sddl = wide(&sddl_for(&sid));
        let mut sd: PSECURITY_DESCRIPTOR = ptr::null_mut();
        let ok = unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.as_ptr(), SDDL_REVISION_1, &mut sd, ptr::null_mut())
        };
        assert_ne!(ok, 0);
        unsafe { LocalFree(sd) };
    }

    struct Running {
        engine: Arc<Engine>,
        thread: Option<std::thread::JoinHandle<io::Result<()>>>,
        path: String,
    }

    impl Drop for Running {
        fn drop(&mut self) {
            self.engine.shutdown.store(true, Ordering::SeqCst);
            if let Some(t) = self.thread.take() {
                let _ = t.join();
            }
        }
    }

    fn start(tag: &str, allowed: Option<String>) -> Running {
        let sid = current_user_sid().unwrap();
        let mut rnd = [0u8; 6];
        crate::clasp::util::random_bytes(&mut rnd).unwrap();
        let suffix = format!("test-{}-{}-{}", tag, std::process::id(), crate::clasp::util::hex_lower(&rnd));
        let name = pipe_name(&sid, Some(&suffix));
        let server = PipeServer::create(&name, &sid).unwrap();
        // A second instance of the same name must be refused.
        let dup = PipeServer::create(&name, &sid);
        assert_eq!(dup.err().map(|e| e.kind()), Some(io::ErrorKind::AddrInUse));
        let engine = Arc::new(test_engine());
        let e2 = engine.clone();
        let thread = std::thread::spawn(move || run(&server, &e2, allowed.as_deref()));
        Running { engine, thread: Some(thread), path: pipe_path(&name) }
    }

    fn connect(path: &str) -> File {
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            match OpenOptions::new().read(true).write(true).open(path) {
                Ok(f) => return f,
                Err(e) => {
                    assert!(Instant::now() < deadline, "cannot connect: {e}");
                    std::thread::sleep(Duration::from_millis(20));
                }
            }
        }
    }

    fn read_frame(f: &mut File) -> Option<Value> {
        let mut hdr = [0u8; 4];
        f.read_exact(&mut hdr).ok()?;
        let n = u32::from_le_bytes(hdr) as usize;
        let mut body = vec![0u8; n];
        f.read_exact(&mut body).ok()?;
        Some(serde_json::from_slice(&body).unwrap())
    }

    fn assert_closed(f: &mut File) {
        let mut b = [0u8; 1];
        assert!(matches!(f.read(&mut b), Ok(0) | Err(_)), "connection still open");
    }

    #[test]
    fn pipe_round_trip() {
        let srv = start("rt", None);

        // hello -> hello_ack, then list_devices.
        let mut c = connect(&srv.path);
        c.write_all(&frame(&hello(1))).unwrap();
        let ack = read_frame(&mut c).unwrap();
        assert_eq!(ack["type"], "hello_ack");
        assert_eq!(ack["seq"], 1);
        c.write_all(&frame(&json!({"type": "list_devices", "request_id": "r1"}))).unwrap();
        let devs = read_frame(&mut c).unwrap();
        assert_eq!(devs["type"], "devices");
        assert_eq!(devs["seq"], 2);
        drop(c);

        // The instance is reused: wrong major -> incompatible_version, then closed.
        let mut c = connect(&srv.path);
        c.write_all(&frame(&hello(2))).unwrap();
        let err = read_frame(&mut c).unwrap();
        assert_eq!(err["type"], "error");
        assert_eq!(err["code"], "incompatible_version");
        assert!(err["seq"].as_u64().unwrap() > 2);
        assert_closed(&mut c);
        drop(c);

        // Oversized frame -> error oversized, then closed.
        let mut c = connect(&srv.path);
        c.write_all(&frame(&hello(1))).unwrap();
        assert_eq!(read_frame(&mut c).unwrap()["type"], "hello_ack");
        c.write_all(&65_537u32.to_le_bytes()).unwrap();
        let err = read_frame(&mut c).unwrap();
        assert_eq!(err["code"], "oversized");
        assert_closed(&mut c);
    }

    #[test]
    fn disallowed_client_is_refused_before_hello_ack() {
        let srv = start("deny", Some("C:/definitely/not/this.exe".into()));
        let mut c = connect(&srv.path);
        let _ = c.write_all(&frame(&hello(1)));
        assert_closed(&mut c);
    }

    #[test]
    fn allowed_client_is_served() {
        let me = std::env::current_exe().unwrap().to_string_lossy().into_owned();
        let srv = start("allow", Some(me.to_uppercase()));
        let mut c = connect(&srv.path);
        c.write_all(&frame(&hello(1))).unwrap();
        assert_eq!(read_frame(&mut c).unwrap()["type"], "hello_ack");
    }
}

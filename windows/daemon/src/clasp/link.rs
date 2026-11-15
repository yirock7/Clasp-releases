//! The AAP link abstraction. The session code only sees `AapLink`, so tests run
//! against `fake::FakeLink` and never open a driver or touch the radio.
//!
//! `DriverLink` is the real NTPodsAAP kernel-driver link. It opens ONE interface by
//! its exact path (the one identity verification picked), never "the first
//! interface". The IOCTL codes and layouts match `windows/drivers/aap`.

use std::ffi::c_void;
use std::io;
use std::ptr;

use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows_sys::Win32::System::IO::DeviceIoControl;

use super::util::wide;

/// Driver connection state (`LP_STATE`): 2 = connected.
pub const LP_CONNECTED: u32 = 2;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LinkStatus {
    pub state: u32,
    /// Address of the device the driver's channel is connected to (0 if none).
    pub address: u64,
}

pub trait AapLink: Send {
    fn connect(&mut self, addr: u64, psm: u16) -> io::Result<bool>;
    fn send(&mut self, data: &[u8]) -> io::Result<()>;
    fn recv(&mut self, timeout_ms: u32, buf: &mut [u8]) -> io::Result<usize>;
    fn status(&mut self) -> io::Result<LinkStatus>;
    /// Close the driver handle now. Idempotent.
    fn close(&mut self);
}

/// Opens a link to one exact driver interface path.
pub trait LinkOpener: Send + Sync {
    fn open(&self, interface_path: &str) -> io::Result<Box<dyn AapLink>>;
}

const GENERIC_READ: u32 = 0x8000_0000;
const GENERIC_WRITE: u32 = 0x4000_0000;
const IOCTL_LP_CONNECT: u32 = 0x8000_2000;
const IOCTL_LP_SEND: u32 = 0x8000_2008;
const IOCTL_LP_RECEIVE: u32 = 0x8000_200C;
const IOCTL_LP_GET_STATUS: u32 = 0x8000_2010;

pub struct DriverLink {
    handle: Option<HANDLE>,
}

// The handle is only used from the session worker thread that owns the link.
unsafe impl Send for DriverLink {}

impl DriverLink {
    fn handle(&self) -> io::Result<HANDLE> {
        self.handle.ok_or_else(|| io::Error::new(io::ErrorKind::NotConnected, "driver handle closed"))
    }

    fn ioctl(&self, code: u32, input: &[u8], output: &mut [u8]) -> io::Result<u32> {
        let h = self.handle()?;
        let mut returned: u32 = 0;
        let in_ptr = if input.is_empty() { ptr::null() } else { input.as_ptr() as *const c_void };
        let out_ptr = if output.is_empty() { ptr::null_mut() } else { output.as_mut_ptr() as *mut c_void };
        let ok = unsafe {
            DeviceIoControl(
                h,
                code,
                in_ptr,
                input.len() as u32,
                out_ptr,
                output.len() as u32,
                &mut returned,
                ptr::null_mut(),
            )
        };
        if ok == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(returned)
    }
}

impl AapLink for DriverLink {
    fn connect(&mut self, addr: u64, psm: u16) -> io::Result<bool> {
        let mut input = [0u8; 10];
        input[0..8].copy_from_slice(&addr.to_le_bytes());
        input[8..10].copy_from_slice(&psm.to_le_bytes());
        let mut out = [0u8; 8]; // LP_CONNECT_OUTPUT { ULONG Success; LONG Status; }
        self.ioctl(IOCTL_LP_CONNECT, &input, &mut out)?;
        Ok(u32::from_le_bytes([out[0], out[1], out[2], out[3]]) != 0)
    }

    fn send(&mut self, data: &[u8]) -> io::Result<()> {
        self.ioctl(IOCTL_LP_SEND, data, &mut [])?;
        Ok(())
    }

    fn recv(&mut self, timeout_ms: u32, buf: &mut [u8]) -> io::Result<usize> {
        // The driver treats 0 as its own 5 s default, so never pass 0.
        let to = timeout_ms.max(1).to_le_bytes();
        Ok(self.ioctl(IOCTL_LP_RECEIVE, &to, buf)? as usize)
    }

    fn status(&mut self) -> io::Result<LinkStatus> {
        // LP_STATUS_OUTPUT is packed: State @0 (u32), ConnectedAddress @4 (u64).
        let mut out = [0u8; 32];
        self.ioctl(IOCTL_LP_GET_STATUS, &[], &mut out)?;
        let mut addr = [0u8; 8];
        addr.copy_from_slice(&out[4..12]);
        Ok(LinkStatus {
            state: u32::from_le_bytes([out[0], out[1], out[2], out[3]]),
            address: u64::from_le_bytes(addr),
        })
    }

    fn close(&mut self) {
        if let Some(h) = self.handle.take() {
            unsafe { CloseHandle(h) };
        }
    }
}

impl Drop for DriverLink {
    fn drop(&mut self) {
        self.close();
    }
}

/// Real opener: `CreateFileW` on the exact interface path.
pub struct DriverOpener;

impl LinkOpener for DriverOpener {
    fn open(&self, interface_path: &str) -> io::Result<Box<dyn AapLink>> {
        let w = wide(interface_path);
        let h = unsafe {
            CreateFileW(
                w.as_ptr(),
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                ptr::null(),
                OPEN_EXISTING,
                0,
                ptr::null_mut(),
            )
        };
        if h == INVALID_HANDLE_VALUE || h.is_null() {
            return Err(io::Error::last_os_error());
        }
        Ok(Box::new(DriverLink { handle: Some(h) }))
    }
}

#[cfg(test)]
pub mod fake {
    //! Scripted in-memory link for tests.
    use super::*;
    use std::collections::VecDeque;
    use std::sync::{Arc, Mutex};
    use std::time::Duration;

    #[derive(Default)]
    pub struct FakeState {
        pub inbound: VecDeque<io::Result<Vec<u8>>>,
        pub status: Option<LinkStatus>,
        pub status_error: bool,
        pub fail_sends: bool,
        pub connect_result: Option<bool>,
        pub connect_delay: Duration,
        /// When non-zero, every send and recv blocks this long (a stuck driver call).
        pub block: Duration,
    }

    pub struct FakeLink {
        pub sent: Arc<Mutex<Vec<Vec<u8>>>>,
        pub connects: Arc<Mutex<Vec<(u64, u16)>>>,
        pub closed: Arc<Mutex<bool>>,
        pub state: Arc<Mutex<FakeState>>,
    }

    impl FakeLink {
        pub fn new() -> FakeLink {
            FakeLink {
                sent: Arc::default(),
                connects: Arc::default(),
                closed: Arc::default(),
                state: Arc::new(Mutex::new(FakeState {
                    connect_result: Some(true),
                    ..FakeState::default()
                })),
            }
        }

        pub fn share(&self) -> FakeLink {
            FakeLink {
                sent: self.sent.clone(),
                connects: self.connects.clone(),
                closed: self.closed.clone(),
                state: self.state.clone(),
            }
        }
    }

    impl AapLink for FakeLink {
        fn connect(&mut self, addr: u64, psm: u16) -> io::Result<bool> {
            let (delay, result) = {
                let s = self.state.lock().unwrap();
                (s.connect_delay, s.connect_result)
            };
            std::thread::sleep(delay);
            self.connects.lock().unwrap().push((addr, psm));
            result.ok_or_else(|| io::Error::other("fake connect error"))
        }

        fn send(&mut self, data: &[u8]) -> io::Result<()> {
            let block = self.state.lock().unwrap().block;
            std::thread::sleep(block);
            assert!(!*self.closed.lock().unwrap(), "send after close");
            if self.state.lock().unwrap().fail_sends {
                return Err(io::Error::other("fake send error"));
            }
            self.sent.lock().unwrap().push(data.to_vec());
            Ok(())
        }

        fn recv(&mut self, timeout_ms: u32, buf: &mut [u8]) -> io::Result<usize> {
            let block = self.state.lock().unwrap().block;
            std::thread::sleep(block);
            let next = self.state.lock().unwrap().inbound.pop_front();
            match next {
                Some(Ok(p)) => {
                    buf[..p.len()].copy_from_slice(&p);
                    Ok(p.len())
                }
                Some(Err(e)) => Err(e),
                None => {
                    std::thread::sleep(Duration::from_millis(timeout_ms.min(5) as u64));
                    Err(io::Error::new(io::ErrorKind::TimedOut, "fake timeout"))
                }
            }
        }

        fn status(&mut self) -> io::Result<LinkStatus> {
            let s = self.state.lock().unwrap();
            if s.status_error {
                return Err(io::Error::other("fake status error"));
            }
            Ok(s.status.unwrap_or(LinkStatus { state: LP_CONNECTED, address: 0 }))
        }

        fn close(&mut self) {
            *self.closed.lock().unwrap() = true;
        }
    }
}

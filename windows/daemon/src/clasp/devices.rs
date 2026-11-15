//! Read-only device inventory from Windows. Nothing here opens a device handle,
//! starts an inquiry/scan, or changes any state:
//!
//! - registry `HKLM\SYSTEM\CurrentControlSet\Enum\BTHENUM\{74ec2172-...}*` instance
//!   ids give the addresses Windows published the AAP service for;
//! - `BluetoothFindFirstDevice` with authenticated/remembered/connected = 1,
//!   unknown = 0 and `fIssueInquiry` = 0 lists paired devices from the cache;
//! - `SetupDiGetClassDevsW` + `SetupDiEnumDeviceInterfaces` list every present
//!   NTPodsAAP driver interface path (paths only; no `CreateFile`).

use std::io;
use std::mem::{size_of, zeroed};
use std::ptr;

use windows_sys::core::GUID;
use windows_sys::Win32::Devices::Bluetooth::{
    BluetoothFindDeviceClose, BluetoothFindFirstDevice, BluetoothFindNextDevice,
    BLUETOOTH_DEVICE_INFO, BLUETOOTH_DEVICE_SEARCH_PARAMS,
};
use windows_sys::Win32::Devices::DeviceAndDriverInstallation::{
    SetupDiDestroyDeviceInfoList, SetupDiEnumDeviceInterfaces, SetupDiGetClassDevsW,
    SetupDiGetDeviceInterfaceDetailW, DIGCF_DEVICEINTERFACE, DIGCF_PRESENT,
    SP_DEVICE_INTERFACE_DATA, SP_DEVICE_INTERFACE_DETAIL_DATA_W,
};
use windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE;
use windows_sys::Win32::System::Registry::{
    RegCloseKey, RegEnumKeyExW, RegOpenKeyExW, HKEY, HKEY_LOCAL_MACHINE, KEY_READ,
};

use super::identity::{DeviceSource, Inventory, PairedDevice};
use super::util::{from_wide, wide, win_join};

/// NTPodsAAP device interface class (matches `windows/drivers/aap/NTPodsAAP.h`).
pub const GUID_DEVINTERFACE_NTPODSAAP: GUID = GUID {
    data1: 0xC0FF_EE00,
    data2: 0x1337,
    data3: 0x4A5B,
    data4: [0x9E, 0x6F, 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6],
};

const AAP_SERVICE_PREFIX: &str = "{74ec2172-0bad-4d01-8f77-997b2be0722a}";

pub struct WindowsDevices;

impl DeviceSource for WindowsDevices {
    fn inventory(&self) -> io::Result<Inventory> {
        Ok(Inventory {
            paired: paired_devices(),
            aap_service: aap_service_addresses(),
            interfaces: driver_interface_paths()?,
        })
    }
}

/// Paired devices from the Bluetooth cache. No inquiry is issued.
fn paired_devices() -> Vec<PairedDevice> {
    let mut out = Vec::new();
    unsafe {
        let mut params: BLUETOOTH_DEVICE_SEARCH_PARAMS = zeroed();
        params.dwSize = size_of::<BLUETOOTH_DEVICE_SEARCH_PARAMS>() as u32;
        params.fReturnAuthenticated = 1;
        params.fReturnRemembered = 1;
        params.fReturnConnected = 1;
        params.fReturnUnknown = 0;
        params.fIssueInquiry = 0;
        params.cTimeoutMultiplier = 0;
        params.hRadio = ptr::null_mut();

        let mut info: BLUETOOTH_DEVICE_INFO = zeroed();
        info.dwSize = size_of::<BLUETOOTH_DEVICE_INFO>() as u32;
        let h = BluetoothFindFirstDevice(&params, &mut info);
        if h.is_null() {
            return out;
        }
        loop {
            // Paired = remembered or authenticated; a merely connected, unpaired
            // device is never listed.
            if info.fRemembered != 0 || info.fAuthenticated != 0 {
                out.push(PairedDevice {
                    address: info.Address.Anonymous.ullLong & 0xFFFF_FFFF_FFFF,
                    name: from_wide(&info.szName),
                    connected: Some(info.fConnected != 0),
                });
            }
            info = zeroed();
            info.dwSize = size_of::<BLUETOOTH_DEVICE_INFO>() as u32;
            if BluetoothFindNextDevice(h, &mut info) == 0 {
                break;
            }
        }
        BluetoothFindDeviceClose(h);
    }
    out
}

unsafe fn reg_subkeys(key: HKEY) -> Vec<String> {
    let mut out = Vec::new();
    let mut buf = [0u16; 512];
    let mut i = 0;
    loop {
        let mut len = buf.len() as u32;
        let r = RegEnumKeyExW(
            key,
            i,
            buf.as_mut_ptr(),
            &mut len,
            ptr::null_mut(),
            ptr::null_mut(),
            ptr::null_mut(),
            ptr::null_mut(),
        );
        if r != 0 {
            break;
        }
        out.push(String::from_utf16_lossy(&buf[..len as usize]));
        i += 1;
    }
    out
}

unsafe fn reg_open(parent: HKEY, path: &str) -> Option<HKEY> {
    let w = wide(path);
    let mut k: HKEY = ptr::null_mut();
    (RegOpenKeyExW(parent, w.as_ptr(), 0, KEY_READ, &mut k) == 0).then_some(k)
}

/// Address from a BTHENUM instance id such as `7&3549f023&2&282D7FB83C8B_C00000000`.
pub fn instance_id_address(inst: &str) -> Option<u64> {
    let upper = inst.to_ascii_uppercase();
    let pos = upper.rfind("_C00000000")?;
    if pos < 12 {
        return None;
    }
    let hex = &upper[pos - 12..pos];
    if !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
        return None;
    }
    u64::from_str_radix(hex, 16).ok().filter(|&a| a != 0)
}

fn aap_service_addresses() -> Vec<u64> {
    let mut addrs = Vec::new();
    unsafe {
        let root_path = win_join(&["SYSTEM", "CurrentControlSet", "Enum", "BTHENUM"]);
        let Some(root) = reg_open(HKEY_LOCAL_MACHINE, &root_path) else {
            return addrs;
        };
        for dev in reg_subkeys(root) {
            if !dev.to_ascii_lowercase().starts_with(AAP_SERVICE_PREFIX) {
                continue;
            }
            if let Some(k) = reg_open(root, &dev) {
                for inst in reg_subkeys(k) {
                    if let Some(a) = instance_id_address(&inst) {
                        if !addrs.contains(&a) {
                            addrs.push(a);
                        }
                    }
                }
                RegCloseKey(k);
            }
        }
        RegCloseKey(root);
    }
    addrs
}

/// Every present NTPodsAAP interface path. Paths only: no handle is opened.
fn driver_interface_paths() -> io::Result<Vec<String>> {
    let mut out = Vec::new();
    unsafe {
        let devinfo = SetupDiGetClassDevsW(
            &GUID_DEVINTERFACE_NTPODSAAP,
            ptr::null(),
            ptr::null_mut(),
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE,
        );
        if devinfo == INVALID_HANDLE_VALUE as isize {
            return Err(io::Error::last_os_error());
        }
        let mut index = 0u32;
        loop {
            let mut ifdata: SP_DEVICE_INTERFACE_DATA = zeroed();
            ifdata.cbSize = size_of::<SP_DEVICE_INTERFACE_DATA>() as u32;
            if SetupDiEnumDeviceInterfaces(
                devinfo,
                ptr::null(),
                &GUID_DEVINTERFACE_NTPODSAAP,
                index,
                &mut ifdata,
            ) == 0
            {
                break; // ERROR_NO_MORE_ITEMS (or any failure): stop
            }
            index += 1;
            let mut required: u32 = 0;
            SetupDiGetDeviceInterfaceDetailW(
                devinfo,
                &ifdata,
                ptr::null_mut(),
                0,
                &mut required,
                ptr::null_mut(),
            );
            if !(8..=64 * 1024).contains(&required) {
                // Keep the count honest: an interface we cannot read is unparsed.
                out.push(String::new());
                continue;
            }
            // u32-aligned buffer for SP_DEVICE_INTERFACE_DETAIL_DATA_W.
            let mut buf = vec![0u32; (required as usize).div_ceil(4)];
            let detail = buf.as_mut_ptr() as *mut SP_DEVICE_INTERFACE_DETAIL_DATA_W;
            (*detail).cbSize = if cfg!(target_pointer_width = "64") { 8 } else { 6 };
            if SetupDiGetDeviceInterfaceDetailW(
                devinfo,
                &ifdata,
                detail,
                required,
                ptr::null_mut(),
                ptr::null_mut(),
            ) == 0
            {
                out.push(String::new());
                continue;
            }
            let path_ptr = (*detail).DevicePath.as_ptr();
            let max = (required as usize - 4) / 2;
            let slice = std::slice::from_raw_parts(path_ptr, max);
            out.push(from_wide(slice));
        }
        SetupDiDestroyDeviceInfoList(devinfo);
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn instance_ids() {
        assert_eq!(instance_id_address("7&3549f023&2&282D7FB83C8B_C00000000"), Some(0x282D_7FB8_3C8B));
        assert_eq!(instance_id_address("7&3549f023&2&282d7fb83c8b_c00000000"), Some(0x282D_7FB8_3C8B));
        assert_eq!(instance_id_address("7&3549f023&2&000000000000_C00000000"), None);
        assert_eq!(instance_id_address("garbage"), None);
    }
}

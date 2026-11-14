//! Device identity (protocol section 6, review finding HR-03).
//!
//! - `device_id` = "dev-" + first 16 lowercase hex chars of
//!   SHA-256(32-byte local salt || 6 address bytes big-endian).
//! - `open_session` resolves a `device_id` to exactly one paired AAP device AND
//!   exactly one NTPodsAAP driver interface whose path names that address.
//!   Anything else fails with a reason; there is no fallback.
//!
//! All functions here are pure over an `Inventory` snapshot, so they are unit
//! tested with synthetic interface paths and never enumerate real hardware.

use std::fs::OpenOptions;
use std::io::{self, Read, Write};
use std::path::Path;

use super::protocol::{DeviceEntry, DriverInterface};
use super::util::{hex_lower, random_bytes, sha256};

pub const SALT_FILE: &str = "device-salt";

pub const NOT_CONNECTED: &str =
    "not_connected: connect the AirPods to Windows first (Bluetooth settings), then start the session";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PairedDevice {
    pub address: u64,
    pub name: String,
    pub connected: Option<bool>,
}

/// One read-only snapshot of what Windows knows.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Inventory {
    /// Paired (remembered or authenticated) Bluetooth devices.
    pub paired: Vec<PairedDevice>,
    /// Addresses Windows published the AAP service for (BTHENUM {74ec2172-...}).
    pub aap_service: Vec<u64>,
    /// Paths of every present NTPodsAAP driver interface.
    pub interfaces: Vec<String>,
}

/// Where the inventory comes from (real Windows APIs, or a test fixture).
pub trait DeviceSource: Send + Sync {
    fn inventory(&self) -> io::Result<Inventory>;
}

/// Load the 32-byte salt from `dir`, creating it (randomly) on first use. An
/// existing file of the wrong size is an error, never silently replaced.
pub fn load_or_create_salt(dir: &Path) -> io::Result<[u8; 32]> {
    std::fs::create_dir_all(dir)?;
    let path = dir.join(SALT_FILE);
    match OpenOptions::new().write(true).create_new(true).open(&path) {
        Ok(mut f) => {
            let mut salt = [0u8; 32];
            random_bytes(&mut salt)?;
            f.write_all(&salt)?;
            f.sync_all()?;
            return Ok(salt);
        }
        Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {}
        Err(e) => return Err(e),
    }
    let mut data = Vec::new();
    std::fs::File::open(&path)?.read_to_end(&mut data)?;
    let salt: [u8; 32] = data.as_slice().try_into().map_err(|_| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            format!("device salt file has {} bytes, expected 32", data.len()),
        )
    })?;
    Ok(salt)
}

pub fn device_id(salt: &[u8; 32], address: u64) -> io::Result<String> {
    let mut input = Vec::with_capacity(38);
    input.extend_from_slice(salt);
    input.extend_from_slice(&address.to_be_bytes()[2..8]);
    let digest = sha256(&input)?;
    Ok(format!("dev-{}", &hex_lower(&digest)[..16]))
}

/// The 48-bit address named by a driver interface path: the 12 hex digits right
/// before `_C00000000` (case-insensitive), preceded by a non-hex delimiter
/// (`&`, `#` or `_`). `None` when there is no such token, more than one distinct
/// address, or the address is zero.
pub fn parse_interface_address(path: &str) -> Option<u64> {
    const MARK: &str = "_c00000000";
    let lower = path.to_ascii_lowercase();
    let bytes = lower.as_bytes();
    let mut found: Option<u64> = None;
    let mut start = 0;
    while let Some(rel) = lower[start..].find(MARK) {
        let pos = start + rel;
        start = pos + MARK.len();
        if pos < 13 {
            continue;
        }
        let hex = &lower[pos - 12..pos];
        if !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
            continue;
        }
        if !matches!(bytes[pos - 13], b'&' | b'#' | b'_') {
            continue;
        }
        // The marker must end the instance-id segment.
        if let Some(&next) = bytes.get(pos + MARK.len()) {
            if next != b'#' {
                continue;
            }
        }
        let addr = u64::from_str_radix(hex, 16).ok()?;
        match found {
            Some(prev) if prev != addr => return None,
            _ => found = Some(addr),
        }
    }
    found.filter(|&a| a != 0)
}

/// Driver-interface status for one address, and the matching paths.
pub fn interface_status(address: u64, interfaces: &[String]) -> (DriverInterface, Vec<String>) {
    let mut matching = Vec::new();
    let mut unparsed = 0;
    for p in interfaces {
        match parse_interface_address(p) {
            Some(a) if a == address => matching.push(p.clone()),
            Some(_) => {}
            None => unparsed += 1,
        }
    }
    let status = if matching.len() > 1 {
        DriverInterface::Ambiguous
    } else if unparsed > 0 {
        // An unparsable path might name this device too; we cannot tell.
        DriverInterface::Unparsed
    } else if matching.len() == 1 {
        DriverInterface::Bound
    } else {
        DriverInterface::NotFound
    };
    (status, matching)
}

/// Paired devices, deduplicated by address (first entry wins).
fn unique_paired(inv: &Inventory) -> Vec<&PairedDevice> {
    let mut out: Vec<&PairedDevice> = Vec::new();
    for d in &inv.paired {
        if d.address != 0 && !out.iter().any(|o| o.address == d.address) {
            out.push(d);
        }
    }
    out
}

/// The `devices` listing: paired devices that publish the AAP service or that a
/// driver interface names. No address ever leaves this function.
pub fn list_devices(inv: &Inventory, salt: &[u8; 32]) -> io::Result<Vec<DeviceEntry>> {
    let mut out = Vec::new();
    for d in unique_paired(inv) {
        let aap = inv.aap_service.contains(&d.address);
        let (status, matching) = interface_status(d.address, &inv.interfaces);
        if !aap && matching.is_empty() {
            continue;
        }
        out.push(DeviceEntry {
            device_id: device_id(salt, d.address)?,
            name: d.name.clone(),
            aap_service: aap,
            driver_interface: status,
            windows_connected: d.connected,
        });
    }
    Ok(out)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Resolved {
    pub address: u64,
    pub interface_path: String,
}

/// Resolve a `device_id` for `open_session`. Errors are the `detail` to report.
pub fn resolve(inv: &Inventory, salt: &[u8; 32], wanted: &str) -> Result<Resolved, String> {
    let mut hits = Vec::new();
    for d in unique_paired(inv) {
        let id = device_id(salt, d.address).map_err(|e| format!("internal: {e}"))?;
        if id == wanted {
            hits.push(d);
        }
    }
    let dev = match hits.len() {
        0 => return Err("not_found: no paired device has this device_id".to_string()),
        1 => hits[0],
        n => return Err(format!("ambiguous: {n} paired devices have this device_id")),
    };
    if !inv.aap_service.contains(&dev.address) {
        return Err("not_found: the device does not publish the AAP service".to_string());
    }
    // Never page a device that is not already connected to Windows: the driver
    // connect could pull it from another host or change Windows audio routing.
    if dev.connected != Some(true) {
        return Err(NOT_CONNECTED.to_string());
    }
    let (status, matching) = interface_status(dev.address, &inv.interfaces);
    match status {
        DriverInterface::Bound => Ok(Resolved {
            address: dev.address,
            interface_path: matching[0].clone(),
        }),
        DriverInterface::Ambiguous => Err(format!(
            "ambiguous: {} AAP driver interfaces name this device",
            matching.len()
        )),
        DriverInterface::Unparsed => {
            Err("unparsed: an AAP driver interface path does not name a device address".to_string())
        }
        DriverInterface::NotFound => {
            Err("not_found: no AAP driver interface names this device".to_string())
        }
    }
}

#[cfg(test)]
pub mod tests {
    use super::*;

    pub const ADDR_A: u64 = 0x282D_7FB8_3C8B;
    pub const ADDR_B: u64 = 0xA0B1_C2D3_E4F5;
    pub const SALT: [u8; 32] = [7u8; 32];

    /// A synthetic interface path in the shape BTHENUM child interfaces have.
    pub fn iface(addr_hex: &str) -> String {
        format!(
            "##?#BTHENUM#{{74ec2172-0bad-4d01-8f77-997b2be0722a}}_VID&0001004c_PID&2014#7&3549f023&2&{addr_hex}_C00000000#{{c0ffee00-1337-4a5b-9e6f-a1b2c3d4e5f6}}"
        )
    }

    pub fn inventory() -> Inventory {
        Inventory {
            paired: vec![
                PairedDevice { address: ADDR_A, name: "AirPods Pro".into(), connected: Some(true) },
                PairedDevice { address: ADDR_B, name: "Keyboard".into(), connected: Some(false) },
            ],
            aap_service: vec![ADDR_A],
            interfaces: vec![iface("282D7FB83C8B")],
        }
    }

    #[test]
    fn parses_addresses() {
        assert_eq!(parse_interface_address(&iface("282D7FB83C8B")), Some(ADDR_A));
        assert_eq!(parse_interface_address(&iface("282d7fb83c8b")), Some(ADDR_A));
        assert_eq!(parse_interface_address(&iface("282D7FB83C8B").to_ascii_lowercase()), Some(ADDR_A));
        assert_eq!(parse_interface_address("x_A0B1C2D3E4F5_C00000000"), Some(ADDR_B));
        assert_eq!(parse_interface_address("x#A0B1C2D3E4F5_c00000000#y"), Some(ADDR_B));
    }

    #[test]
    fn unparsable_paths() {
        for p in [
            "".to_string(),
            "##?#ROOT#SYSTEM#0001#{c0ffee00-1337-4a5b-9e6f-a1b2c3d4e5f6}".to_string(),
            iface("282D7FB83C8"),          // 11 digits
            iface("282D7FB83C8G"),         // not hex
            iface("000000000000"),         // zero address
            "x&1282D7FB83C8B_C00000000".to_string(), // longer hex run, no delimiter
            "x&282D7FB83C8B_C000000001".to_string(), // marker not at segment end
            "x&282D7FB83C8B_C00000000#y&A0B1C2D3E4F5_C00000000".to_string(), // two addresses
        ] {
            assert_eq!(parse_interface_address(&p), None, "{p}");
        }
        // The same address twice is still one address.
        assert_eq!(
            parse_interface_address("x&282D7FB83C8B_C00000000#y&282D7FB83C8B_C00000000"),
            Some(ADDR_A)
        );
    }

    #[test]
    fn device_id_shape_and_salt() {
        let id = device_id(&SALT, ADDR_A).unwrap();
        assert!(id.starts_with("dev-") && id.len() == 20);
        assert!(id[4..].bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase()));
        // Reproduce the definition independently.
        let mut input = SALT.to_vec();
        input.extend_from_slice(&[0x28, 0x2D, 0x7F, 0xB8, 0x3C, 0x8B]);
        assert_eq!(id, format!("dev-{}", &hex_lower(&sha256(&input).unwrap())[..16]));
        assert_ne!(id, device_id(&[8u8; 32], ADDR_A).unwrap());
        assert!(!id.contains("282d7f"));
    }

    #[test]
    fn salt_created_once() {
        let dir = std::env::temp_dir().join(format!("clasp-salt-test-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let a = load_or_create_salt(&dir).unwrap();
        let b = load_or_create_salt(&dir).unwrap();
        assert_eq!(a, b);
        assert_ne!(a, [0u8; 32]);
        std::fs::write(dir.join(SALT_FILE), b"short").unwrap();
        assert!(load_or_create_salt(&dir).is_err());
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn listing() {
        let list = list_devices(&inventory(), &SALT).unwrap();
        assert_eq!(list.len(), 1);
        assert_eq!(list[0].name, "AirPods Pro");
        assert_eq!(list[0].driver_interface, DriverInterface::Bound);
        assert_eq!(list[0].windows_connected, Some(true));
        let json = serde_json::to_string(&list).unwrap().to_ascii_lowercase();
        assert!(!json.contains("282d7fb83c8b"));
    }

    #[test]
    fn resolve_ok_uses_the_exact_path() {
        let id = device_id(&SALT, ADDR_A).unwrap();
        let mut inv = inventory();
        inv.interfaces.insert(0, iface("A0B1C2D3E4F5")); // another device's interface first
        let r = resolve(&inv, &SALT, &id).unwrap();
        assert_eq!(r.address, ADDR_A);
        assert_eq!(r.interface_path, iface("282D7FB83C8B"));
    }

    #[test]
    fn resolve_failures() {
        let id = device_id(&SALT, ADDR_A).unwrap();

        let err = resolve(&inventory(), &SALT, "dev-0000000000000000").unwrap_err();
        assert!(err.starts_with("not_found"), "{err}");

        let mut inv = inventory();
        inv.interfaces.push(iface("282d7fb83c8b"));
        let err = resolve(&inv, &SALT, &id).unwrap_err();
        assert_eq!(err, "ambiguous: 2 AAP driver interfaces name this device");
        assert_eq!(list_devices(&inv, &SALT).unwrap()[0].driver_interface, DriverInterface::Ambiguous);

        let mut inv = inventory();
        inv.interfaces = vec![iface("A0B1C2D3E4F5")]; // mismatch: names a different device
        let err = resolve(&inv, &SALT, &id).unwrap_err();
        assert!(err.starts_with("not_found"), "{err}");
        assert_eq!(list_devices(&inv, &SALT).unwrap()[0].driver_interface, DriverInterface::NotFound);

        let mut inv = inventory();
        inv.interfaces.push("##?#SOMETHING#ELSE#{c0ffee00-1337-4a5b-9e6f-a1b2c3d4e5f6}".into());
        let err = resolve(&inv, &SALT, &id).unwrap_err();
        assert!(err.starts_with("unparsed"), "{err}");
        assert_eq!(list_devices(&inv, &SALT).unwrap()[0].driver_interface, DriverInterface::Unparsed);

        let mut inv = inventory();
        inv.interfaces.clear();
        assert!(resolve(&inv, &SALT, &id).unwrap_err().starts_with("not_found"));

        let mut inv = inventory();
        inv.aap_service.clear();
        assert!(resolve(&inv, &SALT, &id).unwrap_err().contains("AAP service"));

        // Not connected to Windows (or unknown): refused, never paged.
        for connected in [Some(false), None] {
            let mut inv = inventory();
            inv.paired[0].connected = connected;
            assert_eq!(resolve(&inv, &SALT, &id).unwrap_err(), NOT_CONNECTED);
        }

        // A device id for an unpaired address never resolves, even with an interface.
        let mut inv = inventory();
        inv.paired.retain(|d| d.address != ADDR_A);
        assert!(resolve(&inv, &SALT, &id).unwrap_err().starts_with("not_found"));
    }
}

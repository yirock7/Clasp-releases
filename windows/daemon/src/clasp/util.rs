//! Small helpers: OS randomness, SHA-256 (both via Windows CNG, no extra crates),
//! UUIDs, UTC timestamps and UTF-16 conversion.

use std::io;
use std::time::{SystemTime, UNIX_EPOCH};

use windows_sys::Win32::Security::Cryptography::{
    BCryptGenRandom, BCryptHash, BCRYPT_SHA256_ALG_HANDLE, BCRYPT_USE_SYSTEM_PREFERRED_RNG,
};

/// The path separator of Windows paths, produced at runtime so no source file has
/// to carry a literal backslash (see the tool-input note in the engine README).
pub fn bs() -> char {
    char::from(0x5c_u8)
}

/// Joins path or registry segments with the Windows separator.
pub fn win_join(parts: &[&str]) -> String {
    parts.join(&bs().to_string())
}

/// Cryptographically random bytes from the system-preferred RNG.
pub fn random_bytes(out: &mut [u8]) -> io::Result<()> {
    let status = unsafe {
        BCryptGenRandom(
            std::ptr::null_mut(),
            out.as_mut_ptr(),
            out.len() as u32,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG,
        )
    };
    if status != 0 {
        return Err(io::Error::other(format!("BCryptGenRandom failed: 0x{:08X}", status as u32)));
    }
    Ok(())
}

/// SHA-256 of `data`.
pub fn sha256(data: &[u8]) -> io::Result<[u8; 32]> {
    let mut out = [0u8; 32];
    let status = unsafe {
        BCryptHash(
            BCRYPT_SHA256_ALG_HANDLE,
            std::ptr::null(),
            0,
            data.as_ptr(),
            data.len() as u32,
            out.as_mut_ptr(),
            out.len() as u32,
        )
    };
    if status != 0 {
        return Err(io::Error::other(format!("BCryptHash failed: 0x{:08X}", status as u32)));
    }
    Ok(out)
}

pub fn hex_lower(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        s.push_str(&format!("{b:02x}"));
    }
    s
}

/// A random (version 4) UUID in the usual 8-4-4-4-12 form.
pub fn new_uuid_v4() -> io::Result<String> {
    let mut b = [0u8; 16];
    random_bytes(&mut b)?;
    b[6] = (b[6] & 0x0F) | 0x40;
    b[8] = (b[8] & 0x3F) | 0x80;
    let h = hex_lower(&b);
    Ok(format!("{}-{}-{}-{}-{}", &h[0..8], &h[8..12], &h[12..16], &h[16..20], &h[20..32]))
}

/// Current UTC time as ISO 8601 with milliseconds and `Z`.
pub fn utc_now_iso_ms() -> String {
    format_iso_ms(SystemTime::now())
}

pub fn format_iso_ms(t: SystemTime) -> String {
    let d = t.duration_since(UNIX_EPOCH).unwrap_or_default();
    let secs = d.as_secs() as i64;
    let ms = d.subsec_millis();
    let days = secs.div_euclid(86_400);
    let rem = secs.rem_euclid(86_400);
    let (y, m, day) = civil_from_days(days);
    format!(
        "{y:04}-{m:02}-{day:02}T{:02}:{:02}:{:02}.{ms:03}Z",
        rem / 3600,
        (rem % 3600) / 60,
        rem % 60
    )
}

/// Days since 1970-01-01 to (year, month, day) in the proleptic Gregorian calendar
/// (H. Hinnant's algorithm).
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

/// NUL-terminated UTF-16 for Win32 W APIs.
pub fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// UTF-16 buffer up to the first NUL.
pub fn from_wide(buf: &[u16]) -> String {
    let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    String::from_utf16_lossy(&buf[..end])
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    #[test]
    fn sha256_known_vector() {
        assert_eq!(
            hex_lower(&sha256(b"abc").unwrap()),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }

    #[test]
    fn uuid_shape() {
        let u = new_uuid_v4().unwrap();
        assert_eq!(u.len(), 36);
        assert_eq!(&u[14..15], "4");
        assert_ne!(u, new_uuid_v4().unwrap());
    }

    #[test]
    fn iso_format() {
        let t = UNIX_EPOCH + Duration::from_millis(1_791_916_445_123);
        assert_eq!(format_iso_ms(t), "2026-10-13T18:34:05.123Z");
        assert_eq!(format_iso_ms(UNIX_EPOCH), "1970-01-01T00:00:00.000Z");
        let leap = UNIX_EPOCH + Duration::from_secs(951_782_400); // 2000-02-29
        assert_eq!(format_iso_ms(leap), "2000-02-29T00:00:00.000Z");
    }

    #[test]
    fn separator_is_backslash() {
        assert_eq!(bs() as u32, 0x5c);
        assert_eq!(win_join(&["a", "b"]).as_bytes(), &[b'a', 0x5c, b'b']);
    }
}

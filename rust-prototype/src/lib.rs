//! Prototype Rust hash core for fHash.
//!
//! Goal: prove that the C++ hash core (`Algorithms/` + `Common/HashEngine`) can
//! be replaced by Rust **behind a C FFI**, leaving every native UI (Swift on
//! macOS, C# WinUI 3 / C++ UWP / MFC on Windows) untouched.
//!
//! The existing engine reads each file once and feeds the same buffer to all
//! four algorithms in parallel (see `HashEngine.cpp`). `MultiHasher` mirrors
//! that single-pass shape, and the digests are emitted as **uppercase** hex to
//! match the C++ `%02X` output byte-for-byte.

use std::ffi::{c_char, c_int, CStr};
use std::fs::File;
use std::io::Read;

use md5::Md5;
use sha1::Sha1;
use sha2::{Digest, Sha256, Sha512};

/// One file's four digests, as uppercase hex strings.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Digests {
    pub md5: String,
    pub sha1: String,
    pub sha256: String,
    pub sha512: String,
}

/// Updates MD5 / SHA1 / SHA256 / SHA512 from a single stream of bytes,
/// exactly like the C++ engine does over one file read loop.
pub struct MultiHasher {
    md5: Md5,
    sha1: Sha1,
    sha256: Sha256,
    sha512: Sha512,
}

impl Default for MultiHasher {
    fn default() -> Self {
        Self::new()
    }
}

impl MultiHasher {
    pub fn new() -> Self {
        MultiHasher {
            md5: Md5::new(),
            sha1: Sha1::new(),
            sha256: Sha256::new(),
            sha512: Sha512::new(),
        }
    }

    pub fn update(&mut self, data: &[u8]) {
        self.md5.update(data);
        self.sha1.update(data);
        self.sha256.update(data);
        self.sha512.update(data);
    }

    pub fn finalize(self) -> Digests {
        Digests {
            md5: hex::encode_upper(self.md5.finalize()),
            sha1: hex::encode_upper(self.sha1.finalize()),
            sha256: hex::encode_upper(self.sha256.finalize()),
            sha512: hex::encode_upper(self.sha512.finalize()),
        }
    }
}

/// Hash an in-memory buffer (used by tests and the buffer FFI).
pub fn hash_bytes(data: &[u8]) -> Digests {
    let mut hasher = MultiHasher::new();
    hasher.update(data);
    hasher.finalize()
}

/// Hash a file by path, streaming it in 1 MiB chunks like the C++ engine.
pub fn hash_file(path: &str) -> std::io::Result<Digests> {
    let mut file = File::open(path)?;
    let mut hasher = MultiHasher::new();
    let mut buf = vec![0u8; 1024 * 1024]; // 2^20, matches DataBuffer::preflen
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(hasher.finalize())
}

// ---------------------------------------------------------------------------
// C FFI — this is the surface the Swift / C++ / MFC UIs would call instead of
// the current C++ engine. Caller passes a `FHashDigestsC` to fill; all strings
// are NUL-terminated uppercase hex. Returns 0 on success, non-zero on error.
// ---------------------------------------------------------------------------

/// C-compatible result holder. Buffer sizes are (hex length + 1) for the NUL.
#[repr(C)]
pub struct FHashDigestsC {
    pub md5: [c_char; 33],
    pub sha1: [c_char; 41],
    pub sha256: [c_char; 65],
    pub sha512: [c_char; 129],
}

fn copy_into(dst: &mut [c_char], src: &str) {
    // src is ASCII hex and always shorter than dst (dst has room for NUL).
    let bytes = src.as_bytes();
    for (i, b) in bytes.iter().enumerate() {
        dst[i] = *b as c_char;
    }
    dst[bytes.len()] = 0;
}

fn fill_struct(out: *mut FHashDigestsC, d: &Digests) {
    // Safety: caller guarantees `out` points to a valid FHashDigestsC.
    let out = unsafe { &mut *out };
    copy_into(&mut out.md5, &d.md5);
    copy_into(&mut out.sha1, &d.sha1);
    copy_into(&mut out.sha256, &d.sha256);
    copy_into(&mut out.sha512, &d.sha512);
}

/// Hash a raw buffer. Returns 0 on success, -1 on null argument.
///
/// # Safety
/// `data` must point to `len` readable bytes; `out` must be a valid pointer.
#[no_mangle]
pub unsafe extern "C" fn fhash_core_hash_buffer(
    data: *const u8,
    len: usize,
    out: *mut FHashDigestsC,
) -> c_int {
    if out.is_null() || (data.is_null() && len != 0) {
        return -1;
    }
    let slice = if len == 0 {
        &[][..]
    } else {
        std::slice::from_raw_parts(data, len)
    };
    fill_struct(out, &hash_bytes(slice));
    0
}

/// Hash a file by UTF-8 path. Returns 0 on success, -1 bad args, -2 on I/O error.
///
/// # Safety
/// `path` must be a valid NUL-terminated C string; `out` must be valid.
#[no_mangle]
pub unsafe extern "C" fn fhash_core_hash_file(
    path: *const c_char,
    out: *mut FHashDigestsC,
) -> c_int {
    if path.is_null() || out.is_null() {
        return -1;
    }
    let path = match CStr::from_ptr(path).to_str() {
        Ok(s) => s,
        Err(_) => return -1,
    };
    match hash_file(path) {
        Ok(d) => {
            fill_struct(out, &d);
            0
        }
        Err(_) => -2,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Canonical NIST/RFC test vectors for "abc" (uppercased to match the C++
    // engine's %02X output).
    #[test]
    fn abc_matches_known_vectors() {
        let d = hash_bytes(b"abc");
        assert_eq!(d.md5, "900150983CD24FB0D6963F7D28E17F72");
        assert_eq!(d.sha1, "A9993E364706816ABA3E25717850C26C9CD0D89D");
        assert_eq!(
            d.sha256,
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"
        );
        assert_eq!(
            d.sha512,
            "DDAF35A193617ABACC417349AE20413112E6FA4E89A97EA20A9EEEE64B55D39A\
2192992A274FC1A836BA3C23A3FEEBBD454D4423643CE80E2A9AC94FA54CA49F"
        );
    }

    #[test]
    fn empty_matches_known_vectors() {
        let d = hash_bytes(b"");
        assert_eq!(d.md5, "D41D8CD98F00B204E9800998ECF8427E");
        assert_eq!(d.sha1, "DA39A3EE5E6B4B0D3255BFEF95601890AFD80709");
        assert_eq!(
            d.sha256,
            "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"
        );
    }

    #[test]
    fn ffi_buffer_roundtrip() {
        let mut out = FHashDigestsC {
            md5: [0; 33],
            sha1: [0; 41],
            sha256: [0; 65],
            sha512: [0; 129],
        };
        let data = b"abc";
        let rc = unsafe { fhash_core_hash_buffer(data.as_ptr(), data.len(), &mut out) };
        assert_eq!(rc, 0);
        let md5 = unsafe { CStr::from_ptr(out.md5.as_ptr()) };
        assert_eq!(md5.to_str().unwrap(), "900150983CD24FB0D6963F7D28E17F72");
    }
}

# fhash_core — Rust hash-core prototype

A standalone proof that fHash's C++ hash core (`trunk/source/Algorithms` +
`trunk/source/Common/HashEngine.cpp`) can be replaced by Rust **behind a C FFI**
without touching any of the native UIs.

It is **not wired into the apps** — it is a parity/feasibility prototype. See
[`../RUST_REWRITE_ASSESSMENT.md`](../RUST_REWRITE_ASSESSMENT.md) for the full
analysis.

## What it does

- `MultiHasher` updates MD5 / SHA1 / SHA256 / SHA512 from one byte stream, the
  same single-file-read shape the C++ engine uses.
- Digests are emitted as **uppercase** hex to match the C++ `%02X` output
  byte-for-byte (the UI lower/upper-cases for display, exactly as today).
- A C FFI (`fhash_core_hash_file`, `fhash_core_hash_buffer` + `FHashDigestsC`)
  is the surface the Swift / C++ / MFC code would call.

## Build & test

```sh
cargo test                 # runs known-answer tests (abc, empty) + FFI roundtrip
cargo build --release      # produces target/release/libfhash_core.a (the link artifact)
cargo run --release --bin fhash_demo -- <file>   # print the 4 digests for a file
```

Parity check against the system tools (which the C++ engine also matches):

```sh
cargo run -q --release --bin fhash_demo -- ../LICENSE
md5 -q ../LICENSE; shasum -a 256 ../LICENSE   # compare (case-insensitive)
```

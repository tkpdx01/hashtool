//! Demo CLI: hashes the files passed as arguments and prints the four digests
//! in the same shape the app shows them. Lets you eyeball parity against the
//! shipping C++ engine (or `md5` / `shasum -a 1/256/512`).

use std::process::ExitCode;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.is_empty() {
        eprintln!("usage: fhash_demo <file> [file ...]");
        return ExitCode::FAILURE;
    }

    let mut had_error = false;
    for path in &args {
        match fhash_core::hash_file(path) {
            Ok(d) => {
                println!("Name: {path}");
                println!("MD5: {}", d.md5);
                println!("SHA1: {}", d.sha1);
                println!("SHA256: {}", d.sha256);
                println!("SHA512: {}", d.sha512);
                println!();
            }
            Err(e) => {
                eprintln!("error: {path}: {e}");
                had_error = true;
            }
        }
    }

    if had_error {
        ExitCode::FAILURE
    } else {
        ExitCode::SUCCESS
    }
}

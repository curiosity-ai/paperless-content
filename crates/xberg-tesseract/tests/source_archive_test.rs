#![allow(dead_code)]

#[path = "../build_support/source_archive.rs"]
mod source_archive;
#[path = "../build_support/source_cache.rs"]
mod source_cache;

use source_archive::{ArchiveLimits, extract_source_archive};
use source_cache::{VerifiedArtifact, prepare_source_tree};
use std::fs;
use std::io::{Cursor, Write};
use tempfile::tempdir;
use zip::write::SimpleFileOptions;

const ARCHIVE_ROOT: &str = "tesseract-pinned";

#[test]
fn should_reject_archive_above_entry_limit_before_extraction() {
    let archive = source_zip(&[
        ("tesseract-pinned/CMakeLists.txt", b"build"),
        ("tesseract-pinned/src.cpp", b"source"),
    ]);
    let destination = tempdir().expect("create extraction destination");

    let error = extract_source_archive(
        &archive,
        destination.path(),
        ARCHIVE_ROOT,
        ArchiveLimits {
            max_entries: 1,
            max_uncompressed_bytes: 1024,
        },
    )
    .expect_err("entry limit must reject archive");

    assert_eq!(error.kind(), std::io::ErrorKind::InvalidData);
    assert_eq!(fs::read_dir(destination.path()).expect("read destination").count(), 0);
}

#[test]
fn should_reject_archive_above_expanded_byte_limit_and_remove_staging() {
    let archive = source_zip(&[("tesseract-pinned/CMakeLists.txt", b"123456789")]);
    let archive = VerifiedArtifact {
        path: "verified.zip".into(),
        bytes: archive,
        downloaded: true,
    };
    let root = tempdir().expect("create test root");
    let third_party = root.path().join("third-party");

    let error = prepare_source_tree(&third_party, "tesseract", &archive, |bytes, destination| {
        extract_source_archive(
            bytes,
            destination,
            ARCHIVE_ROOT,
            ArchiveLimits {
                max_entries: 2,
                max_uncompressed_bytes: 8,
            },
        )
    })
    .expect_err("expanded byte limit must reject archive");

    assert_eq!(error.kind(), std::io::ErrorKind::InvalidData);
    assert!(!third_party.join("tesseract").exists());
    assert_eq!(
        fs::read_dir(&third_party).expect("read third-party directory").count(),
        0
    );
}

#[test]
fn should_accept_archive_at_exact_expanded_byte_limit() {
    let archive = source_zip(&[("tesseract-pinned/CMakeLists.txt", b"12345678")]);
    let destination = tempdir().expect("create extraction destination");

    extract_source_archive(
        &archive,
        destination.path(),
        ARCHIVE_ROOT,
        ArchiveLimits {
            max_entries: 1,
            max_uncompressed_bytes: 8,
        },
    )
    .expect("exact expanded byte limit should pass");

    assert_eq!(
        fs::read(destination.path().join("CMakeLists.txt")).expect("read extracted file"),
        b"12345678"
    );
}

fn source_zip(entries: &[(&str, &[u8])]) -> Vec<u8> {
    let cursor = Cursor::new(Vec::new());
    let mut archive = zip::ZipWriter::new(cursor);
    let options = SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
    for (name, bytes) in entries {
        archive.start_file(*name, options).expect("start ZIP entry");
        archive.write_all(bytes).expect("write ZIP entry");
    }
    archive.finish().expect("finish ZIP archive").into_inner()
}

mod helpers;

use helpers::extract_bytes_document;
use rusqlite::Connection;
use tempfile::TempDir;
use xberg::extractors::security::SecurityError;
use xberg::{
    ExtractInput, ExtractionConfig, OutputFormat, SecurityLimits, XbergError, detect_mime_type,
    detect_mime_type_from_bytes, extract, get_extensions_for_mime, list_supported_formats,
};

const SQLITE_MIME: &str = "application/vnd.sqlite3";
const GEOPACKAGE_MIME: &str = "application/geopackage+sqlite3";
const SQLITE_ALIAS_MIME: &str = "application/x-sqlite3";
const OCTET_STREAM_MIME: &str = "application/octet-stream";
const GEOPACKAGE_1_0_APPLICATION_ID: u32 = 0x4750_3130;
const GEOPACKAGE_1_2_APPLICATION_ID: u32 = 0x4750_4B47;

fn database_bytes(schema_and_data: &str) -> Vec<u8> {
    let directory = TempDir::new().expect("temporary database directory should be created");
    let path = directory.path().join("fixture.sqlite");
    let connection = Connection::open(&path).expect("synthetic SQLite database should open");
    connection
        .execute_batch(schema_and_data)
        .expect("synthetic SQLite schema and rows should be created");
    drop(connection);
    std::fs::read(path).expect("synthetic SQLite database should be readable")
}

fn database_bytes_with_application_id(application_id: u32, schema_and_data: &str) -> Vec<u8> {
    database_bytes(&format!("PRAGMA application_id = {application_id};\n{schema_and_data}"))
}

fn sqlite_input(bytes: Vec<u8>, filename: &str) -> ExtractInput {
    ExtractInput::from_bytes(bytes, OCTET_STREAM_MIME, Some(filename.to_string()))
}

async fn extraction_error(bytes: Vec<u8>, filename: &str, config: &ExtractionConfig) -> XbergError {
    extract(sqlite_input(bytes, filename), config)
        .await
        .expect_err("single-document extraction should propagate parser and security failures")
}

fn security_config(limits: SecurityLimits) -> ExtractionConfig {
    ExtractionConfig {
        security_limits: Some(limits),
        ..ExtractionConfig::default()
    }
}

#[test]
fn should_detect_sqlite_magic_before_the_text_fallback() {
    let bytes = database_bytes("CREATE TABLE records (id INTEGER PRIMARY KEY, value TEXT);");

    assert_eq!(detect_mime_type_from_bytes(&bytes).unwrap(), SQLITE_MIME);
}

#[test]
fn should_detect_geopackage_application_ids_from_the_sqlite_header() {
    for application_id in [GEOPACKAGE_1_0_APPLICATION_ID, GEOPACKAGE_1_2_APPLICATION_ID] {
        let bytes = database_bytes_with_application_id(
            application_id,
            "CREATE TABLE records (id INTEGER PRIMARY KEY, value TEXT);",
        );

        assert_eq!(
            detect_mime_type_from_bytes(&bytes).unwrap(),
            GEOPACKAGE_MIME,
            "application ID {application_id:#010x} should identify a GeoPackage"
        );
    }
}

#[test]
fn should_register_all_sqlite_and_geopackage_extensions_case_insensitively() {
    assert_eq!(
        get_extensions_for_mime(SQLITE_MIME).unwrap(),
        ["db", "sqlite", "sqlite3"]
    );
    assert_eq!(
        get_extensions_for_mime(SQLITE_ALIAS_MIME).unwrap(),
        ["db", "sqlite", "sqlite3"]
    );
    assert_eq!(get_extensions_for_mime(GEOPACKAGE_MIME).unwrap(), ["gpkg", "gpkx"]);
    let advertised: std::collections::HashMap<String, String> = list_supported_formats()
        .into_iter()
        .map(|format| (format.extension, format.mime_type))
        .collect();
    for (extension, expected_mime) in [
        ("db", SQLITE_MIME),
        ("sqlite", SQLITE_MIME),
        ("sqlite3", SQLITE_MIME),
        ("gpkg", GEOPACKAGE_MIME),
        ("gpkx", GEOPACKAGE_MIME),
    ] {
        assert_eq!(advertised.get(extension).map(String::as_str), Some(expected_mime));
        assert_eq!(
            detect_mime_type(format!("fixture.{}", extension.to_ascii_uppercase()), false).unwrap(),
            expected_mime
        );
    }
}

#[test]
fn should_route_the_sqlite_mime_alias_to_the_same_extractor() {
    xberg::extractors::ensure_initialized().expect("built-in extractor registration should succeed");
    let registry = xberg::plugins::registry::get_document_extractor_registry();
    let registry = registry.read();

    let canonical = registry
        .get(SQLITE_MIME)
        .unwrap_or_else(|error| panic!("{SQLITE_MIME} should route: {error}"));
    let alias = registry
        .get(SQLITE_ALIAS_MIME)
        .unwrap_or_else(|error| panic!("{SQLITE_ALIAS_MIME} should route: {error}"));
    assert_eq!(alias.name(), canonical.name());
}

#[tokio::test]
async fn should_extract_user_tables_in_deterministic_name_and_row_order() {
    let bytes = database_bytes(
        r#"
        CREATE TABLE zeta (key TEXT, value INTEGER);
        INSERT INTO zeta VALUES ('last', 9), ('later', 10);
        CREATE TABLE alpha (
            id INTEGER,
            label TEXT,
            amount REAL,
            optional TEXT,
            payload BLOB
        );
        INSERT INTO alpha VALUES (1, 'A|B', 3.5, NULL, X'00017F');
        "#,
    );

    let config = ExtractionConfig {
        output_format: OutputFormat::Markdown,
        ..ExtractionConfig::default()
    };
    let document = extract_bytes_document(&bytes, SQLITE_MIME, &config)
        .await
        .expect("SQLite extraction should succeed");

    assert_eq!(document.mime_type, SQLITE_MIME);
    assert_eq!(document.tables.len(), 2);
    assert_eq!(
        document.tables[0].cells,
        vec![
            vec!["id", "label", "amount", "optional", "payload"],
            vec!["1", "A|B", "3.5", "", "[BLOB: 3 bytes]"],
        ]
    );
    assert_eq!(
        document.tables[0].markdown,
        "| id | label | amount | optional | payload |\n\
         | --- | --- | --- | --- | --- |\n\
         | 1 | A\\|B | 3.5 |  | [BLOB: 3 bytes] |\n"
    );
    assert_eq!(
        document.tables[1].cells,
        vec![vec!["key", "value"], vec!["last", "9"], vec!["later", "10"]]
    );
    assert_eq!(
        document.content,
        "## alpha\n\n\
         | id | label | amount | optional | payload |\n\
         | --- | --- | --- | --- | --- |\n\
         | 1 | A\\|B | 3.5 |  | [BLOB: 3 bytes] |\n\n\
         ## zeta\n\n\
         | key | value |\n\
         | --- | --- |\n\
         | last | 9 |\n\
         | later | 10 |\n"
    );
}

#[tokio::test]
async fn should_refine_geopackage_mime_from_valid_signatures_not_the_filename() {
    for application_id in [GEOPACKAGE_1_0_APPLICATION_ID, GEOPACKAGE_1_2_APPLICATION_ID] {
        let geopackage = database_bytes_with_application_id(
            application_id,
            r#"
            CREATE TABLE gpkg_contents (table_name TEXT PRIMARY KEY, data_type TEXT);
            INSERT INTO gpkg_contents VALUES ('roads', 'features');
            CREATE TABLE roads (id INTEGER, name TEXT);
            INSERT INTO roads VALUES (1, 'Main Street');
            "#,
        );

        let document = extract(sqlite_input(geopackage, "misleading.db"), &ExtractionConfig::default())
            .await
            .expect("GeoPackage extraction should return an envelope");
        assert_eq!(document.summary.results, 1);
        assert_eq!(document.results[0].mime_type, GEOPACKAGE_MIME);
        assert_eq!(document.results[0].tables.len(), 1);
        assert_eq!(document.results[0].tables[0].cells[0], vec!["id", "name"]);
    }

    let ordinary_sqlite = database_bytes("CREATE TABLE records (id INTEGER); INSERT INTO records VALUES (1);");

    let sqlite_document = extract(
        sqlite_input(ordinary_sqlite, "misleading.gpkg"),
        &ExtractionConfig::default(),
    )
    .await
    .expect("ordinary SQLite extraction should return an envelope");
    assert_eq!(sqlite_document.summary.results, 1);
    assert_eq!(sqlite_document.results[0].mime_type, SQLITE_MIME);
}

#[tokio::test]
async fn should_not_treat_a_reserved_table_name_as_a_geopackage_signature() {
    let spoof = database_bytes(
        r#"
        CREATE TABLE gpkg_contents (table_name TEXT PRIMARY KEY, data_type TEXT);
        INSERT INTO gpkg_contents VALUES ('records', 'attributes');
        CREATE TABLE records (id INTEGER, value TEXT);
        INSERT INTO records VALUES (1, 'ordinary SQLite');
        "#,
    );

    assert_eq!(detect_mime_type_from_bytes(&spoof).unwrap(), SQLITE_MIME);
    let document = extract(sqlite_input(spoof, "spoof.gpkg"), &ExtractionConfig::default())
        .await
        .expect("ordinary SQLite extraction should return an envelope");
    assert_eq!(document.summary.results, 1);
    assert_eq!(document.results[0].mime_type, SQLITE_MIME);
    assert_eq!(document.results[0].tables.len(), 2);
    assert_eq!(
        document.results[0].tables[0].cells,
        vec![vec!["table_name", "data_type"], vec!["records", "attributes"],]
    );
    assert_eq!(
        document.results[0].tables[1].cells,
        vec![vec!["id", "value"], vec!["1", "ordinary SQLite"]]
    );
}

#[tokio::test]
async fn should_exclude_internal_metadata_views_and_virtual_tables() {
    let bytes = database_bytes_with_application_id(
        GEOPACKAGE_1_2_APPLICATION_ID,
        r#"
        CREATE TABLE gpkg_contents (table_name TEXT PRIMARY KEY, data_type TEXT);
        INSERT INTO gpkg_contents VALUES ('public_data', 'attributes');
        CREATE TABLE public_data (id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT);
        INSERT INTO public_data (value) VALUES ('visible');
        CREATE TABLE gpkg_spatial_ref_sys (srs_id INTEGER, organization TEXT);
        INSERT INTO gpkg_spatial_ref_sys VALUES (4326, 'EPSG');
        CREATE TABLE rtree_public_data_geom (id INTEGER, min_x REAL, max_x REAL);
        INSERT INTO rtree_public_data_geom VALUES (1, 0.0, 1.0);
        CREATE VIEW leaked_view AS SELECT 'hidden' AS secret;
        CREATE VIRTUAL TABLE search_index USING fts5(body);
        INSERT INTO search_index VALUES ('indexed but hidden');
        CREATE TABLE gpkgish (value TEXT);
        INSERT INTO gpkgish VALUES ('prefix is not reserved');
        "#,
    );

    let document = extract_bytes_document(&bytes, SQLITE_MIME, &ExtractionConfig::default())
        .await
        .expect("GeoPackage extraction should succeed");

    assert_eq!(document.mime_type, GEOPACKAGE_MIME);
    assert_eq!(document.tables.len(), 2);
    assert_eq!(document.tables[0].cells[0], vec!["value"]);
    assert_eq!(document.tables[0].cells[1], vec!["prefix is not reserved"]);
    assert_eq!(document.tables[1].cells[0], vec!["id", "value"]);
    assert_eq!(document.tables[1].cells[1], vec!["1", "visible"]);
    assert!(!document.content.contains("EPSG"));
    assert!(!document.content.contains("hidden"));
    assert!(!document.content.contains("indexed but hidden"));
}

#[tokio::test]
async fn should_quote_table_and_column_identifiers_without_executing_them() {
    let bytes = database_bytes(
        r#"
        CREATE TABLE safe (value TEXT);
        INSERT INTO safe VALUES ('still present');
        CREATE TABLE "odd""name; DROP TABLE safe; --" ("value""name" TEXT);
        INSERT INTO "odd""name; DROP TABLE safe; --" VALUES ('quoted value');
        "#,
    );

    let document = extract_bytes_document(&bytes, SQLITE_MIME, &ExtractionConfig::default())
        .await
        .expect("quoted identifiers should extract safely");

    assert_eq!(document.tables.len(), 2);
    assert_eq!(
        document.tables[0].cells,
        vec![vec!["value\"name"], vec!["quoted value"]]
    );
    assert_eq!(document.tables[1].cells, vec![vec!["value"], vec!["still present"]]);
}

#[tokio::test]
async fn should_skip_virtual_generated_columns_before_query_evaluation() {
    let bytes = database_bytes(
        "CREATE TABLE records (\
            value TEXT, \
            bomb TEXT GENERATED ALWAYS AS (hex(zeroblob(1000000))) VIRTUAL\
         ); \
         INSERT INTO records (value) VALUES ('safe');",
    );
    let config = security_config(SecurityLimits {
        max_entity_length: 16,
        ..SecurityLimits::default()
    });

    let document = extract_bytes_document(&bytes, SQLITE_MIME, &config)
        .await
        .expect("virtual generated columns should be excluded before evaluation");

    assert_eq!(document.tables.len(), 1);
    assert_eq!(document.tables[0].cells, vec![vec!["value"], vec!["safe"]]);
    assert!(!document.content.contains("bomb"));
}

#[tokio::test]
async fn should_not_evaluate_a_generated_column_that_shadows_rowid_ordering() {
    let bytes = database_bytes(
        "CREATE TABLE records (\
            value INTEGER, \
            rowid INTEGER GENERATED ALWAYS AS (-value) VIRTUAL\
         ); \
         INSERT INTO records (value) VALUES (1), (2);",
    );
    let config = security_config(SecurityLimits {
        max_entity_length: 16,
        ..SecurityLimits::default()
    });

    let document = extract_bytes_document(&bytes, SQLITE_MIME, &config)
        .await
        .expect("rowid ordering should not resolve to a generated column");

    assert_eq!(document.tables.len(), 1);
    assert_eq!(document.tables[0].cells, vec![vec!["value"], vec!["1"], vec!["2"]]);
}

#[tokio::test]
async fn should_fall_back_to_visible_columns_when_all_rowid_aliases_are_shadowed() {
    let bytes = database_bytes(
        "CREATE TABLE records (\
            value INTEGER, \
            rowid INTEGER GENERATED ALWAYS AS (-value) VIRTUAL, \
            _rowid_ INTEGER GENERATED ALWAYS AS (-value) VIRTUAL, \
            oid INTEGER GENERATED ALWAYS AS (-value) VIRTUAL\
         ); \
         INSERT INTO records (value) VALUES (2), (1);",
    );

    let document = extract_bytes_document(&bytes, SQLITE_MIME, &ExtractionConfig::default())
        .await
        .expect("visible columns should provide safe deterministic ordering");

    assert_eq!(document.tables.len(), 1);
    assert_eq!(document.tables[0].cells, vec![vec!["value"], vec!["1"], vec!["2"]]);
}

#[tokio::test]
async fn should_report_a_parsing_error_for_a_malformed_sqlite_database() {
    let mut malformed = b"SQLite format 3\0".to_vec();
    malformed.resize(512, 0);

    let error = extraction_error(malformed, "broken.sqlite", &ExtractionConfig::default()).await;

    assert!(matches!(&error, XbergError::Parsing { .. }));
    assert!(
        error.to_string().to_ascii_lowercase().contains("sqlite"),
        "unexpected error message: {error}"
    );
}

#[tokio::test]
async fn should_reject_the_database_before_extraction_when_input_exceeds_the_content_limit() {
    let bytes = database_bytes("CREATE TABLE records (value TEXT); INSERT INTO records VALUES ('visible');");
    let byte_count = bytes.len();
    let config = security_config(SecurityLimits {
        max_content_size: byte_count - 1,
        ..SecurityLimits::default()
    });

    let error = extraction_error(bytes, "large.sqlite", &config).await;

    assert!(matches!(&error, XbergError::Security { .. }));
    assert!(
        error.to_string().contains(&format!("{byte_count} bytes")),
        "unexpected error message: {error}"
    );
}

#[tokio::test]
async fn should_enforce_the_table_cell_limit_across_the_database() {
    let bytes = database_bytes("CREATE TABLE records (left TEXT, right TEXT); INSERT INTO records VALUES ('a', 'b');");
    let config = security_config(SecurityLimits {
        max_table_cells: 3,
        ..SecurityLimits::default()
    });

    let error = extraction_error(bytes, "cells.sqlite", &config).await;

    assert!(matches!(&error, XbergError::Security { .. }));
    let source = std::error::Error::source(&error)
        .and_then(|source| source.downcast_ref::<SecurityError>())
        .expect("security violation should retain the structured source error");
    assert!(
        matches!(source, SecurityError::TooManyCells { cells: 4, max: 3 }),
        "unexpected structured security source: {source:?}"
    );
}

#[tokio::test]
async fn should_enforce_the_entity_limit_for_column_names_and_cell_values() {
    let column_bytes = database_bytes("CREATE TABLE t (column_name_that_is_too_long TEXT);");
    let column_config = security_config(SecurityLimits {
        max_entity_length: 16,
        ..SecurityLimits::default()
    });
    let column_error = extraction_error(column_bytes, "column.sqlite", &column_config).await;

    assert!(matches!(&column_error, XbergError::Security { .. }));
    assert!(column_error.to_string().contains("Entity too long: 28 chars (max: 16)"));

    let value_bytes =
        database_bytes("CREATE TABLE t (value TEXT); INSERT INTO t VALUES ('cell_value_that_is_too_long');");
    let value_config = security_config(SecurityLimits {
        max_entity_length: 16,
        ..SecurityLimits::default()
    });

    let value_error = extraction_error(value_bytes, "value.sqlite", &value_config).await;

    assert!(matches!(&value_error, XbergError::Security { .. }));
    assert!(value_error.to_string().contains("Entity too long: 27 chars (max: 16)"));
}

#[tokio::test]
async fn should_enforce_the_iteration_limit_across_schema_and_row_scans() {
    let bytes = database_bytes("CREATE TABLE records (value TEXT); INSERT INTO records VALUES ('one'), ('two');");
    let config = security_config(SecurityLimits {
        max_iterations: 2,
        ..SecurityLimits::default()
    });

    let error = extraction_error(bytes, "iterations.sqlite", &config).await;

    assert!(matches!(&error, XbergError::Security { .. }));
    assert!(error.to_string().contains("Too many iterations: 3 (max: 2)"));
}

#[tokio::test]
async fn should_count_reserved_table_schema_rows_towards_the_iteration_limit() {
    let bytes = database_bytes(
        "CREATE TABLE gpkg_first (value TEXT); \
         CREATE TABLE gpkg_second (value TEXT);",
    );
    let config = security_config(SecurityLimits {
        max_iterations: 1,
        ..SecurityLimits::default()
    });

    let error = extraction_error(bytes, "reserved.sqlite", &config).await;

    assert!(matches!(&error, XbergError::Security { .. }));
    assert!(error.to_string().contains("Too many iterations: 2 (max: 1)"));
}

#[tokio::test]
async fn should_not_charge_intrinsic_sqlite_schema_names_to_user_entity_limits() {
    let bytes = database_bytes("CREATE TABLE t (c TEXT);");
    let config = security_config(SecurityLimits {
        max_entity_length: 1,
        ..SecurityLimits::default()
    });

    let document = extract_bytes_document(&bytes, SQLITE_MIME, &config)
        .await
        .expect("intrinsic SQLite metadata should not consume user entity limits");

    assert_eq!(document.tables.len(), 1);
    assert_eq!(document.tables[0].cells, vec![vec!["c"]]);
}

#[tokio::test]
async fn should_count_without_rowid_primary_key_schema_rows_towards_the_iteration_limit() {
    let bytes = database_bytes("CREATE TABLE records (key TEXT PRIMARY KEY) WITHOUT ROWID;");
    let config = security_config(SecurityLimits {
        max_iterations: 1,
        ..SecurityLimits::default()
    });

    let error = extraction_error(bytes, "without-rowid.sqlite", &config).await;

    assert!(matches!(&error, XbergError::Security { .. }));
    assert!(error.to_string().contains("Too many iterations: 2 (max: 1)"));
}

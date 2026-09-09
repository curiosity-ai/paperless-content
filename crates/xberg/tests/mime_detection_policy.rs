use xberg::{ExtractInput, ExtractInputKind, ExtractionConfig, FileExtractionConfig, MimeDetectionPolicy, extract};

const JSON_CONTENT: &[u8] = br#"{"policy":"content"}"#;

fn named_bytes(filename: &str, mime_type: Option<&str>) -> ExtractInput {
    ExtractInput {
        kind: ExtractInputKind::Bytes,
        bytes: Some(JSON_CONTENT.to_vec()),
        filename: Some(filename.to_string()),
        mime_type: mime_type.map(str::to_string),
        config: None,
        uri: None,
    }
}

async fn extracted_mime(input: ExtractInput, config: &ExtractionConfig) -> String {
    let output = extract(input, config)
        .await
        .expect("MIME policy extraction should succeed");
    assert_eq!(output.summary.inputs, 1);
    assert_eq!(output.summary.results, 1);
    assert_eq!(output.summary.errors, 0);
    assert_eq!(output.results.len(), 1);
    output.results[0].mime_type.to_string()
}

async fn extracted_local_mime(filename: &str, mime_type: Option<&str>, config: &ExtractionConfig) -> String {
    let directory = tempfile::tempdir().expect("temporary directory should be created");
    let path = directory.path().join(filename);
    std::fs::write(&path, JSON_CONTENT).expect("test document should be written");
    let mut input = ExtractInput::from_uri(path.to_string_lossy());
    input.mime_type = mime_type.map(str::to_string);
    extracted_mime(input, config).await
}

#[tokio::test]
async fn should_prefer_content_with_the_default_policy() {
    let config = ExtractionConfig::default();

    assert_eq!(config.mime_detection_policy, MimeDetectionPolicy::PreferContent);
    assert_eq!(
        extracted_mime(named_bytes("document.txt", None), &config).await,
        "application/json"
    );
    assert_eq!(
        extracted_local_mime("document.txt", None, &config).await,
        "application/json"
    );
}

#[tokio::test]
async fn should_trust_a_supported_filename_extension_when_requested() {
    let config = ExtractionConfig {
        mime_detection_policy: MimeDetectionPolicy::TrustExtension,
        ..Default::default()
    };

    assert_eq!(
        extracted_mime(named_bytes("document.txt", None), &config).await,
        "text/plain"
    );
    assert_eq!(extracted_local_mime("document.txt", None, &config).await, "text/plain");
}

#[tokio::test]
async fn should_ignore_a_supported_filename_extension_in_content_only_mode() {
    let config = ExtractionConfig {
        mime_detection_policy: MimeDetectionPolicy::ContentOnly,
        ..Default::default()
    };

    assert_eq!(
        extracted_mime(named_bytes("document.txt", None), &config).await,
        "application/json"
    );
    assert_eq!(
        extracted_local_mime("document.txt", None, &config).await,
        "application/json"
    );
}

#[tokio::test]
async fn should_fall_back_to_content_for_an_unknown_extension_under_every_policy() {
    for policy in [
        MimeDetectionPolicy::PreferContent,
        MimeDetectionPolicy::TrustExtension,
        MimeDetectionPolicy::ContentOnly,
    ] {
        let config = ExtractionConfig {
            mime_detection_policy: policy,
            ..Default::default()
        };

        assert_eq!(
            extracted_mime(named_bytes("document.unknown-extension", None), &config).await,
            "application/json",
            "unexpected MIME for policy {policy:?}"
        );
        assert_eq!(
            extracted_local_mime("document.unknown-extension", None, &config).await,
            "application/json",
            "unexpected local MIME for policy {policy:?}"
        );
    }
}

#[tokio::test]
async fn should_keep_an_explicit_caller_mime_authoritative_under_every_policy() {
    for policy in [
        MimeDetectionPolicy::PreferContent,
        MimeDetectionPolicy::TrustExtension,
        MimeDetectionPolicy::ContentOnly,
    ] {
        let config = ExtractionConfig {
            mime_detection_policy: policy,
            ..Default::default()
        };

        assert_eq!(
            extracted_mime(named_bytes("document.json", Some("text/plain")), &config).await,
            "text/plain",
            "unexpected MIME for policy {policy:?}"
        );
        assert_eq!(
            extracted_local_mime("document.json", Some("text/plain"), &config).await,
            "text/plain",
            "unexpected local MIME for policy {policy:?}"
        );
    }
}

#[tokio::test]
async fn should_apply_a_per_file_mime_policy_override() {
    let config = ExtractionConfig {
        mime_detection_policy: MimeDetectionPolicy::TrustExtension,
        ..Default::default()
    };
    let mut input = named_bytes("document.txt", None);
    input.config = Some(FileExtractionConfig {
        mime_detection_policy: Some(MimeDetectionPolicy::ContentOnly),
        ..Default::default()
    });

    assert_eq!(extracted_mime(input, &config).await, "application/json");
}

#[test]
fn should_deserialize_the_default_mime_policy_when_the_field_is_absent() {
    let config: ExtractionConfig = serde_json::from_str("{}").expect("default config JSON should deserialize");

    assert_eq!(config.mime_detection_policy, MimeDetectionPolicy::PreferContent);
}

#[test]
fn should_serialize_mime_detection_policies_as_snake_case() {
    let cases = [
        (MimeDetectionPolicy::PreferContent, "\"prefer_content\""),
        (MimeDetectionPolicy::TrustExtension, "\"trust_extension\""),
        (MimeDetectionPolicy::ContentOnly, "\"content_only\""),
    ];

    for (policy, expected) in cases {
        assert_eq!(
            serde_json::to_string(&policy).expect("MIME policy should serialize"),
            expected
        );
    }
}

#[test]
fn should_deserialize_mime_detection_policies_from_snake_case() {
    let cases = [
        ("\"prefer_content\"", MimeDetectionPolicy::PreferContent),
        ("\"trust_extension\"", MimeDetectionPolicy::TrustExtension),
        ("\"content_only\"", MimeDetectionPolicy::ContentOnly),
    ];

    for (encoded, expected) in cases {
        assert_eq!(
            serde_json::from_str::<MimeDetectionPolicy>(encoded).expect("MIME policy should deserialize"),
            expected
        );
    }
}

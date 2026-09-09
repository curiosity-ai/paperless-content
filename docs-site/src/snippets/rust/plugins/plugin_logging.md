```rust title="Rust"
use xberg::plugins::{Plugin, DocumentExtractor};
use xberg::{ExtractInput, ExtractedDocument, ExtractionConfig, Result};
use async_trait::async_trait;
use tracing::{debug, info, warn};

struct MyPlugin;

impl Plugin for MyPlugin {
    fn name(&self) -> &str {
        "my-plugin"
    }

    fn version(&self) -> String {
        "1.0.0".to_string()
    }

    fn initialize(&self) -> Result<()> {
        info!(plugin = self.name(), "initializing plugin");
        Ok(())
    }

    fn shutdown(&self) -> Result<()> {
        info!(plugin = self.name(), "shutting down plugin");
        Ok(())
    }
}

#[async_trait]
impl DocumentExtractor for MyPlugin {
    #[tracing::instrument(
        name = "xberg::plugin_extract",
        level = "debug",
        skip_all,
        fields(
            plugin = self.name(),
            mime_type = tracing::field::Empty,
            input_len = tracing::field::Empty,
        )
    )]
    async fn extract(
        &self,
        input: ExtractInput,
        _config: &ExtractionConfig,
    ) -> Result<ExtractedDocument> {
        let mime_type = input.mime_type.clone().unwrap_or_default();
        let bytes = input.bytes.unwrap_or_default();
        let span = tracing::Span::current();
        span.record("mime_type", mime_type.as_str());
        span.record("input_len", bytes.len());
        debug!("extracting document");

        let result = ExtractedDocument::default();

        if result.content.is_empty() {
            warn!("extraction produced empty content");
        }

        Ok(result)
    }

    fn supported_mime_types(&self) -> &[&str] {
        &["application/octet-stream"]
    }
}
```

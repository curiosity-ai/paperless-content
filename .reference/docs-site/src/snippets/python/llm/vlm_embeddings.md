```python title="Python"
# NOTE: The Python binding has no standalone embed() entry point — only
# embedding-backend registration (register_embedding_backend,
# list_embedding_backends, unregister_embedding_backend) and the EmbeddingConfig
# type are exposed. The `embed` name in the type stubs is a METHOD on the
# EmbeddingBackend protocol you implement, not a module-level function, so
# `from xberg import embed` fails at import time. VLM/LLM embeddings are produced
# per chunk during extraction by attaching this config to
# ExtractionConfig.chunking — see the Rust tab for the standalone dispatcher.
from xberg import EmbeddingConfig, EmbeddingModelType, LlmConfig

config = EmbeddingConfig(
    model=EmbeddingModelType.llm(
        LlmConfig(model="openai/text-embedding-3-small")
    ),
    normalize=True,
)
```

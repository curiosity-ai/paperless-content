---
name: polyrepo-boundaries
description: >-
  Decide which xberg-io repository owns a cross-repository fix or API, and coordinate compatible changes across
  Xberg, Alef, enterprise, crawler, LLM, and OCR repositories. Load when work spans sibling repos; do not use for a
  self-contained Xberg edit.
---

# Polyrepo boundaries

Each sibling under the workspace root is an independent Git repository with its own history, release, CI, and
governance. Never include changes from two repositories in one commit or assume pushing Xberg publishes a sibling
dependency.

## Ownership

- `xberg`: reusable Rust document-intelligence primitives, public Rust API, CLI/server surfaces, bindings, and Xberg
  CI/release packaging.
- `xberg-enterprise`: product and deployment behavior that consumes Xberg. Check it before removing or reshaping Rust
  primitives, even when the symbol is not exposed in language bindings.
- `alef`: binding, documentation, snippet, and e2e generation defects. Work around a generator defect in Xberg only
  when necessary; report and fix the reusable cause in Alef.
- `liter-llm`: reusable LLM client behavior, provider request construction, endpoint validation, and network-security
  controls owned by that client.
- `crawlberg`: crawling, URL retrieval, and crawler-specific network policy. Put SSRF protection at the component that
  performs the outbound request; callers may add stricter policy but cannot replace the transport boundary.
- `sceptre`: reusable Sceptre OCR models/runtime. Xberg owns its adapter, configuration, and integration behavior.

One boundary is in-repo rather than a sibling repository:

- `dotnet/` — **X-Ray.Content**, a native C# port of the extraction engine, published to NuGet as
  `X-Ray.Content` (namespace `XRay.Content`) and the content-extraction member of a planned X-Ray family of
  .NET libraries. It is not a binding over the Rust core and shares no code with it, so a Rust change never
  propagates there automatically and a change under `dotnet/` never affects the Rust crate or its bindings. Treat it
  as its own project: read `dotnet/Claude.md` before touching it, keep `crates/` untouched from inside it (upstream
  merges depend on that), and do not confuse it with upstream's `packages/csharp` FFI binding (`XbergIo.Xberg`),
  which is a wrapper over the Rust core and unrelated.

## API placement

Keep reusable primitives in the Rust crate when enterprise or Rust callers need them. Bindings should expose a
primitive only when it forms a coherent supported language API; Rust-only public functionality does not need an FFI
wrapper. Compatibility and deprecation policy applies to public APIs, not private internals.

For a cross-repo fix, identify the lowest owning layer, coordinate the dependency/release order, and verify each
repository independently. Commit and publish the dependency before updating a consumer to a version that contains the
fix.

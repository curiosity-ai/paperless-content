```csharp title="C#"
using Xberg;
using System;
using System.Collections.Generic;
using System.Text;

CustomExtractorTests.VerifyExtractsJsonContent();

public static class CustomExtractorTests
{
    public static void VerifyExtractsJsonContent()
    {
        var extractor = new CustomJsonExtractor();
        var input = new ExtractInput
        {
            Kind = ExtractInputKind.Bytes,
            Bytes = Encoding.UTF8.GetBytes("{\"message\": \"Hello, world!\"}"),
            MimeType = "application/json",
        };

        var result = extractor.Extract(input, new ExtractionConfig());

        if (!result.Content.Contains("Hello, world!", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected extracted JSON content was missing.");
        }
        if (result.MimeType != "application/json")
        {
            throw new InvalidOperationException($"Expected application/json, got {result.MimeType}.");
        }
    }
}

public sealed class CustomJsonExtractor : IDocumentExtractor
{
    public string Name => "custom-json";
    public string Version => "1.0.0";
    public int Priority => 50;
    public List<string> SupportedMimeTypes => new() { "application/json" };

    public void Initialize() { }
    public void Shutdown() { }

    public bool CanHandle(string path, string mimeType) => mimeType == "application/json";

    public ExtractedDocument Extract(ExtractInput input, ExtractionConfig config)
    {
        var content = input.Bytes is null ? "" : Encoding.UTF8.GetString(input.Bytes);
        return new ExtractedDocument
        {
            Content = content,
            MimeType = input.MimeType ?? "application/json",
            Metadata = new Metadata(),
        };
    }
}
```

```csharp title="C#"
using Xberg;
using System;
using System.Collections.Generic;
using System.Text;

public class MyExtractorPlugin : IDocumentExtractor
{
    private readonly Action<string> _writeLog;

    public MyExtractorPlugin(Action<string> writeLog)
    {
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
    }

    public string Name => "my-plugin";
    public string Version => "1.0.0";
    public int Priority => 50;
    public List<string> SupportedMimeTypes => new() { "text/plain" };

    public void Initialize()
    {
        _writeLog($"INFO Initializing plugin: {Name}");
    }

    public void Shutdown()
    {
        _writeLog($"INFO Shutting down plugin: {Name}");
    }

    public bool CanHandle(string path, string mimeType) => mimeType == "text/plain";

    public ExtractedDocument Extract(ExtractInput input, ExtractionConfig config)
    {
        _writeLog($"INFO Extracting {input.MimeType} ({input.Bytes?.Length ?? 0} bytes)");
        var content = input.Bytes is null ? "" : Encoding.UTF8.GetString(input.Bytes);
        if (string.IsNullOrEmpty(content))
        {
            _writeLog("WARN Extraction resulted in empty content");
        }
        return new ExtractedDocument
        {
            Content = content,
            MimeType = input.MimeType ?? "text/plain",
            Metadata = new Metadata(),
        };
    }
}
```

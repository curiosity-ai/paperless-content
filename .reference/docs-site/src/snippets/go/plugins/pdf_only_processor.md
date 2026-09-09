```go title="Go"
package main

import (
	"log"

	"github.com/xberg-io/xberg/packages/go"
)

type pdfOnlyProcessor struct{}

func (processor *pdfOnlyProcessor) Name() string       { return "pdf_only_processor" }
func (processor *pdfOnlyProcessor) Version() string    { return "1.0.0" }
func (processor *pdfOnlyProcessor) Initialize() error  { return nil }
func (processor *pdfOnlyProcessor) Shutdown() error    { return nil }
func (processor *pdfOnlyProcessor) Priority() int32    { return 70 }
func (processor *pdfOnlyProcessor) ProcessingStage() xberg.ProcessingStage {
	return xberg.ProcessingStageMiddle
}
func (processor *pdfOnlyProcessor) ShouldProcess(
	result xberg.ExtractedDocument,
	_ xberg.ExtractionConfig,
) bool {
	return result.MimeType == "application/pdf"
}
func (processor *pdfOnlyProcessor) EstimatedDurationMs(_ xberg.ExtractedDocument) uint64 {
	return 1
}
func (processor *pdfOnlyProcessor) Process(
	result xberg.ExtractedDocument,
	_ xberg.ExtractionConfig,
) error {
	log.Printf("Processing PDF with %d tables", len(result.Tables))
	return nil
}

func main() {
	processor := &pdfOnlyProcessor{}
	if err := xberg.RegisterPostProcessor(processor); err != nil {
		log.Fatalf("register post-processor: %v", err)
	}
	defer func() {
		if err := xberg.UnregisterPostProcessor(processor.Name()); err != nil {
			log.Printf("unregister post-processor: %v", err)
		}
	}()

	for _, path := range []string{"document.pdf", "image.jpg", "spreadsheet.xlsx"} {
		input := xberg.ExtractInputFromURI(path)
		result, err := xberg.Extract(*input, xberg.ExtractionConfig{})
		if err != nil {
			log.Printf("extract %s: %v", path, err)
			continue
		}
		log.Printf("Extracted %s as %s", path, result.Results[0].MimeType)
	}
}
```

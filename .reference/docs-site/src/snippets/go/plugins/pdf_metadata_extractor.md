```go title="Go - PDF metadata observer"
package main

import (
	"log"
	"sync/atomic"

	"github.com/xberg-io/xberg/packages/go"
)

type pdfMetadataObserver struct {
	processedCount atomic.Int64
}

func (processor *pdfMetadataObserver) Name() string       { return "pdf_metadata_observer" }
func (processor *pdfMetadataObserver) Version() string    { return "1.0.0" }
func (processor *pdfMetadataObserver) Initialize() error  { return nil }
func (processor *pdfMetadataObserver) Shutdown() error    { return nil }
func (processor *pdfMetadataObserver) Priority() int32    { return 80 }
func (processor *pdfMetadataObserver) ProcessingStage() xberg.ProcessingStage {
	return xberg.ProcessingStageEarly
}
func (processor *pdfMetadataObserver) ShouldProcess(
	result xberg.ExtractedDocument,
	_ xberg.ExtractionConfig,
) bool {
	return result.MimeType == "application/pdf"
}
func (processor *pdfMetadataObserver) EstimatedDurationMs(_ xberg.ExtractedDocument) uint64 {
	return 1
}
func (processor *pdfMetadataObserver) Process(
	result xberg.ExtractedDocument,
	_ xberg.ExtractionConfig,
) error {
	if result.Metadata != nil && result.Metadata.Title != nil {
		log.Printf("PDF title: %s", *result.Metadata.Title)
	}
	log.Printf("PDF content length: %d", len(result.Content))
	processor.processedCount.Add(1)
	return nil
}

func main() {
	processor := &pdfMetadataObserver{}
	if err := xberg.RegisterPostProcessor(processor); err != nil {
		log.Fatalf("register post-processor: %v", err)
	}
	defer func() {
		if err := xberg.UnregisterPostProcessor(processor.Name()); err != nil {
			log.Printf("unregister post-processor: %v", err)
		}
		log.Printf("PDFs processed: %d", processor.processedCount.Load())
	}()

	input := xberg.ExtractInputFromURI("document.pdf")
	result, err := xberg.Extract(*input, xberg.ExtractionConfig{})
	if err != nil {
		log.Fatalf("extract PDF: %v", err)
	}
	log.Printf("PDF MIME type: %s", result.Results[0].MimeType)
}
```

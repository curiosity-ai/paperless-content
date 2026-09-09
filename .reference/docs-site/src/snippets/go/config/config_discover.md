```go title="Go"
package main

import (
	"log"
	"os"
	"os/exec"
)

func main() {
	command := exec.Command("xberg", "extract", "document.pdf")
	command.Stdout = os.Stdout
	command.Stderr = os.Stderr
	if err := command.Run(); err != nil {
		log.Fatalf("extract with automatically discovered config: %v", err)
	}
}
```

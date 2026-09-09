```typescript title="config_discovery.ts"
/// <reference types="node" />
import { existsSync, readFileSync } from "node:fs";
import { ExtractInputKind, extract, type ExtractionConfig } from "@xberg-io/xberg";

const input = {
  kind: ExtractInputKind.Uri,
  uri: "document.pdf",
};

const configPath = "xberg.json";

if (existsSync(configPath)) {
  console.log("Found configuration file");
  const config = JSON.parse(readFileSync(configPath, "utf8")) as ExtractionConfig;
  const output = await extract(input, config);
  console.log(output.results?.[0]?.content);
} else {
  console.log("No configuration file found, using defaults");
  const output = await extract(input);
  console.log(output.results?.[0]?.content);
}
```

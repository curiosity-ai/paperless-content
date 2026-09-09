```typescript title="TypeScript"
/// <reference types="node" />
import { spawn } from "node:child_process";
import * as readline from "node:readline";

const mcpProcess = spawn("xberg", ["mcp"]);

const rl = readline.createInterface({
  input: mcpProcess.stdout,
  output: mcpProcess.stdin,
  terminal: false,
});

const initializeRequest = {
  jsonrpc: "2.0",
  id: 1,
  method: "initialize",
  params: {
    protocolVersion: "2025-06-18",
    capabilities: {},
    clientInfo: { name: "xberg-example", version: "1.0.0" },
  },
};

const extractionRequest = {
  jsonrpc: "2.0",
  id: 2,
  method: "tools/call",
  params: {
    name: "extract",
    arguments: {
      input: { kind: "uri", uri: "document.pdf" },
    },
  },
};

mcpProcess.stdin.write(`${JSON.stringify(initializeRequest)}\n`);

rl.on("line", (line) => {
  const response: unknown = JSON.parse(line);
  console.log(response);

  if (typeof response !== "object" || response === null || !("id" in response)) {
    return;
  }
  if (response.id === 1) {
    mcpProcess.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" })}\n`);
    mcpProcess.stdin.write(`${JSON.stringify(extractionRequest)}\n`);
  } else if (response.id === 2) {
    mcpProcess.kill();
  }
});

mcpProcess.on("error", (err) => {
  console.error("Failed to start MCP process:", err);
});
```

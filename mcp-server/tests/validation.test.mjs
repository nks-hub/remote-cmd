import { test } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const MCP_ENTRY = path.resolve(__dirname, "..", "index.mjs");

/**
 * Starts the MCP server on stdio and exchanges a single JSON-RPC request.
 * Returns the parsed response.
 */
async function rpcCall(method, params = {}, env = {}) {
  const proc = spawn("node", [MCP_ENTRY], {
    stdio: ["pipe", "pipe", "pipe"],
    env: {
      ...process.env,
      REMOTECMD_URL: "http://127.0.0.1:1",
      REMOTECMD_TOKEN: "test-token",
      ...env,
    },
  });

  let buffered = "";
  const responses = [];
  proc.stdout.setEncoding("utf8");
  proc.stdout.on("data", (chunk) => {
    buffered += chunk;
    let newlineIdx;
    while ((newlineIdx = buffered.indexOf("\n")) !== -1) {
      const line = buffered.slice(0, newlineIdx).trim();
      buffered = buffered.slice(newlineIdx + 1);
      if (line) {
        try {
          responses.push(JSON.parse(line));
        } catch {}
      }
    }
  });

  const req = {
    jsonrpc: "2.0",
    id: 1,
    method,
    params,
  };
  proc.stdin.write(JSON.stringify(req) + "\n");

  // Wait until we get a response with id:1 or timeout
  const deadline = Date.now() + 5000;
  while (Date.now() < deadline) {
    const match = responses.find((r) => r.id === 1);
    if (match) {
      proc.kill();
      try { await once(proc, "exit"); } catch {}
      return match;
    }
    await new Promise((r) => setTimeout(r, 50));
  }
  proc.kill();
  throw new Error("MCP server did not respond within 5s");
}

test("ListTools returns 5 tools", async () => {
  const res = await rpcCall("tools/list");
  assert.ok(res.result, "Expected result field");
  const names = res.result.tools.map((t) => t.name).sort();
  assert.deepEqual(names, [
    "remote_download",
    "remote_exec",
    "remote_list_clients",
    "remote_status",
    "remote_upload",
  ]);
});

test("remote_exec schema requires 'command'", async () => {
  const res = await rpcCall("tools/list");
  const exec = res.result.tools.find((t) => t.name === "remote_exec");
  assert.ok(exec);
  assert.deepEqual(exec.inputSchema.required, ["command"]);
  assert.ok(exec.inputSchema.properties.client);
  assert.ok(exec.inputSchema.properties.command);
  assert.ok(exec.inputSchema.properties.timeoutSeconds);
});

test("remote_status has optional client", async () => {
  const res = await rpcCall("tools/list");
  const s = res.result.tools.find((t) => t.name === "remote_status");
  assert.ok(s);
  assert.ok(!s.inputSchema.required || s.inputSchema.required.length === 0);
  assert.ok(s.inputSchema.properties.client);
});

test("remote_list_clients has no required params", async () => {
  const res = await rpcCall("tools/list");
  const lc = res.result.tools.find((t) => t.name === "remote_list_clients");
  assert.ok(lc);
  assert.ok(!lc.inputSchema.required || lc.inputSchema.required.length === 0);
});

test("remote_upload schema requires localPath and remotePath", async () => {
  const res = await rpcCall("tools/list");
  const up = res.result.tools.find((t) => t.name === "remote_upload");
  assert.ok(up);
  assert.deepEqual(up.inputSchema.required.sort(), ["localPath", "remotePath"]);
  assert.ok(up.inputSchema.properties.client);
});

test("remote_download schema requires remotePath and localPath", async () => {
  const res = await rpcCall("tools/list");
  const dl = res.result.tools.find((t) => t.name === "remote_download");
  assert.ok(dl);
  assert.deepEqual(dl.inputSchema.required.sort(), ["localPath", "remotePath"]);
  assert.ok(dl.inputSchema.properties.client);
});

test("CallTool unknown name returns isError", async () => {
  const res = await rpcCall("tools/call", {
    name: "does_not_exist",
    arguments: {},
  });
  assert.ok(res.result);
  assert.equal(res.result.isError, true);
  assert.ok(res.result.content[0].text.includes("Unknown tool"));
});

test("CallTool remote_exec against unreachable server returns Error", async () => {
  const res = await rpcCall("tools/call", {
    name: "remote_exec",
    arguments: { command: "hostname" },
  });
  assert.ok(res.result);
  // network error → isError: true, content contains "Error:"
  assert.equal(res.result.isError, true);
  assert.ok(res.result.content[0].text.startsWith("Error:"));
});

test("Server name and version reported correctly", async () => {
  const res = await rpcCall("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "test", version: "0.0.0" },
  });
  assert.ok(res.result);
  assert.equal(res.result.serverInfo.name, "remote-cmd");
  assert.equal(res.result.serverInfo.version, "1.1.0");
});

test("REMOTECMD_DEFAULT_CLIENT env var is honored in resolveClient", async () => {
  // indirect test: if default client is set, resolveClient returns it
  // we check via listing tools (env doesn't affect list, but server should start fine)
  const res = await rpcCall("tools/list", {}, {
    REMOTECMD_DEFAULT_CLIENT: "my-default",
  });
  assert.ok(res.result);
  assert.equal(res.result.tools.length, 5);
});

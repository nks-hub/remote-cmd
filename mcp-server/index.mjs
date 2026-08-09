import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import http from "http";
import https from "https";
import fs from "fs";
import path from "path";

const SERVER_URL = process.env.REMOTECMD_URL || "https://localhost:7890";
const TOKEN = process.env.REMOTECMD_TOKEN || "";
const DEFAULT_CLIENT = process.env.REMOTECMD_DEFAULT_CLIENT || "";
// Kept in step with RemoteCmd.Shared/ExecLimits.cs — the relay clamps to these anyway.
const EXEC_DEFAULT_SECONDS = 60;
const EXEC_MAX_SECONDS = 3600;
const isHttps = SERVER_URL.startsWith("https");
const transport_module = isHttps ? https : http;

// Without a token every call is a failed attempt, and the relay's brute-force throttle locks this
// machine out after ten of them — for a mistake that is entirely local and knowable at startup.
if (!TOKEN) {
  console.error("REMOTECMD_TOKEN is not set. Set it to a token the relay accepts.");
  process.exit(2);
}

// The relay serves a self-signed certificate, so verification has to be relaxed — but only for
// connections to the relay. Setting NODE_TLS_REJECT_UNAUTHORIZED disabled it for the whole process,
// including anything else this server might ever talk to.
const agent = isHttps ? new https.Agent({ rejectUnauthorized: false }) : undefined;

function buildUrl(endpoint, params = {}) {
  const url = new URL(endpoint, SERVER_URL);
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== "") url.searchParams.set(k, v);
  }
  return url;
}

function apiCall(method, endpoint, body = null, params = {}, isBinary = false, timeoutMs = 300000) {
  return new Promise((resolve, reject) => {
    const url = buildUrl(endpoint, params);
    const options = {
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      method,
      timeout: timeoutMs,
      agent,
      // In the header, never the query string: URLs land in access logs, proxy logs and error
      // pages, and a leaked relay token is remote code execution.
      headers: { "X-Token": TOKEN },
    };
    if (body && !isBinary) {
      options.headers["Content-Type"] = "application/json";
    }

    const req = transport_module.request(options, (res) => {
      const chunks = [];
      res.on("data", (chunk) => chunks.push(chunk));
      res.on("end", () => {
        const buf = Buffer.concat(chunks);
        // Without this a 401, a 429 or a 500 came back to the agent as a perfectly ordinary tool
        // result, so "the command produced no output" and "the relay refused the token" looked
        // exactly alike.
        if (res.statusCode < 200 || res.statusCode >= 300) {
          reject(new Error(`relay returned HTTP ${res.statusCode}: ${buf.toString().slice(0, 500) || "(empty body)"}`));
          return;
        }
        if (isBinary && method === "GET") {
          resolve(buf);
        } else {
          try {
            resolve(JSON.parse(buf.toString()));
          } catch {
            resolve(buf.toString());
          }
        }
      });
    });

    req.on("error", reject);
    req.on("timeout", () => {
      req.destroy();
      reject(new Error("Request timeout"));
    });

    if (body) {
      req.write(typeof body === "string" ? body : JSON.stringify(body));
    }
    req.end();
  });
}

function uploadFile(localPath, remotePath, client) {
  return new Promise((resolve, reject) => {
    const fileData = fs.readFileSync(localPath);
    const url = buildUrl("/api/upload", { path: remotePath, client });

    const options = {
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      method: "POST",
      timeout: 300000,
      agent,
      headers: {
        "X-Token": TOKEN,
        "Content-Type": "application/octet-stream",
        "Content-Length": fileData.length,
      },
    };

    const req = transport_module.request(options, (res) => {
      const chunks = [];
      res.on("data", (chunk) => chunks.push(chunk));
      res.on("end", () => {
        const body = Buffer.concat(chunks).toString();
        // The relay answers 502 when the client could not write the file and 504 when it never
        // answered. Ignoring the status reported both as "Uploaded 12.4MB".
        if (res.statusCode < 200 || res.statusCode >= 300) {
          reject(new Error(`upload failed, relay returned HTTP ${res.statusCode}: ${body.slice(0, 500) || "(empty body)"}`));
          return;
        }
        try {
          resolve(JSON.parse(body));
        } catch {
          resolve(body);
        }
      });
    });

    req.on("error", reject);
    req.on("timeout", () => {
      req.destroy();
      reject(new Error("Upload timeout"));
    });

    req.write(fileData);
    req.end();
  });
}

function downloadFile(remotePath, localPath, client) {
  return new Promise((resolve, reject) => {
    const url = buildUrl("/api/download", { path: remotePath, client });

    const options = {
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      method: "GET",
      timeout: 300000,
      agent,
      headers: { "X-Token": TOKEN },
    };

    const req = transport_module.request(options, (res) => {
      const chunks = [];
      res.on("data", (chunk) => chunks.push(chunk));
      res.on("end", () => {
        const buf = Buffer.concat(chunks);
        if (res.statusCode === 200) {
          const dir = path.dirname(localPath);
          if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
          fs.writeFileSync(localPath, buf);
          resolve({ status: "ok", size: buf.length, localPath });
        } else {
          // A 504 comes back with an empty body, and JSON.parse('') threw straight into a catch
          // that produced { error: "" } — falsy, so the caller reported "Downloaded NaNMB".
          try {
            const parsed = JSON.parse(buf.toString());
            resolve(parsed && parsed.error ? parsed : { error: `relay returned HTTP ${res.statusCode}` });
          } catch {
            resolve({ error: buf.toString() || `relay returned HTTP ${res.statusCode}` });
          }
        }
      });
    });

    req.on("error", reject);
    req.on("timeout", () => {
      req.destroy();
      reject(new Error("Download timeout"));
    });

    req.end();
  });
}

const clientProp = {
  type: "string",
  description:
    "Target client name or ID. If omitted, uses REMOTECMD_DEFAULT_CLIENT env or auto-selects when exactly one client is connected.",
};

function resolveClient(args) {
  return args.client || DEFAULT_CLIENT || undefined;
}

const server = new Server(
  { name: "remote-cmd", version: "1.1.0" },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    {
      name: "remote_exec",
      description:
        "Execute a PowerShell command on a remote client machine. Returns stdout, stderr and exit code. Use 'client' to target a specific machine when multiple are connected.",
      inputSchema: {
        type: "object",
        properties: {
          command: {
            type: "string",
            description: "PowerShell command to execute on remote machine",
          },
          timeoutSeconds: {
            type: "number",
            description: `How long the command may run before it is killed (default ${EXEC_DEFAULT_SECONDS}, max ${EXEC_MAX_SECONDS}). Raise it for builds and other long jobs.`,
            default: EXEC_DEFAULT_SECONDS,
          },
          client: clientProp,
        },
        required: ["command"],
      },
    },
    {
      name: "remote_status",
      description:
        "Check connection status. Without 'client' returns aggregate (total/connected clients). With 'client' returns details for that specific client.",
      inputSchema: {
        type: "object",
        properties: {
          client: clientProp,
        },
      },
    },
    {
      name: "remote_list_clients",
      description:
        "List all clients known to the relay server with their connection status, name, id, and last-poll time.",
      inputSchema: {
        type: "object",
        properties: {},
      },
    },
    {
      name: "remote_upload",
      description:
        "Upload a file from local machine to a remote client (max 200MB). Use 'client' when multiple clients are connected.",
      inputSchema: {
        type: "object",
        properties: {
          localPath: {
            type: "string",
            description: "Absolute path to local file to upload",
          },
          remotePath: {
            type: "string",
            description:
              "Absolute path where file should be saved on remote machine",
          },
          client: clientProp,
        },
        required: ["localPath", "remotePath"],
      },
    },
    {
      name: "remote_download",
      description:
        "Download a file from a remote client to the local machine (max 200MB). Use 'client' when multiple clients are connected.",
      inputSchema: {
        type: "object",
        properties: {
          remotePath: {
            type: "string",
            description: "Absolute path to file on remote machine",
          },
          localPath: {
            type: "string",
            description: "Absolute path where file should be saved locally",
          },
          client: clientProp,
        },
        required: ["remotePath", "localPath"],
      },
    },
  ],
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args = {} } = request.params;

  try {
    switch (name) {
      case "remote_exec": {
        const timeoutSeconds = args.timeoutSeconds || EXEC_DEFAULT_SECONDS;
        const result = await apiCall(
          "POST",
          "/api/exec",
          { command: args.command, timeoutSeconds },
          { client: resolveClient(args) },
          false,
          // Outlast the relay, which itself outlasts the command — otherwise this socket would give
          // up first and a long command would look like a failure while it is still running.
          (Math.min(timeoutSeconds, EXEC_MAX_SECONDS) + 60) * 1000
        );
        return {
          content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
        };
      }

      case "remote_status": {
        const result = await apiCall("GET", "/api/status", null, {
          client: resolveClient(args),
        });
        return {
          content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
        };
      }

      case "remote_list_clients": {
        const result = await apiCall("GET", "/api/clients");
        return {
          content: [{ type: "text", text: JSON.stringify(result, null, 2) }],
        };
      }

      case "remote_upload": {
        if (!fs.existsSync(args.localPath)) {
          return {
            content: [
              {
                type: "text",
                text: `Error: Local file not found: ${args.localPath}`,
              },
            ],
            isError: true,
          };
        }
        const stat = fs.statSync(args.localPath);
        const result = await uploadFile(
          args.localPath,
          args.remotePath,
          resolveClient(args)
        );
        return {
          content: [
            {
              type: "text",
              text: `Uploaded ${(stat.size / 1024 / 1024).toFixed(1)}MB: ${args.localPath} -> ${args.remotePath}\n${JSON.stringify(result, null, 2)}`,
            },
          ],
        };
      }

      case "remote_download": {
        const result = await downloadFile(
          args.remotePath,
          args.localPath,
          resolveClient(args)
        );
        if (result.error) {
          return {
            content: [{ type: "text", text: `Error: ${result.error}` }],
            isError: true,
          };
        }
        return {
          content: [
            {
              type: "text",
              text: `Downloaded ${(result.size / 1024 / 1024).toFixed(1)}MB: ${args.remotePath} -> ${args.localPath}`,
            },
          ],
        };
      }

      default:
        return {
          content: [{ type: "text", text: `Unknown tool: ${name}` }],
          isError: true,
        };
    }
  } catch (error) {
    return {
      content: [{ type: "text", text: `Error: ${error.message}` }],
      isError: true,
    };
  }
});

const transport = new StdioServerTransport();
await server.connect(transport);

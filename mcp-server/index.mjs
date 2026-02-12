import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import http from "http";
import fs from "fs";
import path from "path";

const SERVER_URL = process.env.REMOTECMD_URL || "http://localhost:7890";
const TOKEN = process.env.REMOTECMD_TOKEN || "heslo123";

function apiCall(method, endpoint, body = null, isBinary = false) {
  return new Promise((resolve, reject) => {
    const url = new URL(endpoint, SERVER_URL);
    url.searchParams.set("token", TOKEN);

    const options = {
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      method,
      timeout: 300000,
    };

    if (body && !isBinary) {
      options.headers = { "Content-Type": "application/json" };
    }

    const req = http.request(options, (res) => {
      const chunks = [];
      res.on("data", (chunk) => chunks.push(chunk));
      res.on("end", () => {
        const buf = Buffer.concat(chunks);
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

function uploadFile(localPath, remotePath) {
  return new Promise((resolve, reject) => {
    const fileData = fs.readFileSync(localPath);
    const url = new URL("/api/upload", SERVER_URL);
    url.searchParams.set("token", TOKEN);
    url.searchParams.set("path", remotePath);

    const options = {
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      method: "POST",
      timeout: 300000,
      headers: {
        "Content-Type": "application/octet-stream",
        "Content-Length": fileData.length,
      },
    };

    const req = http.request(options, (res) => {
      const chunks = [];
      res.on("data", (chunk) => chunks.push(chunk));
      res.on("end", () => {
        try {
          resolve(JSON.parse(Buffer.concat(chunks).toString()));
        } catch {
          resolve(Buffer.concat(chunks).toString());
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

function downloadFile(remotePath, localPath) {
  return new Promise((resolve, reject) => {
    const url = new URL("/api/download", SERVER_URL);
    url.searchParams.set("token", TOKEN);
    url.searchParams.set("path", remotePath);

    const options = {
      hostname: url.hostname,
      port: url.port,
      path: url.pathname + url.search,
      method: "GET",
      timeout: 300000,
    };

    const req = http.request(options, (res) => {
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
          try {
            resolve(JSON.parse(buf.toString()));
          } catch {
            resolve({ error: buf.toString() });
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

const server = new Server(
  { name: "remote-cmd", version: "1.0.0" },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    {
      name: "remote_exec",
      description:
        "Execute a PowerShell command on the remote machine (COMOS_1). Returns stdout, stderr and exit code.",
      inputSchema: {
        type: "object",
        properties: {
          command: {
            type: "string",
            description: "PowerShell command to execute on remote machine",
          },
          timeoutSeconds: {
            type: "number",
            description: "Timeout in seconds (default 30, max 300)",
            default: 30,
          },
        },
        required: ["command"],
      },
    },
    {
      name: "remote_status",
      description:
        "Check if the remote client (COMOS_1) is connected to the relay server.",
      inputSchema: {
        type: "object",
        properties: {},
      },
    },
    {
      name: "remote_upload",
      description:
        "Upload a file from local machine to the remote machine (COMOS_1). Max 200MB.",
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
        },
        required: ["localPath", "remotePath"],
      },
    },
    {
      name: "remote_download",
      description:
        "Download a file from the remote machine (COMOS_1) to local machine. Max 200MB.",
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
        },
        required: ["remotePath", "localPath"],
      },
    },
  ],
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args } = request.params;

  try {
    switch (name) {
      case "remote_exec": {
        const result = await apiCall("POST", "/api/exec", {
          command: args.command,
          timeoutSeconds: args.timeoutSeconds || 30,
        });
        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(result, null, 2),
            },
          ],
        };
      }

      case "remote_status": {
        const result = await apiCall("GET", "/api/status");
        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(result, null, 2),
            },
          ],
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
        const result = await uploadFile(args.localPath, args.remotePath);
        return {
          content: [
            {
              type: "text",
              text: `Uploaded ${(stat.size / 1024 / 1024).toFixed(1)}MB: ${args.localPath} → ${args.remotePath}\n${JSON.stringify(result, null, 2)}`,
            },
          ],
        };
      }

      case "remote_download": {
        const result = await downloadFile(args.remotePath, args.localPath);
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
              text: `Downloaded ${(result.size / 1024 / 1024).toFixed(1)}MB: ${args.remotePath} → ${args.localPath}`,
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

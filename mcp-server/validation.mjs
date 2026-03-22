import https from "https";
import fs from "fs";

/**
 * Validate remote file path - reject relative, UNC, and path-traversal attempts.
 * @param {string} p - Path to validate
 * @returns {string} Trimmed path
 * @throws {Error} If path is invalid
 */
export function validateRemotePath(p) {
  if (!p || typeof p !== "string") throw new Error("Path must be a non-empty string");
  const trimmed = p.trim();
  if (trimmed.startsWith("..") || trimmed.includes("/../") || trimmed.includes("\\..\\"))
    throw new Error("Relative paths are not allowed");
  if (trimmed.startsWith("\\\\") || trimmed.startsWith("//"))
    throw new Error("UNC paths are not allowed");
  return trimmed;
}

/**
 * Validate and sanitize command input for remote execution.
 * @param {string} command - Raw command string
 * @returns {{ command: string }} Validated command
 * @throws {Error} If command is empty or exceeds max length
 */
export function validateCommand(command) {
  const trimmed = (command ?? "").trim();
  if (!trimmed) throw new Error("command must not be empty");
  if (trimmed.length > 8192) throw new Error("command exceeds maximum length of 8192 characters");
  return { command: trimmed };
}

/**
 * Clamp timeout to allowed range [1, 300].
 * @param {number|undefined} timeout - Raw timeout value
 * @returns {number} Clamped timeout
 */
export function clampTimeout(timeout) {
  return Math.min(300, Math.max(1, timeout || 30));
}

/**
 * Build HTTPS agent for self-signed or CA-signed relay server.
 * @param {boolean} isHttps - Whether the server uses HTTPS
 * @returns {import("https").Agent|undefined}
 */
export function buildAgent(isHttps) {
  if (!isHttps) return undefined;

  const caCertPath = process.env.REMOTECMD_CA_CERT;
  if (caCertPath) {
    const ca = fs.readFileSync(caCertPath);
    return new https.Agent({ ca });
  }

  return new https.Agent({ rejectUnauthorized: false });
}

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { validateRemotePath, validateCommand, clampTimeout, buildAgent } from "../validation.mjs";

// ---------------------------------------------------------------------------
// validateRemotePath
// ---------------------------------------------------------------------------
describe("validateRemotePath", () => {
  it("accepts absolute Windows path", () => {
    const result = validateRemotePath("C:\\Users\\test");
    assert.equal(result, "C:\\Users\\test");
  });

  it("accepts absolute Unix path", () => {
    const result = validateRemotePath("/home/test");
    assert.equal(result, "/home/test");
  });

  it("trims whitespace from valid path", () => {
    const result = validateRemotePath("  C:\\Users\\test  ");
    assert.equal(result, "C:\\Users\\test");
  });

  it("rejects relative path starting with ..", () => {
    assert.throws(
      () => validateRemotePath("../etc/passwd"),
      { message: "Relative paths are not allowed" }
    );
  });

  it("rejects path with /../ traversal (Unix)", () => {
    assert.throws(
      () => validateRemotePath("C:\\Users\\test/../../../Windows"),
      { message: "Relative paths are not allowed" }
    );
  });

  it("rejects path with \\..\\  traversal (Windows)", () => {
    assert.throws(
      () => validateRemotePath("C:\\Users\\..\\Windows"),
      { message: "Relative paths are not allowed" }
    );
  });

  it("rejects UNC path with backslashes", () => {
    assert.throws(
      () => validateRemotePath("\\\\server\\share"),
      { message: "UNC paths are not allowed" }
    );
  });

  it("rejects UNC path with forward slashes", () => {
    assert.throws(
      () => validateRemotePath("//server/share"),
      { message: "UNC paths are not allowed" }
    );
  });

  it("rejects empty string", () => {
    assert.throws(
      () => validateRemotePath(""),
      { message: "Path must be a non-empty string" }
    );
  });

  it("rejects null", () => {
    assert.throws(
      () => validateRemotePath(null),
      { message: "Path must be a non-empty string" }
    );
  });

  it("rejects undefined", () => {
    assert.throws(
      () => validateRemotePath(undefined),
      { message: "Path must be a non-empty string" }
    );
  });

  it("rejects non-string type (number)", () => {
    assert.throws(
      () => validateRemotePath(42),
      { message: "Path must be a non-empty string" }
    );
  });
});

// ---------------------------------------------------------------------------
// validateCommand
// ---------------------------------------------------------------------------
describe("validateCommand", () => {
  it("accepts normal command", () => {
    const result = validateCommand("Get-Process");
    assert.deepEqual(result, { command: "Get-Process" });
  });

  it("trims whitespace from command", () => {
    const result = validateCommand("  hostname  ");
    assert.deepEqual(result, { command: "hostname" });
  });

  it("rejects empty string", () => {
    assert.throws(
      () => validateCommand(""),
      { message: "command must not be empty" }
    );
  });

  it("rejects whitespace-only string", () => {
    assert.throws(
      () => validateCommand("   "),
      { message: "command must not be empty" }
    );
  });

  it("rejects null", () => {
    assert.throws(
      () => validateCommand(null),
      { message: "command must not be empty" }
    );
  });

  it("rejects undefined", () => {
    assert.throws(
      () => validateCommand(undefined),
      { message: "command must not be empty" }
    );
  });

  it("rejects command longer than 8192 characters", () => {
    const longCmd = "A".repeat(8193);
    assert.throws(
      () => validateCommand(longCmd),
      { message: "command exceeds maximum length of 8192 characters" }
    );
  });

  it("accepts command exactly 8192 characters", () => {
    const cmd = "A".repeat(8192);
    const result = validateCommand(cmd);
    assert.equal(result.command, cmd);
  });
});

// ---------------------------------------------------------------------------
// clampTimeout
// ---------------------------------------------------------------------------
describe("clampTimeout", () => {
  it("returns default 30 when undefined", () => {
    assert.equal(clampTimeout(undefined), 30);
  });

  it("returns default 30 when 0", () => {
    assert.equal(clampTimeout(0), 30);
  });

  it("clamps value below 1 to 1", () => {
    assert.equal(clampTimeout(-5), 1);
  });

  it("clamps value above 300 to 300", () => {
    assert.equal(clampTimeout(999), 300);
  });

  it("passes through value within range", () => {
    assert.equal(clampTimeout(60), 60);
  });

  it("passes through boundary value 1", () => {
    assert.equal(clampTimeout(1), 1);
  });

  it("passes through boundary value 300", () => {
    assert.equal(clampTimeout(300), 300);
  });
});

// ---------------------------------------------------------------------------
// buildAgent
// ---------------------------------------------------------------------------
describe("buildAgent", () => {
  const originalEnv = process.env.REMOTECMD_CA_CERT;

  it("returns undefined for non-HTTPS", () => {
    const agent = buildAgent(false);
    assert.equal(agent, undefined);
  });

  it("returns agent with rejectUnauthorized: false without CA cert", () => {
    delete process.env.REMOTECMD_CA_CERT;
    const agent = buildAgent(true);
    assert.ok(agent, "agent should be defined");
    assert.equal(agent.options.rejectUnauthorized, false);
  });

  it("returns agent with ca option when REMOTECMD_CA_CERT is set", () => {
    // Use package.json as a stand-in file that definitely exists
    process.env.REMOTECMD_CA_CERT = new URL("../package.json", import.meta.url).pathname.replace(/^\/([A-Z]:)/, "$1");
    const agent = buildAgent(true);
    assert.ok(agent, "agent should be defined");
    assert.ok(agent.options.ca, "agent should have ca option");
    assert.equal(agent.options.rejectUnauthorized, undefined);
    // Restore
    if (originalEnv === undefined) {
      delete process.env.REMOTECMD_CA_CERT;
    } else {
      process.env.REMOTECMD_CA_CERT = originalEnv;
    }
  });
});

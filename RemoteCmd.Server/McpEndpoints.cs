using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteCmd.Shared;

/// <summary>What MCP tools can do on the relay; supplied by Program, which owns the sessions.</summary>
public sealed record McpOps(
    Func<string?, TargetResolution> Resolve,
    Func<ClientSession, string, int, Task<CommandResult>> Exec,
    Func<ClientSession, string, byte[], Task<string?>> Push,
    Func<ClientSession, string, Task<FileTransfer>> Pull,
    Func<object> ListClients);

/// <summary>
/// MCP over Streamable HTTP at <c>/mcp</c>, signed in through OAuth with approval on the dashboard.
/// It replaces the Node bridge: an agent adds the relay's URL and nothing else has to run.
///
/// Plain JSON responses only (no SSE stream): every tool answers within the request, and the spec
/// lets a server choose JSON for that.
/// </summary>
public static class McpEndpoints
{
    public const string ProtocolVersion = "2025-06-18";
    private static readonly string[] KnownVersions = [ProtocolVersion, "2025-03-26", "2024-11-05"];

    /// <summary>MCP replies carry file contents inline; beyond this, use the REST API.</summary>
    public const int MaxInlineBytes = 20 * 1024 * 1024;

    public static void Map(WebApplication app, RelayContext r, OAuthServer oauth, McpOps ops)
    {
        // --- Discovery (RFC 9728, RFC 8414) ---

        app.MapGet("/.well-known/oauth-protected-resource/{**rest}", (HttpRequest req) => ResourceMetadata(req));
        app.MapGet("/.well-known/oauth-protected-resource", (HttpRequest req) => ResourceMetadata(req));
        app.MapGet("/.well-known/oauth-authorization-server", (HttpRequest req) =>
        {
            var issuer = Origin(req);
            return Results.Ok(new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = $"{issuer}/oauth/authorize",
                ["token_endpoint"] = $"{issuer}/oauth/token",
                ["registration_endpoint"] = $"{issuer}/oauth/register",
                ["response_types_supported"] = new[] { "code" },
                ["grant_types_supported"] = new[] { "authorization_code" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
                ["token_endpoint_auth_methods_supported"] = new[] { "none" },
                ["scopes_supported"] = new[] { "remotecmd" },
            });
        });

        // --- Dynamic client registration (RFC 7591) ---

        app.MapPost("/oauth/register", async (HttpRequest req) =>
        {
            JsonElement body;
            try { body = await req.ReadFromJsonAsync<JsonElement>(); }
            catch (JsonException) { return OAuthError("invalid_client_metadata", "Body must be JSON"); }

            var uris = body.TryGetProperty("redirect_uris", out var u) && u.ValueKind == JsonValueKind.Array
                ? u.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : [];
            if (uris.Count == 0 || uris.Count > 10 || !uris.All(OAuthServer.IsAcceptableRedirect))
                return OAuthError("invalid_redirect_uri", "Redirect URIs must be https or http on loopback");

            var name = body.TryGetProperty("client_name", out var n) ? n.GetString() : null;
            var client = oauth.Register(name, uris);
            r.Events.Add("oauth", client.Name, "MCP client registered");
            return Results.Json(new Dictionary<string, object>
            {
                ["client_id"] = client.ClientId,
                ["client_name"] = client.Name,
                ["redirect_uris"] = client.RedirectUris,
                ["token_endpoint_auth_method"] = "none",
                ["grant_types"] = new[] { "authorization_code" },
                ["response_types"] = new[] { "code" },
            }, statusCode: 201);
        });

        // --- Authorization: a page that waits for the dashboard ---

        app.MapGet("/oauth/authorize", (HttpRequest req) =>
        {
            var q = req.Query;
            var client = oauth.FindClient(q["client_id"]);
            var redirect = q["redirect_uri"].ToString();
            // Errors before the redirect URI is trusted are shown here, never sent to it.
            if (client is null) return Page("Unknown client", "This MCP client is not registered with the relay. Remove and add the server again.");
            if (!client.RedirectUris.Contains(redirect)) return Page("Unknown redirect", "The redirect address is not one this client registered.");

            if (q["response_type"] != "code")
                return Results.Redirect(WithQuery(redirect, ("error", "unsupported_response_type"), ("state", q["state"])));
            if (q["code_challenge_method"] != "S256" || string.IsNullOrEmpty(q["code_challenge"]))
                return Results.Redirect(WithQuery(redirect, ("error", "invalid_request"), ("error_description", "PKCE with S256 is required"), ("state", q["state"])));

            var request = oauth.Request(client, redirect, q["code_challenge"]!, q["state"], DateTime.UtcNow);
            r.Events.Add("oauth", client.Name, $"waiting for approval, code {request.UserCode}");
            return Results.Content(ConsentPage(request, r.AdminPort), "text/html; charset=utf-8");
        });

        app.MapGet("/oauth/authorize/status", (string id) =>
        {
            var request = oauth.Find(id, DateTime.UtcNow);
            if (request is null) return Results.Ok(new { status = "expired" });
            return request.Status switch
            {
                "approved" => Results.Ok(new { status = "approved", redirect = WithQuery(request.RedirectUri, ("code", request.Code), ("state", request.State)) }),
                "denied" => Results.Ok(new { status = "denied", redirect = WithQuery(request.RedirectUri, ("error", "access_denied"), ("state", request.State)) }),
                _ => Results.Ok(new { status = "pending" }),
            };
        });

        // --- Token endpoint ---

        app.MapPost("/oauth/token", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType) return OAuthError("invalid_request", "Use application/x-www-form-urlencoded");
            var form = await req.ReadFormAsync();
            if (form["grant_type"] != "authorization_code") return OAuthError("unsupported_grant_type", "Only authorization_code");

            var now = DateTime.UtcNow;
            var peer = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (r.Throttle.IsLockedOut(peer, now)) return Results.StatusCode(429);

            var (token, error) = oauth.Exchange(form["code"], form["client_id"], form["redirect_uri"], form["code_verifier"], now);
            if (token is null)
            {
                r.Throttle.RecordFailure(peer, now);
                return OAuthError(error!, "The code is wrong, used or expired");
            }
            r.Events.Add("oauth", oauth.FindClient(form["client_id"])?.Name ?? "-", "MCP client signed in");
            return Results.Ok(new Dictionary<string, object>
            {
                ["access_token"] = token,
                ["token_type"] = "Bearer",
                ["scope"] = "remotecmd",
            });
        });

        // --- Dashboard: approve or deny ---

        app.MapGet("/api/admin/oauth/pending", () => Results.Ok(new
        {
            pending = oauth.Pending(DateTime.UtcNow).Select(p => new
            {
                id = p.Id, client = p.ClientName, userCode = p.UserCode, redirect = p.RedirectHost, createdUtc = p.CreatedUtc,
            }),
        }));

        app.MapPost("/api/admin/oauth/{id}/{decision}", (string id, string decision) =>
        {
            if (decision is not ("approve" or "deny")) return Results.NotFound();
            var request = oauth.Find(id, DateTime.UtcNow);
            if (request is null || !oauth.Decide(id, decision == "approve", DateTime.UtcNow)) return Results.NotFound();
            r.Events.Add("oauth", request.ClientName, decision == "approve" ? "approved on the dashboard" : "denied on the dashboard");
            return Results.Ok();
        });

        // --- MCP itself ---

        app.MapGet("/mcp", () => Results.StatusCode(405));
        app.MapDelete("/mcp", () => Results.StatusCode(405));
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            var req = ctx.Request;
            // DNS rebinding: a browser page must not drive the endpoint, signed in or not.
            if (req.Headers.Origin.FirstOrDefault() is { Length: > 0 } origin
                && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Authority.Equals(req.Host.Value, StringComparison.OrdinalIgnoreCase)))
                return Results.StatusCode(403);

            var credential = r.Store.Authenticate(ExtractBearer(req.Headers.Authorization.FirstOrDefault()));
            if (credential is null || credential.Role == CredentialRole.Client)
            {
                ctx.Response.Headers.WWWAuthenticate =
                    $"Bearer resource_metadata=\"{Origin(req)}/.well-known/oauth-protected-resource\"";
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            }

            JsonNode? message;
            try { message = await JsonNode.ParseAsync(req.Body); }
            catch (JsonException) { return Results.Json(RpcError(null, -32700, "Parse error")); }
            if (message is not JsonObject call) return Results.Json(RpcError(null, -32600, "Expected one JSON-RPC message"));

            var id = call["id"]?.DeepClone();
            var method = call["method"]?.GetValue<string>();
            // Notifications and responses carry no id and get no answer.
            if (id is null) return Results.Accepted();

            try
            {
                JsonNode result = method switch
                {
                    "initialize" => Initialize(call["params"]),
                    "ping" => new JsonObject(),
                    "tools/list" => new JsonObject { ["tools"] = Tools() },
                    "tools/call" => await CallTool(call["params"], ops, credential),
                    _ => throw new RpcException(-32601, $"Method not found: {method}"),
                };
                return Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            }
            catch (RpcException ex)
            {
                return Results.Json(RpcError(id, ex.Code, ex.Message));
            }
        });
    }

    private sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private static JsonObject RpcError(JsonNode? id, int code, string message)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static JsonNode Initialize(JsonNode? p)
    {
        var requested = p?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = KnownVersions.Contains(requested) ? requested : ProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "remote-cmd", ["version"] = VersionInfo.Version },
            ["instructions"] =
                "Runs commands and moves files on machines paired with this RemoteCmd relay. " +
                "Call remote_list_clients first to see which machines are online; pass 'client' whenever more than one is.",
        };
    }

    private static JsonArray Tools()
    {
        static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };
        var client = Prop("string", "Target machine name or id. Optional when exactly one machine is connected.");
        return
        [
            new JsonObject
            {
                ["name"] = "remote_list_clients",
                ["description"] = "List machines known to the relay with their connection state, address and activity.",
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            },
            new JsonObject
            {
                ["name"] = "remote_exec",
                ["description"] = "Run a command on a machine: PowerShell on Windows, bash elsewhere. Returns output and exit code.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["command"] = Prop("string", "The command to run"),
                        ["timeoutSeconds"] = Prop("number", $"How long it may run (default {ExecLimits.DefaultSeconds}, max {ExecLimits.MaxSeconds})"),
                        ["client"] = client.DeepClone(),
                    },
                    ["required"] = new JsonArray("command"),
                },
            },
            new JsonObject
            {
                ["name"] = "remote_write_file",
                ["description"] = $"Write a file on a machine. Give 'text' for text or 'contentBase64' for binary (max {MaxInlineBytes / 1024 / 1024} MB).",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["remotePath"] = Prop("string", "Absolute path on the machine"),
                        ["text"] = Prop("string", "File content as UTF-8 text"),
                        ["contentBase64"] = Prop("string", "File content, base64-encoded"),
                        ["client"] = client.DeepClone(),
                    },
                    ["required"] = new JsonArray("remotePath"),
                },
            },
            new JsonObject
            {
                ["name"] = "remote_read_file",
                ["description"] = $"Read a file from a machine. Returns text when it is UTF-8, otherwise base64 (max {MaxInlineBytes / 1024 / 1024} MB).",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["remotePath"] = Prop("string", "Absolute path on the machine"),
                        ["client"] = client.DeepClone(),
                    },
                    ["required"] = new JsonArray("remotePath"),
                },
            },
            new JsonObject
            {
                ["name"] = "remote_screenshot",
                ["description"] =
                    "Photograph a machine's screen and return the image. Use it to see what is actually on the display - "
                    + "a GUI application's state, an installer or error dialog, anything a command cannot describe. "
                    + "The client must run in the interactive desktop session: a Windows service runs in session 0, which has "
                    + "no desktop, and the capture comes back blank (the reply says so). Linux clients need grim, scrot or ImageMagick.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["remotePath"] = Prop("string", "Optional path on the machine to keep the PNG at; without it a temporary file is used and old captures are pruned"),
                        ["client"] = client.DeepClone(),
                    },
                    ["required"] = new JsonArray(),
                },
            },
        ];
    }

    private static async Task<JsonNode> CallTool(JsonNode? p, McpOps ops, Credential caller)
    {
        var name = p?["name"]?.GetValue<string>();
        var args = p?["arguments"] as JsonObject ?? [];
        string? Str(string key) => args[key]?.GetValue<string>();

        if (name == "remote_list_clients")
            return Text(JsonSerializer.Serialize(ops.ListClients(), new JsonSerializerOptions { WriteIndented = true }));

        if (name is not ("remote_exec" or "remote_write_file" or "remote_read_file" or "remote_screenshot"))
            throw new RpcException(-32602, $"Unknown tool: {name}");

        var target = ops.Resolve(Str("client"));
        if (target.Error != null) return Text(target.Error, isError: true);
        var session = target.Session!;

        switch (name)
        {
            case "remote_exec":
            {
                var command = Str("command");
                if (string.IsNullOrEmpty(command)) return Text("Missing 'command'", isError: true);
                var timeout = args["timeoutSeconds"] is JsonValue v && v.TryGetValue<double>(out var t) ? (int)t : 0;
                var result = await ops.Exec(session, command, timeout);
                return Text($"exit code {result.ExitCode}\n{result.Output}", isError: result.ExitCode != 0);
            }
            case "remote_screenshot":
            {
                var keepAt = Str("remotePath") ?? "";
                var shot = await ops.Exec(session, $"{ScreenshotProtocol.CommandPrefix} {keepAt}".TrimEnd(), 60);
                if (shot.ExitCode != 0) return Text(shot.Output, isError: true);

                // The client answers with the path on the first line and a size/warning note after it.
                var lines = shot.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return Text("Client returned no screenshot path", isError: true);
                var shotPath = lines[0].Trim();
                var shotNote = lines.Length > 1 ? lines[1].Trim() : "";

                var image = await ops.Pull(session, shotPath);
                if (image.Data is null) return Text(image.Error ?? "Could not fetch the capture", isError: true);
                if (image.Data.Length > MaxInlineBytes)
                    return Text($"Capture is {image.Data.Length / 1024 / 1024} MB, too large for MCP; fetch {shotPath} with the REST download", isError: true);

                return Image(image.Data, "image/png", $"Screenshot of {session.Name} ({shotPath}) - {shotNote}");
            }
            case "remote_write_file":
            {
                var path = Str("remotePath");
                if (string.IsNullOrEmpty(path)) return Text("Missing 'remotePath'", isError: true);
                byte[] data;
                if (Str("contentBase64") is { } b64)
                {
                    try { data = Convert.FromBase64String(b64); }
                    catch (FormatException) { return Text("contentBase64 is not valid base64", isError: true); }
                }
                else if (Str("text") is { } text) data = Encoding.UTF8.GetBytes(text);
                else return Text("Give 'text' or 'contentBase64'", isError: true);
                if (data.Length > MaxInlineBytes) return Text("Too large for MCP; use the REST upload", isError: true);

                var error = await ops.Push(session, path, data);
                return error is null ? Text($"Wrote {data.Length} bytes to {path} on {session.Name}") : Text(error, isError: true);
            }
            default:
            {
                var path = Str("remotePath");
                if (string.IsNullOrEmpty(path)) return Text("Missing 'remotePath'", isError: true);
                var file = await ops.Pull(session, path);
                if (file.Data is null) return Text(file.Error ?? "Download failed", isError: true);
                if (file.Data.Length > MaxInlineBytes) return Text("Too large for MCP; use the REST download", isError: true);
                try
                {
                    return Text(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(file.Data));
                }
                catch (DecoderFallbackException)
                {
                    return Text($"base64 ({file.Data.Length} bytes):\n{Convert.ToBase64String(file.Data)}");
                }
            }
        }
    }

    private static JsonObject Text(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    /// <summary>
    /// An image result, with a line of text beside it. The note matters as much as the picture:
    /// a capture taken from a Windows service is blank, and without the explanation the agent sees
    /// only a black rectangle and has no way to know why.
    /// </summary>
    private static JsonObject Image(byte[] data, string mimeType, string note) => new()
    {
        ["content"] = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = note },
            new JsonObject
            {
                ["type"] = "image",
                ["data"] = Convert.ToBase64String(data),
                ["mimeType"] = mimeType,
            }),
        ["isError"] = false,
    };

    private static IResult ResourceMetadata(HttpRequest req) => Results.Ok(new Dictionary<string, object>
    {
        ["resource"] = $"{Origin(req)}/mcp",
        ["authorization_servers"] = new[] { Origin(req) },
        ["scopes_supported"] = new[] { "remotecmd" },
        ["bearer_methods_supported"] = new[] { "header" },
    });

    private static string Origin(HttpRequest req) => $"{req.Scheme}://{req.Host}";

    private static IResult OAuthError(string error, string description)
        => Results.Json(new { error, error_description = description }, statusCode: 400);

    private static string? ExtractBearer(string? header)
        => header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;

    private static string WithQuery(string uri, params (string Key, string? Value)[] pairs)
    {
        var sb = new StringBuilder(uri);
        var sep = uri.Contains('?') ? '&' : '?';
        foreach (var (key, value) in pairs)
        {
            if (string.IsNullOrEmpty(value)) continue;
            sb.Append(sep).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            sep = '&';
        }
        return sb.ToString();
    }

    private static IResult Page(string title, string message)
        => Results.Content(PageShell(title, $"<p>{System.Net.WebUtility.HtmlEncode(message)}</p>"), "text/html; charset=utf-8", statusCode: 400);

    private static string ConsentPage(PendingAuthorization request, int adminPort)
    {
        static string e(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");
        var body = $$"""
            <p><b>{{e(request.ClientName)}}</b> wants to run commands on the machines paired with this relay.</p>
            <p>Approve it in the RemoteCmd dashboard on the relay's machine —
               <a href="http://localhost:{{adminPort}}/manage">http://localhost:{{adminPort}}/manage</a> —
               and check that it shows the same code:</p>
            <p class="code">{{e(request.UserCode)}}</p>
            <p class="muted" id="state">Waiting for approval…</p>
            <script>
              const id = {{JsonSerializer.Serialize(request.Id)}};
              async function poll() {
                try {
                  const r = await (await fetch('/oauth/authorize/status?id=' + encodeURIComponent(id))).json();
                  if (r.redirect) { document.getElementById('state').textContent = r.status === 'approved' ? 'Approved — returning to the app…' : 'Denied.'; location.href = r.redirect; return; }
                  if (r.status === 'expired') { document.getElementById('state').textContent = 'This request expired. Start again from the app.'; return; }
                } catch {}
                setTimeout(poll, 1500);
              }
              poll();
            </script>
            """;
        return PageShell("Approve access", body);
    }

    private static string PageShell(string title, string body) => $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>RemoteCmd — {{System.Net.WebUtility.HtmlEncode(title)}}</title>
        <style>
          :root { color-scheme: light dark; --bg:#eef1f4; --fg:#1b2430; --dim:#5b6878; --sig:#0e7c86; --panel:#fff; --grid:#cfd6de; }
          @media (prefers-color-scheme: dark) { :root { --bg:#13181f; --fg:#e4e9ef; --dim:#8b97a6; --sig:#3cc4c9; --panel:#1a212a; --grid:#2c3642; } }
          body { margin:0; min-height:100vh; display:grid; place-items:center; background:var(--bg); color:var(--fg);
                 font:15px/1.55 "Segoe UI", -apple-system, system-ui, sans-serif; }
          main { max-width:520px; margin:24px; padding:24px 28px; background:var(--panel); border:1px solid var(--grid); border-radius:8px; }
          h1 { font-size:17px; font-weight:650; margin:0 0 14px; }
          .code { font:600 34px/1.2 ui-monospace, Consolas, monospace; letter-spacing:.12em; color:var(--sig); margin:18px 0; }
          .muted { color:var(--dim); } a { color:var(--sig); }
        </style></head>
        <body><main><h1>{{System.Net.WebUtility.HtmlEncode(title)}}</h1>{{body}}</main></body></html>
        """;
}

using System.Collections.Concurrent;
using System.Text.Json;
using RemoteCmd.Shared;

/// <summary>Everything the management endpoints need from the running relay.</summary>
public sealed record RelayContext(
    ServerStore Store,
    string StorePath,
    PairingCodes Pairings,
    AdminGate Gate,
    AuthThrottle Throttle,
    RelayStats Stats,
    ConcurrentDictionary<string, ClientSession> Clients,
    EventLog Events,
    UnknownKeys UnknownKeys,
    int Port,
    int AdminPort,
    bool NoTls,
    string? CertSha256)
{
    /// <summary>Where the install scripts are fetched from.</summary>
    public const string InstallBase = "https://raw.githubusercontent.com/nks-hub/remote-cmd/main";

    /// <summary>
    /// The address clients should use: the one set in the dashboard, or this machine's most likely
    /// LAN address (see <see cref="RelayAddresses"/>).
    /// </summary>
    public string PublicUrl
        => (Store.PublicUrl is { } saved ? RelayUrl(saved) : null)
           ?? UrlFor(RelayAddresses.Candidates().FirstOrDefault()?.Address ?? Environment.MachineName);

    /// <summary>
    /// Reduces an address to scheme, host and port — all a client polls. The result is pasted into
    /// install commands that run as administrator, so nothing else from the input (path, query,
    /// quotes) may reach them. Null when it is not an http(s) address.
    /// </summary>
    public static string? RelayUrl(string url)
        => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && uri.UserInfo.Length == 0
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;

    public string UrlFor(string address) => $"{(NoTls ? "http" : "https")}://{address}:{Port}";

    /// <summary>Ready-to-paste commands that install a client and pair it with this code.</summary>
    public IReadOnlyList<InstallCommand> InstallCommands(string code, string? serverUrl = null)
    {
        var url = serverUrl ?? PublicUrl;
        return
        [
            new("windows", "Windows (PowerShell as admin)",
                $"& ([scriptblock]::Create((irm {InstallBase}/install.ps1))) -Server '{url}' -Code '{code}'"),
            new("unix", "Linux / macOS",
                $"curl -fsSL {InstallBase}/install.sh | sudo sh -s -- client --server '{url}' --code '{code}'"),
            new("installed", "Client already installed",
                $"RemoteCmd.Client pair {url} {code} --install-service"),
        ];
    }
}

/// <summary>One way to install and pair a client, with a stable id for the dashboard's tabs.</summary>
public sealed record InstallCommand(string Id, string Label, string Command);

/// <summary>
/// The management API behind the dashboard: pairing, credentials, password and settings. The
/// middleware only lets these through on the loopback dashboard listener.
/// </summary>
public static class AdminEndpoints
{
    private sealed record LoginBody(string? Password);
    private sealed record PasswordBody(string? Password);
    private sealed record SettingsBody(string? PublicUrl);
    private sealed record CredentialBody(string? Name, string? Role);
    private sealed record ImportBody(string? Token, string? Name, string? Role);
    private sealed record PairBody(string? Name, int? TtlMinutes, string? ServerUrl);

    public static void Map(WebApplication app, RelayContext r)
    {
        app.MapGet("/api/admin/session", (HttpContext ctx) => Results.Ok(new
        {
            passwordRequired = r.Store.HasPassword,
            signedIn = !r.Store.HasPassword || r.Gate.IsValid(ctx.Request.Cookies[AdminGate.CookieName], DateTime.UtcNow),
        }));

        app.MapPost("/api/admin/login", async (HttpContext ctx, LoginBody body) =>
        {
            var now = DateTime.UtcNow;
            var peer = "dashboard-login";
            if (r.Throttle.IsLockedOut(peer, now)) return Results.StatusCode(429);
            if (!r.Store.CheckPassword(body.Password))
            {
                r.Throttle.RecordFailure(peer, now);
                r.Events.Add("auth", "dashboard", "wrong dashboard password");
                await Task.Delay(500);
                return Results.Unauthorized();
            }
            SetSessionCookie(ctx, r.Gate.StartSession(now));
            return Results.Ok();
        });

        app.MapPost("/api/admin/logout", (HttpContext ctx) =>
        {
            r.Gate.End(ctx.Request.Cookies[AdminGate.CookieName]);
            ctx.Response.Cookies.Delete(AdminGate.CookieName);
            return Results.Ok();
        });

        app.MapGet("/api/admin/info", () => Results.Ok(new
        {
            version = VersionInfo.Version,
            port = r.Port,
            adminPort = r.AdminPort,
            tls = !r.NoTls,
            certSha256 = r.CertSha256,
            publicUrl = r.PublicUrl,
            publicUrlSet = r.Store.PublicUrl != null,
            passwordSet = r.Store.HasPassword,
            settingsPath = r.StorePath,
        }));

        app.MapPost("/api/admin/settings", (SettingsBody body) =>
        {
            string? publicUrl = null;
            if (!string.IsNullOrWhiteSpace(body.PublicUrl) && (publicUrl = RelayContext.RelayUrl(body.PublicUrl)) is null)
                return Results.BadRequest(new { error = "Use a full address such as http://192.168.1.10:7890" });
            r.Store.PublicUrl = publicUrl;
            r.Events.Add("admin", "dashboard", r.Store.PublicUrl is null ? "relay address reset to the detected one" : $"relay address set to {r.Store.PublicUrl}");
            return Results.Ok(new { publicUrl = r.PublicUrl });
        });

        // Addresses of this machine, best first, to choose the one machines will reach.
        app.MapGet("/api/admin/addresses", () => Results.Ok(new
        {
            current = r.PublicUrl,
            addresses = RelayAddresses.Candidates().Select(a => new
            {
                url = r.UrlFor(a.Address),
                address = a.Address,
                @interface = a.Interface,
                kind = a.Kind,
            }),
        }));

        // Machines that keep polling with a key nobody issued: usually an old client left behind.
        app.MapGet("/api/admin/unknown-keys", () => Results.Ok(new
        {
            machines = r.UnknownKeys.Recent(DateTime.UtcNow).Select(m => new
            {
                peer = m.Peer,
                name = m.Name,
                host = m.Host,
                path = m.Path,
                attempts = m.Attempts,
                firstUtc = m.FirstUtc,
                lastUtc = m.LastUtc,
            }),
        }));

        // Drops a machine that is gone for good from the list instead of waiting for the hourly sweep.
        app.MapDelete("/api/admin/clients/{id}", (string id) =>
        {
            if (!r.Clients.TryGetValue(id, out var session)) return Results.NotFound(new { error = "No such machine" });
            if (session.IsConnected()) return Results.Conflict(new { error = "The machine is online; revoke its key to cut it off" });
            if (!r.Clients.TryRemove(id, out _)) return Results.NotFound(new { error = "No such machine" });
            foreach (var pending in session.InFlight.Values)
                pending.Tcs.TrySetResult(new CommandResult { Output = "[ERROR] Machine removed from the relay", ExitCode = -1 });
            session.Uploads.FailAll("Machine removed from the relay");
            session.Downloads.FailAll("Machine removed from the relay");
            r.Events.Add("admin", session.Name, "removed from the machine list");
            return Results.Ok();
        });

        app.MapPost("/api/admin/password", (HttpContext ctx, PasswordBody body) =>
        {
            if (body.Password is { Length: > 0 and < 8 })
                return Results.BadRequest(new { error = "Use at least 8 characters" });
            r.Store.SetPassword(body.Password);
            // Everyone else is signed out; whoever changed it stays in.
            r.Gate.EndAll();
            if (r.Store.HasPassword) SetSessionCookie(ctx, r.Gate.StartSession(DateTime.UtcNow));
            r.Events.Add("admin", "dashboard", r.Store.HasPassword ? "dashboard password set" : "dashboard password removed");
            return Results.Ok(new { passwordSet = r.Store.HasPassword });
        });

        app.MapGet("/api/admin/credentials", () => Results.Ok(new
        {
            credentials = r.Store.All().Select(c => new
            {
                id = c.Id,
                name = c.Label,
                display = c.Display,
                role = c.Role.ToString().ToLowerInvariant(),
                createdUtc = c.CreatedUtc,
                lastUsedUtc = c.LastUsedUtc,
                ephemeral = c.Ephemeral,
                online = r.Clients.Values.Count(s => s.CredentialId == c.Id && s.IsConnected()),
            }),
        }));

        app.MapPost("/api/admin/credentials", (CredentialBody body) =>
        {
            if (!TryRole(body.Role ?? "controller", out var role) || role == CredentialRole.Legacy)
                return Results.BadRequest(new { error = "Role must be controller or client" });
            var (credential, token) = r.Store.Issue(body.Name ?? "", role);
            r.Events.Add("admin", "dashboard", $"issued {role.ToString().ToLowerInvariant()} token '{credential.Label}'");
            return Results.Ok(new { id = credential.Id, name = credential.Label, role = credential.Role.ToString().ToLowerInvariant(), token });
        });

        app.MapPost("/api/admin/credentials/import", (ImportBody body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Token) || body.Token.Length < 12)
                return Results.BadRequest(new { error = "Token missing or shorter than 12 characters" });
            if (!TryRole(body.Role ?? "legacy", out var role))
                return Results.BadRequest(new { error = "Role must be legacy, controller or client" });
            var credential = r.Store.Import(body.Token, body.Name ?? "", role);
            r.Events.Add("admin", "dashboard", $"imported token '{credential.Label}' as {role.ToString().ToLowerInvariant()}");
            return Results.Ok(new { id = credential.Id, name = credential.Label });
        });

        app.MapDelete("/api/admin/credentials/{id}", (string id) =>
        {
            if (!r.Store.Revoke(id)) return Results.NotFound(new { error = "No such stored credential" });
            // Machines that used it are cut off now, not when their session would have aged out.
            foreach (var (sessionId, session) in r.Clients)
            {
                if (session.CredentialId != id || !r.Clients.TryRemove(sessionId, out _)) continue;
                foreach (var pending in session.InFlight.Values)
                    pending.Tcs.TrySetResult(new CommandResult { Output = "[ERROR] Credential revoked", ExitCode = -1 });
                session.Uploads.FailAll("Credential revoked");
                session.Downloads.FailAll("Credential revoked");
            }
            r.Events.Add("admin", "dashboard", $"revoked credential {id}");
            return Results.Ok();
        });

        app.MapPost("/api/admin/pair", (PairBody body) =>
        {
            string? serverUrl = null;
            if (!string.IsNullOrWhiteSpace(body.ServerUrl) && (serverUrl = RelayContext.RelayUrl(body.ServerUrl)) is null)
                return Results.BadRequest(new { error = "Use a full address such as http://192.168.1.10:7890" });
            var ttl = body.TtlMinutes is > 0 ? TimeSpan.FromMinutes(body.TtlMinutes.Value) : (TimeSpan?)null;
            var pending = r.Pairings.Create(body.Name, ttl, DateTime.UtcNow, serverUrl);
            r.Events.Add("pair", pending.Label ?? "-", $"pairing code issued, valid until {pending.ExpiresUtc.ToLocalTime():HH:mm}");
            return Results.Ok(Describe(r, pending));
        });

        app.MapGet("/api/admin/pair", () =>
        {
            var now = DateTime.UtcNow;
            return Results.Ok(new
            {
                pending = r.Pairings.Active(now).Select(p => Describe(r, p)),
                // What became of redeemed codes: a paired machine that never polls is the failure that
                // used to be invisible — the code was used, yet nothing ever showed up.
                recent = r.Pairings.Recent(now).Select(c =>
                {
                    var sessions = r.Clients.Values.Where(s => s.CredentialId == c.CredentialId).ToList();
                    return new
                    {
                        id = c.Id,
                        name = c.Name,
                        credentialId = c.CredentialId,
                        peer = c.Peer,
                        atUtc = c.AtUtc,
                        revoked = r.Store.All().All(x => x.Id != c.CredentialId),
                        polled = sessions.Count > 0,
                        connected = sessions.Any(s => s.IsConnected()),
                        sessionId = sessions.FirstOrDefault()?.Id,
                    };
                }),
            });
        });

        app.MapDelete("/api/admin/pair/{id}", (string id)
            => r.Pairings.Cancel(id) ? Results.Ok() : Results.NotFound());

        // --- Public: a machine redeems its code ---

        app.MapPost("/api/pair", async (HttpContext ctx) =>
        {
            var now = DateTime.UtcNow;
            var peer = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            if (r.Throttle.IsLockedOut(peer, now)) return Results.StatusCode(429);

            PairRequest? request;
            try { request = await ctx.Request.ReadFromJsonAsync<PairRequest>(); }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON body" }); }
            if (request is null) return Results.BadRequest(new { error = "Missing body" });

            var pending = r.Pairings.Find(request.CodeId, now);
            if (pending is null)
            {
                r.Throttle.RecordFailure(peer, now);
                r.Events.Add("pair", peer, "unknown or expired pairing code");
                return Results.Json(new { error = "Unknown or expired pairing code" }, statusCode: 403);
            }

            var name = pending.Label ?? CleanName(request.Name);
            Credential? issued = null;
            var answer = Pairing.Answer(pending.Code, request, () =>
            {
                if (!r.Pairings.Consume(pending)) return null; // someone else used it a moment ago
                var (credential, token) = r.Store.Issue(name, CredentialRole.Client);
                issued = credential;
                return new PairGrant { Token = token, CredentialId = credential.Id, Name = credential.Label, CertSha256 = r.CertSha256 };
            });

            if (answer is null)
            {
                if (issued is null && r.Pairings.Find(pending.Id, now) is not null) r.Pairings.Fail(pending);
                r.Throttle.RecordFailure(peer, now);
                r.Events.Add("pair", peer, "pairing proof did not match the code");
                return Results.Json(new { error = "Wrong pairing code" }, statusCode: 403);
            }

            r.Pairings.Complete(pending, issued!.Label, issued.Id, peer, now);
            r.Events.Add("pair", name, $"paired from {peer}");
            return Results.Ok(answer);
        });
    }

    private static object Describe(RelayContext r, PendingPairing p)
    {
        var install = r.InstallCommands(p.Code, p.ServerUrl);
        return new
        {
            id = p.Id,
            code = p.Code,
            name = p.Label,
            serverUrl = p.ServerUrl ?? r.PublicUrl,
            createdUtc = p.CreatedUtc,
            expiresUtc = p.ExpiresUtc,
            // Label → command, as the CLI prints it; the dashboard uses the list with stable ids.
            commands = install.ToDictionary(c => c.Label, c => c.Command),
            install,
        };
    }

    private static bool TryRole(string text, out CredentialRole role)
        => Enum.TryParse(text, ignoreCase: true, out role) && Enum.IsDefined(role);

    /// <summary>A machine-proposed name, kept printable and short.</summary>
    private static string CleanName(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).Take(64).ToArray()).Trim();
        return clean.Length == 0 ? "machine" : clean;
    }

    private static void SetSessionCookie(HttpContext ctx, string session)
        => ctx.Response.Cookies.Append(AdminGate.CookieName, session, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = AdminGate.SessionLife,
        });
}

using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    int Port,
    int AdminPort,
    bool NoTls,
    string? CertSha256)
{
    /// <summary>Where the install scripts are fetched from.</summary>
    public const string InstallBase = "https://raw.githubusercontent.com/nks-hub/remote-cmd/main";

    /// <summary>
    /// The address clients should use: the one set in the dashboard, or a guess from this machine's
    /// first private IPv4 address.
    /// </summary>
    public string PublicUrl => Store.PublicUrl ?? $"{(NoTls ? "http" : "https")}://{GuessAddress()}:{Port}";

    private static string GuessAddress()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToList();
        var lan = addresses.FirstOrDefault(a => a.GetAddressBytes() is [192, 168, ..] or [10, ..] or [172, >= 16 and <= 31, ..]);
        return (lan ?? addresses.FirstOrDefault())?.ToString() ?? Environment.MachineName;
    }

    /// <summary>Ready-to-paste commands that install a client and pair it with this code.</summary>
    public Dictionary<string, string> InstallCommands(string code) => new()
    {
        ["Windows (PowerShell as admin)"] =
            $"& ([scriptblock]::Create((irm {InstallBase}/install.ps1))) -Server '{PublicUrl}' -Code '{code}'",
        ["Linux / macOS"] =
            $"curl -fsSL {InstallBase}/install.sh | sudo sh -s -- client --server '{PublicUrl}' --code '{code}'",
        ["Client already installed"] =
            $"RemoteCmd.Client pair {PublicUrl} {code} --install-service",
    };
}

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
    private sealed record PairBody(string? Name, int? TtlMinutes);

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
            if (body.PublicUrl is { Length: > 0 } url && !Uri.TryCreate(url, UriKind.Absolute, out _))
                return Results.BadRequest(new { error = "Not a URL" });
            r.Store.PublicUrl = body.PublicUrl;
            return Results.Ok(new { publicUrl = r.PublicUrl });
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
            var ttl = body.TtlMinutes is > 0 ? TimeSpan.FromMinutes(body.TtlMinutes.Value) : (TimeSpan?)null;
            var pending = r.Pairings.Create(body.Name, ttl, DateTime.UtcNow);
            r.Events.Add("pair", pending.Label ?? "-", $"pairing code issued, valid until {pending.ExpiresUtc.ToLocalTime():HH:mm}");
            return Results.Ok(Describe(r, pending));
        });

        app.MapGet("/api/admin/pair", () => Results.Ok(new
        {
            pending = r.Pairings.Active(DateTime.UtcNow).Select(p => Describe(r, p)),
        }));

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

            r.Events.Add("pair", name, $"paired from {peer}");
            return Results.Ok(answer);
        });
    }

    private static object Describe(RelayContext r, PendingPairing p) => new
    {
        id = p.Id,
        code = p.Code,
        name = p.Label,
        expiresUtc = p.ExpiresUtc,
        commands = r.InstallCommands(p.Code),
    };

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

using System.Text.Json.Serialization;

namespace RemoteCmd.Client;

/// <summary>
/// Connection settings shared by console and service hosting, and the shape of the client's config
/// file. <see cref="CertSha256"/> pins the relay's TLS certificate; without it any certificate is
/// accepted, as clients did before pairing existed.
/// </summary>
public sealed record ClientConfig(
    [property: JsonPropertyName("server")] string ServerArg,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("keepAwake")] bool KeepAwake = false,
    [property: JsonPropertyName("certSha256")] string? CertSha256 = null)
{
    [JsonIgnore]
    public string BaseUrl => NormalizeServer(ServerArg);

    /// <summary>
    /// Accepts what people actually type: a bare host, host:port or a full URL. Without a scheme it is
    /// https on the relay's port 7890; a URL with a scheme keeps that scheme's own default port, so a
    /// relay behind a reverse proxy on 443 still works.
    /// </summary>
    public static string NormalizeServer(string server)
    {
        var s = server.Trim().TrimEnd('/');
        if (s.Contains("://")) return new Uri(s).GetLeftPart(UriPartial.Authority);
        var host = s.Split('/')[0];
        var hasPort = host.LastIndexOf(':') > host.LastIndexOf(']');
        return hasPort ? $"https://{host}" : $"https://{host}:7890";
    }
}

public sealed class PollResponse
{
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    /// <summary>How long this command may run. Absent from relays older than this field; zero then
    /// falls back to the default.</summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; }
}

public sealed class EncryptedFilePoll
{
    [JsonPropertyName("e")]
    public string? E { get; set; }
}

public sealed class FilePollMeta
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>
    /// Identifies this transfer. Echoed back on file-data, file-done and file-upload so the relay
    /// can tell which one is being answered; relays from before this field simply omit it.
    /// </summary>
    [JsonPropertyName("transferId")]
    public string? TransferId { get; set; }
}

public sealed class CommandResult
{
    [JsonPropertyName("output")]
    public string Output { get; set; } = "";

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }
}

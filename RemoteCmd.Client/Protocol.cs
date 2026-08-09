using System.Text.Json.Serialization;

namespace RemoteCmd.Client;

/// <summary>Connection settings shared by console and service hosting.</summary>
public sealed record ClientConfig(string ServerArg, string Token, string Name)
{
    public string BaseUrl => ServerArg.StartsWith("http")
        ? ServerArg.TrimEnd('/')
        : $"https://{ServerArg}";
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

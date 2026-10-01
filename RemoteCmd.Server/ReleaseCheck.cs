using System.Net.Http.Json;
using System.Text.Json;

/// <summary>
/// Looks up the newest published release, so the dashboard can say whether the relay and each
/// machine run it. Asked once at start and then every six hours; a relay with no internet access
/// simply never knows, and says so rather than guessing.
/// </summary>
public sealed class ReleaseCheck
{
    public const string LatestUrl = "https://api.github.com/repos/nks-hub/remote-cmd/releases/latest";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Newest release version without the leading "v", or null when unknown.</summary>
    public string? Latest { get; private set; }

    public void Start(CancellationToken stopping)
    {
        if (Environment.GetEnvironmentVariable("REMOTECMD_NO_UPDATE_CHECK") == "1") return;
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"RemoteCmd-Relay/{RemoteCmd.Shared.VersionInfo.Version}");
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    var release = await http.GetFromJsonAsync<JsonElement>(LatestUrl, stopping);
                    Latest = release.GetProperty("tag_name").GetString()?.TrimStart('v');
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* offline: stays unknown */ }
                try { await Task.Delay(Interval, stopping); } catch (OperationCanceledException) { return; }
            }
        }, stopping);
    }

    /// <summary>
    /// True only when both versions are known and the given one is older. "dev" builds and clients
    /// too old to report a version are not called outdated — that is unknown, not behind.
    /// </summary>
    public bool IsOutdated(string? version)
        => Latest is { } latest
           && Version.TryParse(Normalize(version), out var have)
           && Version.TryParse(Normalize(latest), out var newest)
           && have < newest;

    private static string? Normalize(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        v = v.TrimStart('v');
        var dash = v.IndexOf('-');
        return dash > 0 ? v[..dash] : v;
    }
}

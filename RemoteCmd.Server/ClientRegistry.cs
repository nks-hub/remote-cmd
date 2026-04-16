using System.Collections.Concurrent;

/// <summary>
/// Session lifecycle helpers. Split into a dedicated static class so they're trivially unit-testable
/// without spinning up a WebApplicationFactory.
/// </summary>
public static class ClientRegistry
{
    /// <summary>
    /// Drop sessions that have not polled within <paramref name="staleAfter"/>. Returns the number removed.
    /// Sessions that have never polled (LastPoll == DateTime.MinValue) are retained to avoid racing with
    /// brand-new registrations.
    /// </summary>
    public static int PruneStale(
        ConcurrentDictionary<string, ClientSession> clients,
        TimeSpan staleAfter,
        DateTime now)
    {
        var count = 0;
        foreach (var kvp in clients)
        {
            var lastPoll = kvp.Value.LastPoll;
            if (lastPoll == DateTime.MinValue) continue;
            if (now - lastPoll <= staleAfter) continue;
            if (clients.TryRemove(kvp.Key, out var removed))
            {
                // Fault any pending tasks so callers unblock promptly.
                removed.ResultTcs?.TrySetResult(new CommandResult { Output = "[ERROR] Session pruned", ExitCode = -1 });
                removed.UploadTcs?.TrySetResult(false);
                removed.DownloadTcs?.TrySetResult(new FileTransfer { Error = "Session pruned" });
                count++;
            }
        }
        return count;
    }
}

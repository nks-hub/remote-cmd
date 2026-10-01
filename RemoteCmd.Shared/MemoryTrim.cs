namespace RemoteCmd.Shared;

/// <summary>
/// Hands the memory of a finished file transfer back to the OS. A transfer leaves its buffers on
/// the large object heap, which is only swept by a full collection — and an idle relay or client
/// allocates so little that one may not come for days, so a single 150 MB file kept ~400 MB
/// resident long after it was written. Transfers are rare, so one forced compacting collection
/// after each large one is cheap.
/// </summary>
public static class MemoryTrim
{
    /// <summary>Transfers below this size leave too little behind to be worth a full collection.</summary>
    public const long Threshold = 8 * 1024 * 1024;

    /// <summary>
    /// Waits a moment before collecting: when the transfer reports done, the HTTP request or
    /// response that carried it still holds the payload, and collecting then frees nothing.
    /// </summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    public static void AfterTransfer(long bytes)
    {
        if (bytes < Threshold) return;
        // Aggressive, not merely forced: a forced compacting collection empties the heap but keeps the
        // freed regions committed, handing them back only a step at a time on later collections —
        // which an idle process never runs, so ~300 MB stayed private after one 150 MB file.
        _ = Task.Delay(Delay).ContinueWith(
            _ => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true),
            TaskScheduler.Default);
    }
}

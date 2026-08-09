using System.Collections.Concurrent;

/// <summary>
/// One queued file transfer. Each has its own id and its own completion, so two callers uploading to
/// the same machine at the same time cannot be handed each other's bytes.
/// </summary>
public sealed class FileJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Path { get; init; } = "";
    /// <summary>Payload for an upload; null for a download, which is filled in by the client.</summary>
    public byte[]? Data { get; init; }
    /// <summary>Completes with null on success, or with the reason it failed.</summary>
    public TaskCompletionSource<string?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>What the client sent back, for a download.</summary>
    public TaskCompletionSource<FileTransfer> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// A client's file transfers, queued rather than held in a single slot.
///
/// The relay used to keep exactly one PendingUpload and one PendingDownload per session, so a second
/// request simply overwrote the first: the client then read the metadata of one transfer and the
/// bytes of another, and wrote the wrong file to the wrong path. Everything here is keyed by a
/// transfer id, and only one transfer is handed out at a time, so concurrent callers queue up
/// instead of clobbering one another.
/// </summary>
public sealed class TransferQueue
{
    private readonly ConcurrentQueue<FileJob> _waiting = new();
    private readonly Lock _gate = new();
    private FileJob? _current;

    /// <summary>The transfer the client is working on right now, if any.</summary>
    public FileJob? Current { get { lock (_gate) return _current; } }

    public bool Idle { get { lock (_gate) return _current == null && _waiting.IsEmpty; } }

    public void Enqueue(FileJob job) => _waiting.Enqueue(job);

    /// <summary>
    /// Hands the client its next transfer, or repeats the one already in progress — a client that
    /// polls twice before finishing must not be given a second job it will never come back for.
    /// </summary>
    public FileJob? Lease()
    {
        lock (_gate)
        {
            if (_current != null) return _current;
            return _waiting.TryDequeue(out var next) ? _current = next : null;
        }
    }

    /// <summary>
    /// Finishes the transfer the client names, or the one in progress when it names none (clients
    /// from before transfer ids). Returns null when there is nothing to finish.
    /// </summary>
    public FileJob? Release(string? id)
    {
        lock (_gate)
        {
            var job = _current;
            if (job == null) return null;
            if (!string.IsNullOrEmpty(id) && job.Id != id) return null;
            _current = null;
            return job;
        }
    }

    /// <summary>Fails everything still queued, so callers unblock instead of waiting out a timeout.</summary>
    public void FailAll(string reason)
    {
        lock (_gate)
        {
            _current?.Done.TrySetResult(reason);
            _current?.Result.TrySetResult(new FileTransfer { Error = reason });
            _current = null;
            while (_waiting.TryDequeue(out var job))
            {
                job.Done.TrySetResult(reason);
                job.Result.TrySetResult(new FileTransfer { Error = reason });
            }
        }
    }
}

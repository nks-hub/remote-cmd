// Thread-safe relay state - extracted for testability

/// <summary>
/// Thread-safe state container for command and file transfer relay between server and client.
/// </summary>
sealed class RelayState
{
    private readonly object _lock = new();

    private string? _pendingCommand;
    private TaskCompletionSource<CommandResult>? _resultTcs;
    private DateTime _lastClientPoll = DateTime.MinValue;

    private FileTransfer? _pendingUpload;
    private TaskCompletionSource<bool>? _uploadTcs;
    private FileTransfer? _pendingDownload;
    private TaskCompletionSource<FileTransfer>? _downloadTcs;

    public DateTime LastClientPoll
    {
        get { lock (_lock) return _lastClientPoll; }
    }

    public bool IsClientConnected
    {
        get { lock (_lock) return (DateTime.UtcNow - _lastClientPoll).TotalSeconds < 10; }
    }

    public bool HasPendingDownload
    {
        get { lock (_lock) return _downloadTcs != null; }
    }

    public void UpdateLastPoll()
    {
        lock (_lock) _lastClientPoll = DateTime.UtcNow;
    }

    /// <summary>Sets pending command and returns its TCS. Returns null if a command is already pending.</summary>
    public TaskCompletionSource<CommandResult>? TrySetCommand(string command)
    {
        lock (_lock)
        {
            if (_pendingCommand != null) return null;
            _resultTcs = new TaskCompletionSource<CommandResult>();
            _pendingCommand = command;
            return _resultTcs;
        }
    }

    /// <summary>Takes the pending command (clears it). Returns null if none.</summary>
    public string? TakeCommand()
    {
        lock (_lock)
        {
            var cmd = _pendingCommand;
            _pendingCommand = null;
            return cmd;
        }
    }

    public void SetResult(CommandResult result)
    {
        lock (_lock) _resultTcs?.TrySetResult(result);
    }

    public void CancelCommand()
    {
        lock (_lock) _pendingCommand = null;
    }

    /// <summary>Sets pending upload and returns its TCS. Returns null if an upload is already pending.</summary>
    public TaskCompletionSource<bool>? TrySetUpload(FileTransfer transfer)
    {
        lock (_lock)
        {
            if (_pendingUpload != null) return null;
            _pendingUpload = transfer;
            _uploadTcs = new TaskCompletionSource<bool>();
            return _uploadTcs;
        }
    }

    public FileTransfer? PeekUpload()
    {
        lock (_lock) return _pendingUpload;
    }

    public void CompleteUpload()
    {
        lock (_lock)
        {
            _pendingUpload = null;
            _uploadTcs?.TrySetResult(true);
            _uploadTcs = null;
        }
    }

    public void CancelUpload()
    {
        lock (_lock)
        {
            _pendingUpload = null;
            _uploadTcs = null;
        }
    }

    /// <summary>Sets pending download request and returns its TCS. Returns null if a download is already pending.</summary>
    public TaskCompletionSource<FileTransfer>? TrySetDownload(FileTransfer transfer)
    {
        lock (_lock)
        {
            if (_downloadTcs != null) return null;
            _pendingDownload = transfer;
            _downloadTcs = new TaskCompletionSource<FileTransfer>();
            return _downloadTcs;
        }
    }

    public FileTransfer? PeekDownload()
    {
        lock (_lock) return _pendingDownload;
    }

    public void CompleteDownload(byte[] data)
    {
        lock (_lock)
        {
            _downloadTcs?.TrySetResult(new FileTransfer { Data = data });
            _pendingDownload = null;
            _downloadTcs = null;
        }
    }

    public void CompleteDownloadWithError(string error)
    {
        lock (_lock)
        {
            _downloadTcs?.TrySetResult(new FileTransfer { Error = error });
            _pendingDownload = null;
            _downloadTcs = null;
        }
    }

    public void CancelDownload()
    {
        lock (_lock)
        {
            _pendingDownload = null;
            _downloadTcs = null;
        }
    }
}

record CommandRequest(string Command, int TimeoutSeconds = 30);

record CommandResult
{
    public string Output { get; set; } = "";
    public int ExitCode { get; set; }
}

class FileTransfer
{
    public string? Path { get; set; }
    public byte[]? Data { get; set; }
    public string? Error { get; set; }
}

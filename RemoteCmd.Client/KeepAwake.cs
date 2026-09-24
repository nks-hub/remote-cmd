using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteCmd.Client;

/// <summary>
/// Keeps the machine from going to sleep for as long as the client runs, so a
/// long build or copy is not cut off by idle sleep and the client does not drop
/// off the relay. Display sleep is left alone.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    private readonly Process? _inhibitor;
    private readonly Timer? _windowsTimer;

    private KeepAwake(Process? inhibitor, Timer? windowsTimer)
    {
        _inhibitor = inhibitor;
        _windowsTimer = windowsTimer;
    }

    /// <summary>
    /// Starts the platform's sleep inhibitor. Returns a handle that releases it;
    /// a platform without one gets a handle that does nothing, with a warning.
    /// </summary>
    public static KeepAwake Start(Action<string> warn)
    {
        if (OperatingSystem.IsWindows())
        {
            // Not ES_CONTINUOUS: that state belongs to the calling thread, and a
            // pool thread may die. Re-asserting resets the idle timer instead.
            var timer = new Timer(_ => SetThreadExecutionState(EsSystemRequired), null,
                TimeSpan.Zero, TimeSpan.FromSeconds(30));
            return new KeepAwake(null, timer);
        }

        var pid = Environment.ProcessId.ToString();
        var (file, arguments) = OperatingSystem.IsMacOS()
            // -i idle sleep, -m disk idle, -s system sleep on AC; -w ends it
            // with this process even if the client is killed outright.
            ? ("caffeinate", $"-ims -w {pid}")
            : ("systemd-inhibit",
                $"--what=sleep:idle --who=RemoteCmd.Client --why=\"remote commands\" " +
                $"--mode=block tail --pid={pid} -f /dev/null");
        try
        {
            var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            return new KeepAwake(process, null);
        }
        catch (Exception ex)
        {
            warn($"--keep-awake: {file} could not be started ({ex.Message}); the machine may still sleep.");
            return new KeepAwake(null, null);
        }
    }

    public void Dispose()
    {
        _windowsTimer?.Dispose();
        if (OperatingSystem.IsWindows())
        {
            SetThreadExecutionState(EsContinuous);
        }
        try
        {
            if (_inhibitor is { HasExited: false }) _inhibitor.Kill();
        }
        catch
        {
            // Already gone; caffeinate -w and tail --pid end with us anyway.
        }
        _inhibitor?.Dispose();
    }

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}

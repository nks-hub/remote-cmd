using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCmd.Client;
using Xunit;

namespace RemoteCmd.Tests;

public class PollWorkerTests
{
    /// <summary>
    /// After the relay host slept, a poll can hang until it times out. The timeout surfaces as a
    /// cancellation, and the worker used to take any cancellation for its own shutdown: the process
    /// stayed up but never polled again. It has to retry like after any other failed poll.
    /// </summary>
    [Fact]
    public async Task A_poll_that_times_out_is_retried_instead_of_ending_the_worker()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var polls = 0;
        using var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Stop();
        http.Start();
        var serving = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                HttpListenerContext context;
                try { context = await http.GetContextAsync(); }
                catch { return; }
                if (context.Request.Url!.AbsolutePath == "/api/poll" && Interlocked.Increment(ref polls) == 1)
                    continue; // the first poll never gets an answer
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync("{}"u8.ToArray());
                context.Response.Close();
            }
        });

        var worker = new PollWorker(new ClientConfig($"http://127.0.0.1:{port}", "test-token", "poll-timeout-test"), NullLogger<PollWorker>.Instance)
        {
            PollTimeout = TimeSpan.FromSeconds(1),
        };
        await worker.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Volatile.Read(ref polls) < 2 && DateTime.UtcNow < deadline) await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);
        http.Stop();
        await serving;

        Assert.True(Volatile.Read(ref polls) >= 2, $"the worker stopped polling after the timed-out request ({polls} polls)");
    }

    /// <summary>
    /// A command's output pipes were read to the end and then left for a finalizer to close. A
    /// client allocates little and so collects rarely; in between, every command held two descriptors.
    /// </summary>
    [Fact]
    public async Task Commands_do_not_leave_their_output_pipes_open()
    {
        if (OperatingSystem.IsWindows()) return; // handles are closed by the finalizer there, at no fixed time

        await PollWorker.ExecuteCommand("echo warm-up", 30, CancellationToken.None);
        var before = Directory.GetFileSystemEntries("/dev/fd").Length;
        for (var i = 0; i < 20; i++)
        {
            var (output, exitCode) = await PollWorker.ExecuteCommand("echo hello", 30, CancellationToken.None);
            Assert.Equal((0, "hello"), (exitCode, output));
        }
        var after = Directory.GetFileSystemEntries("/dev/fd").Length;

        Assert.True(after - before < 10, $"20 commands left {after - before} descriptors open");
    }

    /// <summary>
    /// A detached child keeps the command's pipe open after the command has returned. What it writes
    /// from then on must be read, so it never blocks on a full pipe, but not kept: nobody will ask
    /// for it, and one such child per build added up over a week.
    /// </summary>
    [Fact]
    public async Task Output_written_after_the_result_was_taken_is_dropped()
    {
        var child = new AnonymousPipeServerStream(PipeDirection.Out);
        var reader = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, child.ClientSafePipeHandle));
        var capture = new PollWorker.Capture();
        var pump = PollWorker.PumpAsync(reader, capture, CancellationToken.None);

        await child.WriteAsync("before"u8.ToArray());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (capture) if (capture.Text!.Length == 6) break;
            await Task.Delay(20);
        }
        Assert.Equal("before", capture.Take());

        await child.WriteAsync("after"u8.ToArray());
        child.Dispose();
        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(capture.Text);
        Assert.Throws<ObjectDisposedException>(() => reader.Peek());
    }
}

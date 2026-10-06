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
}

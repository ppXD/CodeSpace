using System.Net;
using System.Reflection;
using CodeSpace.Api.Logging;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.IntegrationTests.Settings;

/// <summary>
/// That the Seq sink is actually attached, and that an absent Seq costs nothing.
///
/// <para>The settings tests next door pin the KEYS. They pass whether or not anything reads them, which is how a
/// first version of this change survived having the sink deleted with every test still green. What is worth
/// pinning is that a configured URL means log lines leave the process — asserted by letting them arrive at a real
/// server rather than by reflecting into Serilog's internals, which would pass on a sink that is attached and
/// broken.</para>
///
/// <para>In this project rather than the unit one only because reaching <c>Program.BuildLogger</c> needs a
/// reference to the API host, which CodeSpace.UnitTests deliberately does not carry. It touches no database and
/// keeps the Unit trait, the same arrangement as <c>FakeAgentCliCollectionConventionTests</c>.</para>
/// </summary>
[Trait("Category", "Unit")]
public class SeqSinkWiringTests
{
    [Fact]
    public async Task A_configured_server_url_sends_log_lines_to_it()
    {
        using var seq = new RecordingSeq();

        using (var logger = BuildLogger(("Serilog:Seq:ServerUrl", seq.Url)))
        {
            logger.Information("the line that has to arrive {Marker}", "codespace-seq-probe");
        }

        var received = await seq.WaitForPostAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        received.ShouldNotBeNull("a configured Serilog:Seq:ServerUrl has to reach WriteTo.Seq — the settings reading the key proves nothing on its own");
        received.ShouldContain("codespace-seq-probe");
    }

    [Fact]
    public async Task A_blank_server_url_is_how_a_deployment_says_console_only()
    {
        using var seq = new RecordingSeq();

        using (var logger = BuildLogger(("Serilog:Seq:ServerUrl", "")))
        {
            logger.Information("this one stays on the console {Marker}", "codespace-seq-probe");
        }

        (await seq.WaitForPostAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            .ShouldBeNull("blanking the URL is the documented off switch, so nothing may be posted anywhere");
    }

    [Fact]
    public void Building_the_logger_reaches_no_server()
    {
        // The batched sink posts from a background timer. If it ever connected eagerly, every boot would pay for
        // it — and the shipped default points every developer at a Seq that is usually not running.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        using (var logger = BuildLogger(("Serilog:Seq:ServerUrl", $"http://127.0.0.1:{ClosedPort()}")))
        {
            logger.Information("hello");
        }

        elapsed.Stop();

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "building the logger, writing to it and disposing it must not wait on an absent Seq");
    }

    /// <summary>
    /// The expensive case: a socket that is accepted and then ignored. A refused connection fails fast; this one
    /// waits on the HTTP client's own timeout — around two hundred seconds — and <c>Log.CloseAndFlush</c> inherits
    /// the wait, so a shutdown reads as a hang.
    /// </summary>
    [Fact]
    public async Task A_server_that_never_answers_does_not_hold_the_process()
    {
        using var silent = new AcceptAndSayNothing();
        using var client = new HttpClient(new BoundedSeqPostHandler());

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() => client.PostAsync($"http://127.0.0.1:{silent.Port}/api/events/raw", new StringContent("{}"))).ConfigureAwait(false);
        elapsed.Stop();

        thrown.ShouldNotBeNull("the point is that the post ends by being abandoned rather than by being answered");
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "unbounded this waits ~200s, and the shutdown that inherits it looks like a hang");
    }

    // ─── Drivers ────────────────────────────────────────────────────────────────

    /// <summary>Calls the real private <c>BuildLogger</c>, so this cannot drift from what the process runs.</summary>
    private static Serilog.Core.Logger BuildLogger(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var build = typeof(CodeSpace.Api.Program).GetMethod("BuildLogger", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Program.BuildLogger not found — the logger is built somewhere else now, and this test is checking nothing");

        return (Serilog.Core.Logger)build.Invoke(null, [configuration, "CodeSpace.Tests"])!;
    }

    private static int ClosedPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    /// <summary>A Seq-shaped endpoint that answers 201 and keeps the first body it was given.</summary>
    private sealed class RecordingSeq : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<string> _firstPost = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingSeq()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();

            _ = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var context = await _listener.GetContextAsync().ConfigureAwait(false);

                        using (var reader = new StreamReader(context.Request.InputStream))
                            _firstPost.TrySetResult(await reader.ReadToEndAsync().ConfigureAwait(false));

                        context.Response.StatusCode = 201;
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException) { /* disposed */ }
                catch (ObjectDisposedException) { /* disposed */ }
            });
        }

        public string Url { get; }

        public async Task<string?> WaitForPostAsync(TimeSpan within)
        {
            var completed = await Task.WhenAny(_firstPost.Task, Task.Delay(within)).ConfigureAwait(false);

            return completed == _firstPost.Task ? await _firstPost.Task.ConfigureAwait(false) : null;
        }

        public void Dispose()
        {
            _listener.Close();
            _firstPost.TrySetCanceled();
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            return port;
        }
    }

    /// <summary>Accepts the connection and then says nothing at all — the shape that used to cost 200 seconds.</summary>
    private sealed class AcceptAndSayNothing : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        public AcceptAndSayNothing()
        {
            _listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                        _ = client;   // held open, never written to
                    }
                }
                catch (OperationCanceledException) { /* stopping */ }
                catch (System.Net.Sockets.SocketException) { /* stopping */ }
            });
        }

        public int Port { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}

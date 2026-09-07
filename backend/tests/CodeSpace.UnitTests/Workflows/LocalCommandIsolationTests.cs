using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using Shouldly;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class LocalCommandIsolationTests
{
    [UnavailableConfinementTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_required_but_unavailable_sandbox_rejects_short_lived_commands_before_they_start(bool stream)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-command-required-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = RuntimeSettings.Override(s => s with { RequireSandboxConfinement = true });
            var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "printf executed > marker" }, WorkingDirectory = directory, TimeoutSeconds = 10 };
            var runner = new LocalProcessRunner();
            await Should.ThrowAsync<InvalidOperationException>(() => stream ? runner.RunStreamingAsync(spec, (_, _) => Task.CompletedTask, CancellationToken.None) : runner.RunAsync(spec, CancellationToken.None));
            File.Exists(Path.Combine(directory, "marker")).ShouldBeFalse("the command must not start before the confinement refusal");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [PosixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Config_home_projection_is_isolated_for_batch_and_stream_commands_and_reclaimed_after_exit(bool stream)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-command-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = RuntimeSettings.Override(s => s with { RequireSandboxConfinement = false });
            var spec = new SandboxSpec
            {
                Command = "/bin/sh", Args = new[] { "-c", "printf '%s\\n' \"$CODESPACE_TEST_HOME\"; cat \"$CODESPACE_TEST_HOME/input.txt\"" },
                WorkingDirectory = directory, TimeoutSeconds = 10, ConfigHomeEnvVars = new[] { "CODESPACE_TEST_HOME" },
                ConfigHomeFiles = new[] { new ConfigHomeFile { RelativePath = "input.txt", Content = "projected-content" } },
            };
            var lines = new List<string>();
            var runner = new LocalProcessRunner();
            var result = stream
                ? await runner.RunStreamingAsync(spec, (line, _) => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None)
                : await runner.RunAsync(spec, CancellationToken.None);
            result.Status.ShouldBe(SandboxStatus.Success, result.Stderr);
            var output = stream ? string.Join('\n', lines) : result.Stdout;
            output.ShouldContain("projected-content");
            var home = output.Split('\n')[0];
            Path.IsPathFullyQualified(home).ShouldBeTrue();
            home.ShouldNotBe(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Directory.Exists(home).ShouldBeFalse("the short-lived command owns and reclaims its temporary configuration");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [PosixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_terminates_the_process_and_reclaims_its_projected_home(bool stream)
    {
        var directory = Directory.CreateTempSubdirectory("cs-command-cancel-").FullName;
        try
        {
            var marker = Path.Combine(directory, "started");
            var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "printf '%s' \"$CODESPACE_TEST_HOME\" > started; sleep 30; printf leaked > escaped" }, WorkingDirectory = directory, ConfigHomeEnvVars = new[] { "CODESPACE_TEST_HOME" } };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var runner = new LocalProcessRunner();
            var running = stream ? runner.RunStreamingAsync(spec, (_, _) => Task.CompletedTask, cancellation.Token) : runner.RunAsync(spec, cancellation.Token);
            while (!File.Exists(marker)) await Task.Delay(10, cancellation.Token);
            var home = await File.ReadAllTextAsync(marker);
            Directory.Exists(home).ShouldBeTrue();
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => running);
            Directory.Exists(home).ShouldBeFalse();
            File.Exists(Path.Combine(directory, "escaped")).ShouldBeFalse();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [PosixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pre_cancelled_command_never_starts(bool stream)
    {
        var directory = Directory.CreateTempSubdirectory("cs-command-pre-cancel-").FullName;
        try
        {
            var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "printf started > marker" }, WorkingDirectory = directory };
            var runner = new LocalProcessRunner();
            await Should.ThrowAsync<OperationCanceledException>(() => stream ? runner.RunStreamingAsync(spec, (_, _) => Task.CompletedTask, new CancellationToken(true)) : runner.RunAsync(spec, new CancellationToken(true)));
            File.Exists(Path.Combine(directory, "marker")).ShouldBeFalse();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [PosixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_commands_have_distinct_homes_and_preserve_their_own_projected_bytes(bool stream)
    {
        var runner = new LocalProcessRunner();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var token = Guid.NewGuid().ToString("N");
            var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "printf '%s\\n' \"$CODESPACE_TEST_HOME\"; cat \"$CODESPACE_TEST_HOME/input\"" }, ConfigHomeEnvVars = new[] { "CODESPACE_TEST_HOME" }, ConfigHomeFiles = new[] { new ConfigHomeFile { RelativePath = "input", Content = token } }, TimeoutSeconds = 10 };
            var lines = new List<string>();
            var result = stream ? await runner.RunStreamingAsync(spec, (line, _) => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None) : await runner.RunAsync(spec, CancellationToken.None);
            result.Status.ShouldBe(SandboxStatus.Success, result.Stderr);
            var output = stream ? string.Join('\n', lines) : result.Stdout;
            var parts = output.Split('\n');
            parts[1].ShouldBe(token);
            Directory.Exists(parts[0]).ShouldBeFalse();
            return parts[0];
        }));
        results.Distinct().Count().ShouldBe(8);
    }

    [UnavailableConfinementTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_parent_that_exits_leaving_an_inherited_pipe_cannot_hold_observation_past_the_deadline(bool stream)
    {
        var directory = Directory.CreateTempSubdirectory("cs-command-pipe-").FullName;
        try
        {
            using var settings = RuntimeSettings.Override(s => s with { RequireSandboxConfinement = false });
            var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "sleep 30 & echo $! > child; exit 0" }, WorkingDirectory = directory, TimeoutSeconds = 1 };
            var runner = new LocalProcessRunner();
            var timer = Stopwatch.StartNew();
            var running = stream ? runner.RunStreamingAsync(spec, (_, _) => Task.CompletedTask, CancellationToken.None) : runner.RunAsync(spec, CancellationToken.None);
            var result = await running.WaitAsync(TimeSpan.FromSeconds(8));
            result.Status.ShouldBe(SandboxStatus.TimedOut);
            timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
        }
        finally
        {
            // Unconfined macOS cannot fence orphan descendants. The test owns and explicitly reaps its canary.
            var marker = Path.Combine(directory, "child");
            if (File.Exists(marker) && int.TryParse(await File.ReadAllTextAsync(marker), out var pid))
            {
                try { using var child = Process.GetProcessById(pid); child.Kill(); }
                catch (ArgumentException) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnavailableConfinementTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_denial_does_not_change_successful_execution_into_a_retryable_failure(bool stream)
    {
        var directory = Directory.CreateTempSubdirectory("cs-command-cleanup-").FullName;
        string? home = null;
        try
        {
            using var settings = RuntimeSettings.Override(s => s with { RequireSandboxConfinement = false });
            var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "printf '%s' \"$CODESPACE_TEST_HOME\" > home; mkdir \"$CODESPACE_TEST_HOME/locked\"; touch \"$CODESPACE_TEST_HOME/locked/file\"; chmod 000 \"$CODESPACE_TEST_HOME/locked\"; printf committed" }, WorkingDirectory = directory, ConfigHomeEnvVars = new[] { "CODESPACE_TEST_HOME" }, TimeoutSeconds = 10 };
            var logger = new CleanupLogger();
            var runner = new LocalProcessRunner(logger);
            var lines = new List<string>();
            var result = stream ? await runner.RunStreamingAsync(spec, (line, _) => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None) : await runner.RunAsync(spec, CancellationToken.None);
            home = await File.ReadAllTextAsync(Path.Combine(directory, "home"));
            result.Status.ShouldBe(SandboxStatus.Success);
            (stream ? string.Join('\n', lines) : result.Stdout).ShouldBe("committed");
            Directory.Exists(home).ShouldBeTrue("the permission denial must actually exercise cleanup failure");
            logger.Warnings.ShouldContain(message => message.Contains("execution outcome is preserved"));
        }
        finally
        {
            home ??= File.Exists(Path.Combine(directory, "home")) ? await File.ReadAllTextAsync(Path.Combine(directory, "home")) : null;
            if (home is not null && Directory.Exists(home))
            {
                File.SetUnixFileMode(Path.Combine(home, "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(home, recursive: true);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnavailableConfinementTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stream_consumer_failure_is_preserved_even_with_a_surviving_pipe_or_unreadable_config(bool denyCleanup)
    {
        var directory = Directory.CreateTempSubdirectory("cs-command-consumer-").FullName;
        string? home = null;
        try
        {
            using var settings = RuntimeSettings.Override(s => s with { RequireSandboxConfinement = false });
            var cleanup = denyCleanup ? "mkdir \"$CODESPACE_TEST_HOME/locked\"; touch \"$CODESPACE_TEST_HOME/locked/file\"; chmod 000 \"$CODESPACE_TEST_HOME/locked\"; " : "";
            var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "printf '%s' \"$CODESPACE_TEST_HOME\" > home; " + cleanup + "sleep 30 & echo $! > child; printf 'ready\\n'; exit 0" }, WorkingDirectory = directory, ConfigHomeEnvVars = new[] { "CODESPACE_TEST_HOME" }, TimeoutSeconds = 10 };
            var original = new InvalidOperationException("consumer failed");
            var runner = new LocalProcessRunner();
            var failure = await Should.ThrowAsync<InvalidOperationException>(() => runner.RunStreamingAsync(spec, async (_, _) => { await Task.Delay(200); throw original; }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8)));
            failure.ShouldBeSameAs(original);
        }
        finally
        {
            var marker = Path.Combine(directory, "child");
            if (File.Exists(marker) && int.TryParse(await File.ReadAllTextAsync(marker), out var pid))
            {
                try { using var child = Process.GetProcessById(pid); child.Kill(); }
                catch (ArgumentException) { }
            }
            home = File.Exists(Path.Combine(directory, "home")) ? await File.ReadAllTextAsync(Path.Combine(directory, "home")) : null;
            if (home is not null && Directory.Exists(home))
            {
                if (denyCleanup) File.SetUnixFileMode(Path.Combine(home, "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(home, recursive: true);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CleanupLogger : ILogger<LocalProcessRunner>
    {
        public List<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class UnavailableConfinementTheoryAttribute : TheoryAttribute
    {
        public UnavailableConfinementTheoryAttribute()
        {
            if (OperatingSystem.IsWindows() || BubblewrapSandbox.Available is not null) Skip = "This refusal case needs a POSIX host without confinement; the kernel suite covers available confinement.";
        }
    }

    private sealed class PosixTheoryAttribute : TheoryAttribute
    {
        public PosixTheoryAttribute()
        {
            if (OperatingSystem.IsWindows()) Skip = "The process fixture uses a POSIX shell.";
        }
    }
}

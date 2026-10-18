using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Health;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Health;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.UnitTests.Health;

/// <summary>
/// The line a pod prints once it is serving: build, role, git. It is the log-side twin of <c>/health</c> — what lets two
/// pods be compared from a log search when nothing can reach their health endpoint — and it starts the refresh-lock
/// self-test so a lock that is not exclusive is an Error at boot rather than a finding nobody asked for.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BootPostureReporterTests
{
    [Fact]
    public async Task It_logs_one_line_naming_the_build_the_role_and_git()
    {
        var logger = new CapturingLogger();
        var reporter = NewReporter(new HostPosture(HangfireHosting.Api, "1.0.0+abc123"), logger);
        using var _ = HostGit.Use(Reading("git version 2.39.5"));

        await reporter.ReportAsync(CancellationToken.None);

        var line = logger.Entries.Where(e => e.Message.StartsWith("Boot posture:", StringComparison.Ordinal)).ToList().ShouldHaveSingleItem();

        line.Level.ShouldBe(LogLevel.Information);
        line.Properties["Build"].ShouldBe("1.0.0+abc123");
        line.Properties["Role"].ShouldBe("Api");
        line.Properties["GitVersion"].ShouldBe("2.39.5");
        line.Message.ShouldContain("1.0.0+abc123");
        line.Message.ShouldContain("Api");
        line.Message.ShouldContain("2.39.5");
    }

    [Fact]
    public async Task A_git_below_the_floor_is_also_a_Warning_with_the_cause()
    {
        var logger = new CapturingLogger();
        using var _ = HostGit.Use(Reading("git version 2.30.2"));

        await NewReporter(new HostPosture(HangfireHosting.Worker, "1.0.0+abc123"), logger).ReportAsync(CancellationToken.None);

        var warning = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList().ShouldHaveSingleItem();

        warning.Message.ShouldContain("2.30.2");
        warning.Message.ShouldContain("GIT_CONFIG_COUNT");
    }

    [Fact]
    public async Task A_current_git_logs_no_Warning()
    {
        var logger = new CapturingLogger();
        using var _ = HostGit.Use(Reading("git version 2.39.5"));

        await NewReporter(new HostPosture(HangfireHosting.Worker, "1.0.0+abc123"), logger).ReportAsync(CancellationToken.None);

        logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task It_runs_the_refresh_lock_self_test_so_its_finding_is_logged_at_boot()
    {
        var test = new CountingSelfTest();
        using var _ = HostGit.Use(Reading("git version 2.39.5"));

        await NewReporter(new HostPosture(HangfireHosting.Api, "1.0.0+abc123"), new CapturingLogger(), test).ReportAsync(CancellationToken.None);

        test.Asks.ShouldBe(1);
    }

    [Fact]
    public async Task A_self_test_that_throws_never_escapes_the_boot_path()
    {
        var logger = new CapturingLogger();
        using var _ = HostGit.Use(Reading("git version 2.39.5"));

        await Should.NotThrowAsync(() => NewReporter(new HostPosture(HangfireHosting.Api, "1.0.0+abc123"), logger, new CountingSelfTest { Throws = true }).ReportAsync(CancellationToken.None));

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task It_reports_when_the_host_has_started_and_not_before()
    {
        var logger = new CapturingLogger();
        var lifetime = new FakeLifetime();
        using var _ = HostGit.Use(Reading("git version 2.39.5"));
        var reporter = new BootPostureReporter(new HostPosture(HangfireHosting.Api, "1.0.0+abc123"), new CountingSelfTest(), lifetime, logger);

        reporter.ReportWhenStarted();

        logger.Entries.ShouldBeEmpty("nothing is said before the host is serving");

        lifetime.Start();

        await Eventually(() => logger.Entries.Any(e => e.Message.StartsWith("Boot posture:", StringComparison.Ordinal)));
    }

    private static BootPostureReporter NewReporter(HostPosture posture, CapturingLogger logger, CountingSelfTest? test = null) =>
        new(posture, test ?? new CountingSelfTest(), new FakeLifetime(), logger);

    private static GitReading Reading(string output) => new(GitVersion.Parse(output), output);

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);

        condition().ShouldBeTrue("the report was never logged after the host started");
    }

    private sealed class CountingSelfTest : IRefreshLockSelfTest
    {
        public int Asks { get; private set; }

        public bool Throws { get; init; }

        public Task<RefreshLockVerdict> VerdictAsync(CancellationToken cancellationToken)
        {
            Asks++;

            return Throws ? throw new InvalidOperationException("boom") : Task.FromResult(new RefreshLockVerdict(RefreshLockOutcome.Exclusive, "ok", DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Start() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class CapturingLogger : ILogger<BootPostureReporter>
    {
        private readonly object _gate = new();
        private readonly List<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries { get { lock (_gate) return _entries.ToList(); } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = (state as IEnumerable<KeyValuePair<string, object?>> ?? []).ToDictionary(p => p.Key, p => p.Value?.ToString());

            lock (_gate) _entries.Add(new Entry(logLevel, formatter(state, exception), exception, properties));
        }

        public sealed record Entry(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, string?> Properties);
    }
}

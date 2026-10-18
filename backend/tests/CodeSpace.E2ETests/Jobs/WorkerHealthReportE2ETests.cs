using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using CodeSpace.Core.Services.Health;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Health;
using Serilog.Core;
using Serilog.Events;
using Shouldly;

namespace CodeSpace.E2ETests.Jobs;

/// <summary>
/// What a Worker-role pod says about itself, over HTTP and in its boot log, from the real worker host: the same fixture and
/// the same <c>WorkerHangfireRegistrar</c> that register the recurring jobs on a deployment's worker. The API-role half is
/// <c>HealthEndpointE2ETests</c>; together they are the "compare the two images from outside" surface.
///
/// <para>Tier: 🟢 High-fidelity (Rule 12) — real host, real Postgres, real Hangfire storage, the production health checks and
/// self-test. Substituted: the host runs as Development, like every sibling fixture (see
/// <see cref="RecurringJobWorkerHostFactory"/>), and the refresh-lock "pooler" case is covered at the integration tier because
/// no pooler is stood up here.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Worker")]
public sealed class WorkerHealthReportE2ETests
{
    private static readonly TimeSpan BootLineBudget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_worker_pod_reports_its_build_and_role_a_scheduled_sweep_and_an_exclusive_refresh_lock()
    {
        var sink = new CapturedEvents();

        await using var factory = new RecurringJobWorkerHostFactory(sink);
        await factory.InitializeAsync();

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "an anonymous request must be answered: a probe cannot sign in");

        var anonymous = (await response.Content.ReadFromJsonAsync<HealthResponse>()).ShouldNotBeNull();

        anonymous.Build.ShouldBe(CodeSpace.Core.Settings.Logging.BuildIdentity.Value, "the build /health names is the one every log line carries");
        anonymous.Role.ShouldBe("Worker");
        anonymous.ProcessesJobs.ShouldBeTrue();
        anonymous.DetailsIncluded.ShouldBeFalse();
        Entry(anonymous, "credential-repair-sweep").Status.ShouldBe("Healthy", "the Worker role registers every recurring job when it boots, so the sweep is scheduled");
        Entry(anonymous, "refresh-lock").Status.ShouldBe("Healthy");
        anonymous.Checks.ShouldAllBe(check => check.Description == null && check.Data.Count == 0);

        var report = await AdminReportAsync(factory);
        var sweep = Entry(report, "credential-repair-sweep");

        report.DetailsIncluded.ShouldBeTrue();
        sweep.Status.ShouldBe("Healthy", sweep.Description);
        Fact(sweep, "registered").GetBoolean().ShouldBeTrue("the Worker role registers every recurring job when it boots, so the sweep is scheduled");
        Fact(sweep, "strandedCandidates").GetInt32().ShouldBe(0, "a fresh database has nothing to repair");

        var refreshLock = Entry(report, "refresh-lock");

        refreshLock.Status.ShouldBe("Healthy", refreshLock.Description);
        Fact(refreshLock, "outcome").GetString().ShouldBe("Exclusive");
    }

    [Fact]
    public async Task A_worker_pod_logs_its_boot_posture_once_naming_build_and_role()
    {
        var sink = new CapturedEvents();

        await using var factory = new RecurringJobWorkerHostFactory(sink);
        await factory.InitializeAsync();

        _ = factory.Services;   // builds and starts the host, which fires ApplicationStarted

        var line = await WaitForBootLineAsync(sink);

        line.Level.ShouldBe(LogEventLevel.Information);
        line.Properties["Build"].ToString().Trim('"').ShouldBe(CodeSpace.Core.Settings.Logging.BuildIdentity.Value);
        line.Properties["Role"].ToString().Trim('"').ShouldBe("Worker");
        line.Properties.ShouldContainKey("GitVersion");
        sink.Events().Count(IsBootPostureLine).ShouldBe(1, "the host reports once, however many times it is asked about");
    }

    /// <summary>The report as a signed-in global Admin reads it — the instance <c>Admin</c> role is a database row, so the account is seeded with it.</summary>
    private static async Task<HealthResponse> AdminReportAsync(RecurringJobWorkerHostFactory factory)
    {
        var harness = new TenantHarness(factory);
        var admin = await harness.AccountAsync();

        await harness.MakeGlobalAdminAsync(admin);

        var response = await harness.SendAsync(HttpMethod.Get, "/health", admin, teamId: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        return (await response.Content.ReadFromJsonAsync<HealthResponse>()).ShouldNotBeNull();
    }

    private static HealthCheckEntry Entry(HealthResponse report, string name) => report.Checks.Single(check => check.Name == name);

    private static System.Text.Json.JsonElement Fact(HealthCheckEntry entry, string key) => ((System.Text.Json.JsonElement)entry.Data[key]);

    private static bool IsBootPostureLine(LogEvent logEvent) => logEvent.MessageTemplate.Text.StartsWith("Boot posture:", StringComparison.Ordinal);

    /// <summary>The report is made off the thread that starts the host, so the line arrives shortly after it; the wait is bounded and names what never came.</summary>
    private static async Task<LogEvent> WaitForBootLineAsync(CapturedEvents sink)
    {
        var deadline = DateTime.UtcNow + BootLineBudget;

        while (DateTime.UtcNow < deadline)
        {
            if (sink.Events().FirstOrDefault(IsBootPostureLine) is { } line) return line;

            await Task.Delay(50);
        }

        throw new ShouldAssertException($"no \"Boot posture:\" line was logged within {BootLineBudget.TotalSeconds:0}s of the host starting. BootPostureReporter.ReportWhenStarted must be called from Startup.Configure, and the host's logger must let {nameof(BootPostureReporter)} through at Information.");
    }

    private sealed class CapturedEvents : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

        public IReadOnlyList<LogEvent> Events() => _events.ToArray();
    }
}

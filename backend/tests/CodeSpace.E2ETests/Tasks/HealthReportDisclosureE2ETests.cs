using System.Net;
using System.Net.Http.Json;
using CodeSpace.Core.Services.Health.Checks;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Health;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// What <c>GET /health</c> prints about a scheduler fault, over the real pipeline and from real Hangfire storage. Hangfire's
/// scheduler stores the exception chain of a failed schedule — up to a hundred stack frames, and for a storage fault the
/// server's host and port — in the recurring job's <c>Error</c> field. The report says an error was recorded and never repeats
/// it, for an anonymous caller and for a global Admin alike: the full text is in the pod's log.
///
/// <para>Own fixture, own database: the entry written here would otherwise change what every other health test sees, and the
/// check reuses a reading for thirty seconds.</para>
///
/// <para>Tier: 🟢 High-fidelity — real app host, real Postgres, the real middleware pipeline and the production sweep check,
/// over a schedule entry written field for field the way Hangfire's scheduler leaves a disabled one
/// (<c>WorkerFleetCapabilityE2ETests</c> does the same for the whole schedule).</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class HealthReportDisclosureE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string StorageFault =
        "Npgsql.NpgsqlException: Failed to connect to 10.20.30.40:5432\n ---> System.Net.Sockets.SocketException (111): Connection refused\n" +
        "   at Npgsql.Internal.NpgsqlConnector.Connect(NpgsqlTimeout timeout)\n   at Hangfire.PostgreSql.PostgreSqlJobQueue.Enqueue(String queue, String jobId)";

    private readonly TaskLaunchApiFactory _factory;

    public HealthReportDisclosureE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_scheduler_error_stored_with_a_storage_address_and_stack_frames_reaches_neither_an_anonymous_caller_nor_an_admin()
    {
        DisableTheSweepAsAFailingSchedulerDoes();

        var anonymous = await _factory.CreateClient().GetAsync("/health");
        var anonymousBody = await anonymous.Content.ReadAsStringAsync();

        anonymous.StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldHoldNothingOfTheFault(anonymousBody);

        var harness = new TenantHarness(_factory);
        var admin = await harness.AccountAsync();

        await harness.MakeGlobalAdminAsync(admin);

        var response = await harness.SendAsync(HttpMethod.Get, "/health", admin, teamId: null);
        var adminBody = await response.Content.ReadAsStringAsync();
        var report = (await response.Content.ReadFromJsonAsync<HealthResponse>()).ShouldNotBeNull();

        ShouldHoldNothingOfTheFault(adminBody);
        report.Checks.Single(check => check.Name == "credential-repair-sweep").Description.ShouldNotBeNull().ShouldContain("recorded an error", customMessage: "the Admin is told there is one, and where its text is");
    }

    private void DisableTheSweepAsAFailingSchedulerDoes()
    {
        var storage = _factory.Services.GetRequiredService<JobStorage>();

        using var connection = storage.GetConnection();
        using var transaction = connection.CreateWriteTransaction();

        transaction.SetRangeInHash($"recurring-job:{CredentialRepairSweepHealthCheck.JobId}", new Dictionary<string, string> { ["NextExecution"] = string.Empty, ["Error"] = StorageFault, ["RetryAttempt"] = "5" });
        transaction.AddToSet("recurring-jobs", CredentialRepairSweepHealthCheck.JobId, -1.0);
        transaction.Commit();
    }

    private static void ShouldHoldNothingOfTheFault(string body)
    {
        body.ShouldNotContain("10.20.30.40", Case.Sensitive, "a storage host and port stay in the log");
        body.ShouldNotContain("Npgsql", Case.Sensitive);
        body.ShouldNotContain("PostgreSqlJobQueue", Case.Sensitive);
        body.ShouldNotContain("SocketException", Case.Sensitive);
        body.ShouldNotContain("   at ", Case.Sensitive, "no stack frame");
    }
}

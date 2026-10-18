using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Settings.Logging;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// E2E coverage for the k8s probe surface through the REAL ASP.NET pipeline. The global FallbackPolicy requires an
/// authenticated user, so the load-bearing assertion is that BOTH health endpoints answer 200 WITHOUT any auth
/// header — a kubelet probe is unauthenticated, so a probe that 401'd would mark every pod perpetually unready.
/// /health/ready additionally runs the DbContext check (the test DB is up), proving readiness reflects real state.
///
/// <para>/health is the report on top of that surface — this process's build and role and every check's finding, in two tiers:
/// anyone gets the build, the role and each check's status; a signed-in global Admin also gets each check's description and
/// data — and the probes' semantics must not have moved: while /health says Degraded, /health/ready is still the plain-text 200 it was, so no
/// orchestrator restarts or unroutes a pod over a finding.</para>
///
/// <para>Tier: 🟢 High-fidelity — real app host + real Postgres + the real middleware pipeline (auth, fallback
/// policy, endpoint routing).</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class HealthEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public HealthEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Liveness_answers_200_anonymously()
    {
        // No Authorization header — a kubelet liveness probe is unauthenticated. AllowAnonymous must bypass the
        // global FallbackPolicy, and liveness carries no checks so it's 200 whenever the process is up.
        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            customMessage: "/health/live must be reachable WITHOUT auth (the FallbackPolicy must not apply) and 200 when the process is up");
    }

    [Fact]
    public async Task Readiness_answers_200_anonymously_when_the_database_is_reachable()
    {
        var response = await _factory.CreateClient().GetAsync("/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            customMessage: "/health/ready must be anonymous and 200 when the DbContext check passes (the test Postgres is up)");
    }

    [Fact]
    public async Task The_probes_answer_exactly_what_they_always_did()
    {
        // Additive proof: the new checks carry no "ready" tag and the probes' writers are the default plain-text one, so a
        // client that has always read these bodies reads the same bytes.
        var live = await _factory.CreateClient().GetAsync("/health/live");
        var ready = await _factory.CreateClient().GetAsync("/health/ready");

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await live.Content.ReadAsStringAsync()).ShouldBe("Healthy");
        live.Content.Headers.ContentType?.MediaType.ShouldBe("text/plain");
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ready.Content.ReadAsStringAsync()).ShouldBe("Healthy", "readiness runs only the \"ready\"-tagged checks — the database — whatever /health says");
        ready.Content.Headers.ContentType?.MediaType.ShouldBe("text/plain");
    }

    [Fact]
    public void Readiness_still_runs_only_the_database_check_and_the_report_runs_all_four()
    {
        // The structural half of "probes unchanged": which checks the probe predicates select. The three new checks must carry
        // no "ready" tag — one that did would let a git, lock or sweep finding pull a pod out of rotation.
        var registrations = _factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        registrations.Where(registration => registration.Tags.Contains("ready")).Select(registration => registration.Name).ShouldBe(new[] { "db" });
        registrations.Select(registration => registration.Name).OrderBy(name => name, StringComparer.Ordinal).ShouldBe(new[] { "credential-repair-sweep", "db", "git", "refresh-lock" });
    }

    [Fact]
    public async Task The_report_names_this_pods_build_and_role_and_each_checks_status_anonymously_and_nothing_more()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "a Degraded report is still a 200 — only an Unhealthy one is 503");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        var body = await response.Content.ReadAsStringAsync();
        var report = JsonSerializer.Deserialize<HealthResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldNotBeNull();

        report.Build.ShouldBe(BuildIdentity.Value, "the build /health names is the one every log line carries");
        report.Role.ShouldBe("Api", "this fixture pins HangfireHosting=Api");
        report.ProcessesJobs.ShouldBeFalse();
        report.DetailsIncluded.ShouldBeFalse("nobody signed in");
        report.Checks.Select(check => check.Name).ShouldBe(new[] { "credential-repair-sweep", "db", "git", "refresh-lock" });
        Entry(report, "db").Status.ShouldBe("Healthy");
        Entry(report, "credential-repair-sweep").Status.ShouldBe("Degraded", "an API-only fixture has no sweep — the status says so without saying why");
        report.Checks.ShouldAllBe(check => check.Description == null && check.Data.Count == 0, "the findings' text and facts are for a signed-in Admin");
        body.ShouldNotContain("strandedCandidates", Case.Sensitive, "an internal backlog count is not for an anonymous caller");
        body.ShouldNotContain("PgBouncer", Case.Sensitive, "nor is the connection topology");
        body.ShouldNotContain("git version", Case.Insensitive);
    }

    [Fact]
    public async Task A_signed_in_global_admin_also_gets_every_checks_description_and_data()
    {
        var report = await AdminReportAsync();

        report.DetailsIncluded.ShouldBeTrue();
        report.Build.ShouldBe(BuildIdentity.Value);
        report.Checks.Select(check => check.Name).ShouldBe(new[] { "credential-repair-sweep", "db", "git", "refresh-lock" });
        Entry(report, "refresh-lock").Description.ShouldNotBeNull();
        Entry(report, "refresh-lock").Data.ShouldContainKey("outcome");
    }

    [Fact]
    public async Task A_signed_in_account_that_is_not_a_global_admin_gets_the_anonymous_report()
    {
        var harness = new TenantHarness(_factory);
        var account = await harness.AccountAsync();

        var response = await harness.SendAsync(HttpMethod.Get, "/health", account, teamId: null);
        var report = (await response.Content.ReadFromJsonAsync<HealthResponse>()).ShouldNotBeNull();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        report.DetailsIncluded.ShouldBeFalse("being signed in is not enough; the instance Admin role is");
        report.Checks.ShouldAllBe(check => check.Description == null && check.Data.Count == 0);
    }

    [Fact]
    public async Task The_report_says_what_the_hosts_git_and_database_connections_can_do()
    {
        var report = await AdminReportAsync();

        var git = Entry(report, "git");
        var host = HostGit.Current;

        // Through the real reader: whatever git this machine has, the entry must agree with the floor the credential helper has.
        git.Status.ShouldBe(host.Version is { } version && version.IsAtLeast(TokenedGitCommand.MinimumGit) ? "Healthy" : "Degraded", git.Description);
        if (host.Version is { } installed) Fact(git, "version").GetString().ShouldBe(installed.ToString());

        var refreshLock = Entry(report, "refresh-lock");

        refreshLock.Status.ShouldBe("Healthy", refreshLock.Description);
        Fact(refreshLock, "outcome").GetString().ShouldBe("Exclusive", "the test Postgres is reached directly, so a second session is refused the held key and the holder's unlock lands on its own session");
    }

    [Fact]
    public async Task An_api_only_deployment_reports_the_repair_sweep_as_not_registered_without_touching_readiness()
    {
        // This fixture is the API role with no worker against its database: nothing registered the sweep, and /health says so.
        var report = await AdminReportAsync();

        var sweep = Entry(report, "credential-repair-sweep");

        sweep.Status.ShouldBe("Degraded");
        sweep.Description.ShouldNotBeNull().ShouldContain("not registered");
        Fact(sweep, "registered").GetBoolean().ShouldBeFalse();
        report.Status.ShouldBe("Degraded", "the worst check names the report's status");

        (await _factory.CreateClient().GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK, "a finding on /health must never pull a pod out of rotation");
    }

    /// <summary>The report as a signed-in global Admin reads it: the instance <c>Admin</c> role is a database row, so the account is seeded with it and sends its own token.</summary>
    private async Task<HealthResponse> AdminReportAsync()
    {
        var harness = new TenantHarness(_factory);
        var admin = await harness.AccountAsync();

        await harness.MakeGlobalAdminAsync(admin);

        var response = await harness.SendAsync(HttpMethod.Get, "/health", admin, teamId: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        return (await response.Content.ReadFromJsonAsync<HealthResponse>()).ShouldNotBeNull();
    }

    private static HealthCheckEntry Entry(HealthResponse report, string name) => report.Checks.Single(check => check.Name == name);

    private static JsonElement Fact(HealthCheckEntry entry, string key) => (JsonElement)entry.Data[key];
}

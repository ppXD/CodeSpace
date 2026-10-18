using System.Text.Json;
using CodeSpace.Core.Services.Health;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;

namespace CodeSpace.UnitTests.Health;

/// <summary>
/// The body of <c>GET /health</c>, in two tiers. The endpoint is anonymous — a probe cannot sign in, and an operator comparing an
/// API image with a worker image should not need to — so anyone gets the build, the role and each check's status. What a check
/// says about the deployment (its description, its data: the git release, the connection topology, the repair backlog) is
/// reconnaissance on an internet-facing API, and is shown to a signed-in global Admin only. A check that threw is reported by
/// the exception's type in either tier, never its message.
/// </summary>
[Trait("Category", "Unit")]
public sealed class HealthResponseWriterTests
{
    private static readonly HostPosture Worker = new(HangfireHosting.Worker, "1.0.0+abc123");

    [Fact]
    public async Task An_admin_gets_the_build_the_role_the_worst_status_and_every_checks_finding_in_full()
    {
        var report = Report(
            ("git", new HealthReportEntry(HealthStatus.Degraded, "git is old", TimeSpan.Zero, null, new Dictionary<string, object> { ["version"] = "2.30.2" })),
            ("db", new HealthReportEntry(HealthStatus.Healthy, null, TimeSpan.Zero, null, new Dictionary<string, object>())));

        var (context, body) = await WriteAsync(report, Worker, new StubUser(Roles.Admin));

        context.Response.ContentType.ShouldStartWith("application/json");
        body.GetProperty("status").GetString().ShouldBe("Degraded");
        body.GetProperty("build").GetString().ShouldBe("1.0.0+abc123");
        body.GetProperty("role").GetString().ShouldBe("Worker");
        body.GetProperty("processesJobs").GetBoolean().ShouldBeTrue();
        body.GetProperty("detailsIncluded").GetBoolean().ShouldBeTrue();

        var checks = body.GetProperty("checks").EnumerateArray().ToList();

        checks.Select(c => c.GetProperty("name").GetString()).ShouldBe(new[] { "db", "git" }, "ordered by name so two reports diff cleanly");
        checks[1].GetProperty("status").GetString().ShouldBe("Degraded");
        checks[1].GetProperty("description").GetString().ShouldBe("git is old");
        checks[1].GetProperty("data").GetProperty("version").GetString().ShouldBe("2.30.2");
    }

    [Fact]
    public async Task Anyone_else_gets_the_build_the_role_and_each_checks_status_but_no_description_and_no_data()
    {
        var report = Report(
            ("git", new HealthReportEntry(HealthStatus.Degraded, "git 2.30.2 is older than 2.31.0", TimeSpan.Zero, null, new Dictionary<string, object> { ["version"] = "2.30.2" })),
            ("credential-repair-sweep", new HealthReportEntry(HealthStatus.Degraded, "not registered", TimeSpan.Zero, null, new Dictionary<string, object> { ["strandedCandidates"] = 7 })));

        var (_, body) = await WriteAsync(report, Worker, user: null);

        body.GetProperty("status").GetString().ShouldBe("Degraded");
        body.GetProperty("build").GetString().ShouldBe("1.0.0+abc123", "comparing two images from outside is what the endpoint is for");
        body.GetProperty("role").GetString().ShouldBe("Worker");
        body.GetProperty("detailsIncluded").GetBoolean().ShouldBeFalse("so a reader who sees empty findings knows why");

        var checks = body.GetProperty("checks").EnumerateArray().ToList();

        checks.Select(c => c.GetProperty("name").GetString()).ShouldBe(new[] { "credential-repair-sweep", "git" });
        checks.Select(c => c.GetProperty("status").GetString()).ShouldBe(new[] { "Degraded", "Degraded" });
        checks.ShouldAllBe(c => c.GetProperty("description").ValueKind == JsonValueKind.Null && c.GetProperty("data").EnumerateObject().Count() == 0);

        var printed = body.GetRawText();

        printed.ShouldNotContain("2.30.2");
        printed.ShouldNotContain("strandedCandidates");
        printed.ShouldNotContain("not registered");
    }

    [Theory]
    [InlineData("Member")]
    [InlineData("Viewer")]
    public async Task A_signed_in_caller_who_is_not_a_global_admin_is_treated_as_anonymous(string role)
    {
        var report = Report(("git", new HealthReportEntry(HealthStatus.Degraded, "git is old", TimeSpan.Zero, null, new Dictionary<string, object> { ["version"] = "2.30.2" })));

        var (_, body) = await WriteAsync(report, Worker, new StubUser(role));

        body.GetProperty("detailsIncluded").GetBoolean().ShouldBeFalse();
        body.GetRawText().ShouldNotContain("git is old");
    }

    [Fact]
    public async Task A_caller_whose_roles_cannot_be_read_is_treated_as_anonymous_and_the_report_is_still_written()
    {
        // Roles come from the database; a report is most needed when it is down, and a signed-in caller must not turn that into a 500.
        var report = Report(("db", new HealthReportEntry(HealthStatus.Healthy, "up", TimeSpan.Zero, null, new Dictionary<string, object>())));

        var (_, body) = await WriteAsync(report, Worker, new StubUser(throws: true));

        body.GetProperty("status").GetString().ShouldBe("Healthy");
        body.GetProperty("detailsIncluded").GetBoolean().ShouldBeFalse();
        body.GetRawText().ShouldNotContain("up\"");
    }

    [Fact]
    public async Task A_host_with_no_current_user_registered_serves_the_anonymous_tier()
    {
        var report = Report(("git", new HealthReportEntry(HealthStatus.Degraded, "git is old", TimeSpan.Zero, null, new Dictionary<string, object>())));

        var (_, body) = await WriteAsync(report, Worker, user: null);

        body.GetProperty("detailsIncluded").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task An_api_role_says_it_processes_no_jobs()
    {
        var (_, body) = await WriteAsync(Report(), new HostPosture(HangfireHosting.Api, "1.0.0+abc123"), user: null);

        body.GetProperty("role").GetString().ShouldBe("Api");
        body.GetProperty("processesJobs").GetBoolean().ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_check_that_threw_is_reported_by_the_exception_type_and_never_its_message_in_either_tier(bool admin)
    {
        var thrown = new InvalidOperationException("Failed to connect to 10.20.30.40:5432 as user svc_codespace");
        var report = Report(("db", new HealthReportEntry(HealthStatus.Unhealthy, thrown.Message, TimeSpan.Zero, thrown, new Dictionary<string, object>())));

        var (_, body) = await WriteAsync(report, new HostPosture(HangfireHosting.Api, "1.0.0+abc123"), admin ? new StubUser(Roles.Admin) : null);

        var entry = body.GetProperty("checks")[0];

        entry.GetProperty("status").GetString().ShouldBe("Unhealthy");
        if (admin) entry.GetProperty("description").GetString().ShouldBe(nameof(InvalidOperationException));
        body.GetRawText().ShouldNotContain("10.20.30.40", customMessage: "an address or a user name from an exception message must never reach /health");
        body.GetRawText().ShouldNotContain("svc_codespace");
    }

    private static HealthReport Report(params (string Name, HealthReportEntry Entry)[] entries) =>
        new(entries.ToDictionary(e => e.Name, e => e.Entry), TimeSpan.Zero);

    private static async Task<(DefaultHttpContext Context, JsonElement Body)> WriteAsync(HealthReport report, HostPosture posture, ICurrentUser? user)
    {
        var services = new ServiceCollection().AddSingleton(posture);

        if (user is not null) services.AddSingleton(user);

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await HealthResponseWriter.WriteAsync(context, report);

        context.Response.Body.Position = 0;

        return (context, JsonDocument.Parse(context.Response.Body).RootElement.Clone());
    }

    /// <summary>The caller as the request pipeline resolves one: no roles at all for an anonymous request, the database's roles for a signed-in one, and a throw when that database is down.</summary>
    private sealed class StubUser(string? role = null, bool throws = false) : ICurrentUser
    {
        public Guid? Id => Guid.NewGuid();
        public string Name => "stub";
        public IReadOnlyList<string> Roles => throws ? throw new InvalidOperationException("role lookup failed: no connection to 10.20.30.40") : role is null ? Array.Empty<string>() : new[] { role };
        public IReadOnlyList<string> Permissions => Array.Empty<string>();
        public bool HasRole(string name) => Roles.Contains(name);
        public bool HasPermission(string permission) => false;
        public bool PasswordMustChange => false;
    }
}

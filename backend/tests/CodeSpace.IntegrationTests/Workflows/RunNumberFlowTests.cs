using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Team-scoped run numbers — the run's clean-URL handle (migration 0100). Every insert into
/// workflow_run is stamped by the <c>trg_workflow_run_number</c> BEFORE INSERT trigger, which claims
/// the next number from the per-team <c>team_run_counter</c> with a row-locked upsert. This proves:
/// numbers are dense per team, independent across teams, and — crucially — collision-free under the
/// PARALLEL run creates flow.map produces (a naive MAX+1 read would duplicate).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunNumberFlowTests
{
    private readonly PostgresFixture _fixture;
    public RunNumberFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Runs_get_sequential_per_team_numbers()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        var r1 = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        var r2 = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        var r3 = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        (await RunNumbersAsync(new[] { r1, r2, r3 })).ShouldBe(new long[] { 1, 2, 3 },
            customMessage: "a fresh team's runs number densely from 1 in creation order");
    }

    [Fact]
    public async Task Run_numbers_are_team_scoped()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var wfA = await CreateWorkflowAsync(teamA, userA);
        var wfB = await CreateWorkflowAsync(teamB, userB);

        var a1 = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, wfA, teamA);
        var a2 = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, wfA, teamA);
        var b1 = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, wfB, teamB);

        (await RunNumbersAsync(new[] { a1, a2 })).ShouldBe(new long[] { 1, 2 });
        (await RunNumbersAsync(new[] { b1 }))[0].ShouldBe(1,
            customMessage: "team B's counter is independent — its first run is #1, not a continuation of team A");
    }

    [Fact]
    public async Task Concurrent_run_creates_get_distinct_numbers()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        // Fire N seeds in parallel — each on its own DbContext/connection, exactly like flow.map's
        // concurrent fan-out. The trigger row-locks the counter, so the numbers come out distinct.
        const int n = 20;
        var runIds = await Task.WhenAll(Enumerable.Range(0, n)
            .Select(_ => WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId)));

        var numbers = await RunNumbersAsync(runIds);

        numbers.Distinct().Count().ShouldBe(n,
            customMessage: "every concurrent run must get a unique team run number — a duplicate means the allocator raced");
        numbers.OrderBy(x => x).ShouldBe(Enumerable.Range(1, n).Select(i => (long)i),
            customMessage: "and with no rollbacks they're the dense 1..N, no gaps");
    }

    [Fact]
    public async Task GetRunByRef_resolves_by_number_and_by_guid_and_exposes_the_number()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = verify.Resolve<IMediator>();

        var byNumber = await mediator.Send(new GetWorkflowRunByRefQuery { IdOrNumber = "1" });
        var byGuid = await mediator.Send(new GetWorkflowRunByRefQuery { IdOrNumber = runId.ToString() });

        byNumber.ShouldNotBeNull(customMessage: "the clean run URL resolves by the team-scoped run number");
        byNumber!.Id.ShouldBe(runId);
        byNumber.RunNumber.ShouldBe(1);
        byGuid.ShouldNotBeNull(customMessage: "a legacy GUID URL still resolves — the router redirects it to the number URL");
        byGuid!.Id.ShouldBe(runId);
        byGuid.RunNumber.ShouldBe(1);
    }

    [Fact]
    public async Task GetRunByRef_by_number_is_team_scoped()
    {
        // Team A run #1 and team B run #1 both exist; team B's by-number lookup MUST return B's run.
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runA = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, await CreateWorkflowAsync(teamA, userA), teamA);
        var runB = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, await CreateWorkflowAsync(teamB, userB), teamB);

        using var teamBScope = _fixture.BeginScopeAs(userB, teamB, Roles.Admin);
        var resolved = await teamBScope.Resolve<IMediator>().Send(new GetWorkflowRunByRefQuery { IdOrNumber = "1" });

        resolved.ShouldNotBeNull();
        resolved!.Id.ShouldBe(runB, customMessage: "team B's /runs/1 MUST resolve team B's run, not team A's #1");
        resolved.Id.ShouldNotBe(runA, "resolving another team's run through a shared number is a cross-team leak");
    }

    [Theory]
    [InlineData("999999")]   // a number with no run
    [InlineData("not-a-ref")] // neither a GUID nor a number
    [InlineData("")]
    public async Task GetRunByRef_returns_null_for_a_ref_that_matches_nothing(string idOrNumber)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        var result = await scope.Resolve<IMediator>().Send(new GetWorkflowRunByRefQuery { IdOrNumber = idOrNumber });

        result.ShouldBeNull(customMessage: "an unresolvable ref is a real miss — the router turns null into a 404");
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "run-number-" + Guid.NewGuid().ToString("N")[..8],
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
        });
    }

    private async Task<IReadOnlyList<long>> RunNumbersAsync(IReadOnlyList<Guid> runIds)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var map = await db.WorkflowRun.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.RunNumber);
        return runIds.Select(id => map[id]).ToList();
    }
}

using Autofac;
using CodeSpace.Core.Services.Plans;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Plans;
using Shouldly;

namespace CodeSpace.IntegrationTests.Plans;

/// <summary>
/// The work-plan store's semantics over REAL Postgres (the two unique indexes are the authority, so these
/// can only be proven against the real database): append-only per-run version sequencing, the origin-key
/// exactly-once contract (a crash-replayed producer lands on the EXISTING row), and team-scoped reads.
/// The run link is a soft reference, so a bare Guid stands in for the run — no engine needed at this tier.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkPlanServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkPlanServiceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Versions_are_appended_per_run_and_current_is_the_highest()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var plans = scope.Resolve<IWorkPlanService>();

        var v1 = await plans.SaveVersionAsync(Draft(teamId, runId, goal: "first plan"), CancellationToken.None);
        var v2 = await plans.SaveVersionAsync(Draft(teamId, runId, goal: "revised plan"), CancellationToken.None);

        v1.Version.ShouldBe(1);
        v2.Version.ShouldBe(2);
        v2.Id.ShouldNotBe(v1.Id, "a keyless save is always a NEW version (the plan.author edit-loop contract)");

        var current = await plans.GetCurrentAsync(runId, teamId, CancellationToken.None);
        current.ShouldNotBeNull();
        current!.Id.ShouldBe(v2.Id, "the highest version is the run's current plan");
        current.Goal.ShouldBe("revised plan");

        (await plans.ListVersionsAsync(runId, teamId, CancellationToken.None)).Select(p => p.Version).ShouldBe(new[] { 1, 2 }, "oldest first");
    }

    [Fact]
    public async Task The_same_origin_key_returns_the_existing_row_instead_of_a_new_version()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var plans = scope.Resolve<IWorkPlanService>();

        var first = await plans.SaveVersionAsync(Draft(teamId, runId, goal: "the plan", originKey: "sup#turn0"), CancellationToken.None);
        var replayed = await plans.SaveVersionAsync(Draft(teamId, runId, goal: "the plan", originKey: "sup#turn0"), CancellationToken.None);

        replayed.Id.ShouldBe(first.Id, "a crash-replayed producer must land on the EXISTING version — exactly-once per (run, origin key)");
        replayed.Version.ShouldBe(1);

        var later = await plans.SaveVersionAsync(Draft(teamId, runId, goal: "the re-plan", originKey: "sup#turn3"), CancellationToken.None);
        later.Version.ShouldBe(2, "a DIFFERENT origin key (a later re-plan turn) appends the run's next version");
    }

    [Fact]
    public async Task Reads_are_team_scoped()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var plans = scope.Resolve<IWorkPlanService>();

        await plans.SaveVersionAsync(Draft(teamId, runId, goal: "team-owned plan"), CancellationToken.None);

        (await plans.GetCurrentAsync(runId, foreignTeamId, CancellationToken.None)).ShouldBeNull("a foreign team must not read another team's plan");
        (await plans.ListVersionsAsync(runId, foreignTeamId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SetStatus_is_a_compare_and_swap_scoped_to_the_team()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var plans = scope.Resolve<IWorkPlanService>();

        var row = await plans.SaveVersionAsync(Draft(teamId, runId, goal: "the plan"), CancellationToken.None);

        (await plans.SetStatusAsync(row.Id, foreignTeamId, WorkPlanStatuses.Authored, WorkPlanStatuses.AwaitingConfirmation, CancellationToken.None))
            .ShouldBeFalse("a foreign team can never flip another team's plan");

        (await plans.SetStatusAsync(row.Id, teamId, WorkPlanStatuses.AwaitingConfirmation, WorkPlanStatuses.Confirmed, CancellationToken.None))
            .ShouldBeFalse("a wrong FROM status no-ops — the CAS is the double-transition guard");

        (await plans.SetStatusAsync(row.Id, teamId, WorkPlanStatuses.Authored, WorkPlanStatuses.AwaitingConfirmation, CancellationToken.None))
            .ShouldBeTrue();

        (await plans.SetStatusAsync(row.Id, teamId, WorkPlanStatuses.Authored, WorkPlanStatuses.AwaitingConfirmation, CancellationToken.None))
            .ShouldBeFalse("a crash-replayed flip is a no-op, never a second transition");

        (await plans.GetCurrentAsync(runId, teamId, CancellationToken.None))!.Status
            .ShouldBe(WorkPlanStatuses.AwaitingConfirmation, "the one successful CAS is the row's status");
    }

    private static WorkPlanDraft Draft(Guid teamId, Guid runId, string goal, string? originKey = null) => new()
    {
        TeamId = teamId,
        WorkflowRunId = runId,
        OriginKind = WorkPlanOrigins.Supervisor,
        OriginKey = originKey,
        Goal = goal,
        Items = new[]
        {
            new WorkPlanItem { Id = "a", Title = "Alpha", Instruction = "do alpha" },
        },
    };
}

using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="WorkflowEngine"/> + the REAL
/// <see cref="SupervisorTurnService"/>; the scripted decider stands in for the LLM): A1 wire honesty. A supervisor
/// run that GAVE UP still lands <see cref="WorkflowRunStatus.Success"/> on the wire by design — the graph did
/// finish — so every list rendered it identically to a clean solve. The run now carries the honest terminal WORD
/// beside its status, stamped at the engine's single terminal write from the tape's own last stop decision, and
/// the runs-list projection carries it to the client.
///
/// <para>The fences that make this trustworthy rather than decorative: a CLEAN stop must read Succeeded (the word
/// cannot simply mean "supervisor"), a non-supervisor run must read NULL (absence means "the status is already
/// honest", never a verdict), and the status itself must be untouched (every existing status filter, terminal
/// predicate and cockpit zone partition keeps working).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RunOutcomeHonestyFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;

    public RunOutcomeHonestyFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task A_give_up_run_lands_Success_but_records_the_honest_outcome()
    {
        using (var scope = _fixture.BeginScope()) scope.Resolve<SupervisorDecisionScript>().PlanThenGiveUp();

        var runId = await DriveSupervisorRunToTerminalAsync();

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success, "the engine's terminal semantics are UNCHANGED — the graph finished, and status filters must keep working");
        run.Outcome.ShouldBe(nameof(SupervisorStopKind.GaveUp), "…but the run now says how the work actually ended, which is the thing a list was rendering as a clean solve");
    }

    [Fact]
    public async Task A_clean_run_records_a_succeeded_outcome()
    {
        // The discriminating fence: if the word merely meant "this was a supervisor run" it would be useless. A
        // genuine success must be distinguishable from a give-up by the word alone.
        using (var scope = _fixture.BeginScope()) scope.Resolve<SupervisorDecisionScript>().PlanThenStop();

        var runId = await DriveSupervisorRunToTerminalAsync();

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success);
        run.Outcome.ShouldBe(nameof(SupervisorStopKind.Succeeded));
    }

    [Fact]
    public async Task The_honest_outcome_reaches_the_runs_list_projection()
    {
        using (var scope = _fixture.BeginScope()) scope.Resolve<SupervisorDecisionScript>().PlanThenGiveUp();

        var runId = await DriveSupervisorRunToTerminalAsync();

        using var verify = _fixture.BeginScope();
        var teamId = (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).TeamId;
        var page = await verify.Resolve<IWorkflowService>().ListTeamRunsAsync(teamId, new RunListFilter(), cursor: null, limit: 50, CancellationToken.None);
        var summary = page.Items.Single(r => r.Id == runId);

        summary.Status.ShouldBe(WorkflowRunStatus.Success);
        summary.Outcome.ShouldBe(nameof(SupervisorStopKind.GaveUp), "the word must reach the surface that was lying — a column no list reads is not an honesty fix");
    }

    [Fact]
    public async Task A_non_supervisor_run_records_no_outcome_word()
    {
        // Absence is load-bearing: it means "this run's status is already honest", NOT "unknown verdict". A reader
        // that treated null as a degradation would slander every ordinary workflow run.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateMinimalWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success);
        run.Outcome.ShouldBeNull("no supervisor tape to classify — the status word is the whole truth for this run");
    }

    // ── Chassis ──────────────────────────────────────────────────────────────────────

    /// <summary>Turn 0 plans (synchronous, parks on a self-advance wait); resolving that wait drives the terminal stop turn.</summary>
    private async Task<Guid> DriveSupervisorRunToTerminalAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

        Guid waitId;
        using (var verify = _fixture.BeginScope())
        {
            waitId = (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision && w.Status == WorkflowWaitStatuses.Pending)).Id;
        }

        // Resolving the wait ENQUEUES the re-dispatch (the post-commit hand-off); drive the engine again to run
        // the terminal stop turn — the same two-step every supervisor flow test uses.
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

        return runId;
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "outcome-honesty-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Label = "Start", Config = Empty(), Inputs = Empty() },
                    new() { Id = "sup", TypeKey = "agent.supervisor", Label = "Supervisor", Config = Empty(), Inputs = Empty() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Label = "Done", Config = Empty(), Inputs = Empty() },
                },
                Edges = new List<EdgeDefinition> { new() { From = "start", To = "sup" }, new() { From = "sup", To = "end" } },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> CreateMinimalWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "outcome-honesty-plain-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private static System.Text.Json.JsonElement Empty() => System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone();
}

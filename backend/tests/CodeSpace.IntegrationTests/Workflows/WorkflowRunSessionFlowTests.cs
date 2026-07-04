using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration — EVERY workflow run belongs to a work session so its run-detail page renders the Session (the
/// Room/Journal transcript) instead of the raw-trace modal. The session is resolved GENERICALLY at the run-staging seam
/// (<see cref="IWorkSessionService.ResolveForRunAsync"/>, called by <see cref="IRunStarter"/>), so a source that supplies
/// no session (scheduled, webhook, child) still gets one and no new source can forget; a source that DOES supply one (a
/// task continuation, an inherited fork) keeps it. Covers the end-to-end manual command + the seam directly.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowRunSessionFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunSessionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_manually_run_workflow_reaches_its_room_because_the_seam_opened_a_session()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        Guid runId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            runId = await scope.Resolve<MediatR.IMediator>().Send(new RunWorkflowManuallyCommand { WorkflowId = workflowId });

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.SessionId.ShouldNotBeNull("a manual workflow run must belong to a work session, so its detail renders the Session — not the raw-trace modal (the reported bug)");
        run.SessionTurnIndex.ShouldBe(1, "the run is the opening turn of its own session");

        var session = await db.WorkSession.AsNoTracking().SingleAsync(s => s.Id == run.SessionId!.Value);
        session.TeamId.ShouldBe(teamId, "the session belongs to the run's team");
        session.Kind.ShouldBe(WorkSessionKind.Workflow, "a workflow run's thread is a Workflow-kind session");

        // The bug's real symptom: the run must now RESOLVE to a room (session-backed), not 404 into the raw-trace modal.
        (await verify.Resolve<IRoomProjector>().ProjectByRunAsync(runId, teamId, CancellationToken.None))
            .ShouldNotBeNull("the run projects to its Session now that it has one — the FE lands on the Room/Journal, not the trace modal");
    }

    [Fact]
    public async Task A_session_less_source_still_gets_a_workflow_session_at_the_staging_seam()
    {
        // The generic guarantee, exercised on a source that supplies NO session (a scheduled run — ScheduleTriggerService
        // hands the starter no Session). Every trigger flows through IRunStarter, so this one assertion covers scheduled /
        // webhook / child alike: the seam opens a fresh Workflow-kind session, so none of them can be session-less.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = await scope.Resolve<IRunStarter>().StartAsync(new RunSourceEnvelope
            {
                TeamId = teamId,
                WorkflowId = workflowId,
                WorkflowVersion = 1,
                SourceType = WorkflowRunSourceTypes.ScheduleCron,
                ActorType = WorkflowRunActorTypes.System,
                ActorId = SystemUsers.SeederId,
                NormalizedPayloadJson = "{}",
                CreatedBy = SystemUsers.SeederId,
            }, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.SessionId.ShouldNotBeNull("a run whose source supplies no session must still get one at the staging seam — so scheduled/webhook/child runs reach the Journal and no source can forget");

        var session = await db.WorkSession.AsNoTracking().SingleAsync(s => s.Id == run.SessionId!.Value);
        session.Kind.ShouldBe(WorkSessionKind.Workflow);
        session.TeamId.ShouldBe(teamId);
    }

    [Fact]
    public async Task An_explicitly_provided_session_is_kept_not_replaced()
    {
        // A source that DID resolve a session (a task continuation, an inherited fork) must keep it — the seam is a
        // default, not an override. Open a session, hand it to the starter, and assert the run rides it with NO second
        // session minted.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        Guid runId;
        Guid providedSessionId;
        using (var scope = _fixture.BeginScope())
        {
            var provided = await scope.Resolve<IWorkSessionService>().OpenAsync(teamId, "existing thread", WorkSessionKind.Task, userId, CancellationToken.None);
            providedSessionId = provided.SessionId;

            runId = await scope.Resolve<IRunStarter>().StartAsync(new RunSourceEnvelope
            {
                TeamId = teamId,
                WorkflowId = workflowId,
                WorkflowVersion = 1,
                SourceType = WorkflowRunSourceTypes.Manual,
                ActorType = WorkflowRunActorTypes.User,
                ActorId = userId,
                NormalizedPayloadJson = "{}",
                Session = provided,
                CreatedBy = userId,
            }, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).SessionId
            .ShouldBe(providedSessionId, "the run rides the provided session, not a freshly-opened one");
        (await db.WorkSession.AsNoTracking().CountAsync(s => s.TeamId == teamId))
            .ShouldBe(1, "no second session was minted — the seam passed the provided one through");
        (await db.WorkSession.AsNoTracking().SingleAsync(s => s.Id == providedSessionId)).Kind
            .ShouldBe(WorkSessionKind.Task, "and it's the caller's own Task-kind session, not overwritten to Workflow");
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "run-session-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}

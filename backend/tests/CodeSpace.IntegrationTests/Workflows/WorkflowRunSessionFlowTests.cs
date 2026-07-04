using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Sessions.Room;
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
/// 🟢 Integration — a manually-run workflow must OPEN a work session so its run-detail page renders the Session (the
/// Room/Journal transcript) instead of falling back to the raw-trace modal. Without a session the run's SessionId is null,
/// the room projector 404s, and the FE opens the raw trace. Drives the REAL <see cref="RunWorkflowManuallyCommand"/>
/// through the mediator (TransactionalBehavior wraps it, so the post-commit dispatch DEFERS to the drain — a missing
/// Hangfire worker in the test can't fail the command) and asserts the run is session-backed end to end.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowRunSessionFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunSessionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_manually_run_workflow_opens_a_workflow_session_so_the_run_reaches_its_room()
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
        session.Kind.ShouldBe(WorkSessionKind.Workflow, "a manual workflow run's thread is a Workflow-kind session (the enum kind reserved for exactly this)");

        // The bug's real symptom: the run must now RESOLVE to a room (session-backed), not 404 into the raw-trace modal.
        (await verify.Resolve<IRoomProjector>().ProjectByRunAsync(runId, teamId, CancellationToken.None))
            .ShouldNotBeNull("the run projects to its Session now that it has one — the FE lands on the Room/Journal, not the trace modal");
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "manual-run-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}

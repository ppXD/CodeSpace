using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// <see cref="ISupervisorTurnService.ReopenDiscardedAskAsync"/> against real Postgres — the supervisor's re-entry half of
/// Continue after a stop. Only this node's LATEST ask wait is ever re-opened, and only when a stop discarded it: an
/// older discarded ask is never the open question, an answered or still-pending one has nothing to re-open, and the
/// node's other discarded waits (its agent barrier, its self-advance) stay closed. The end-to-end cancel → continue →
/// answer path is <see cref="SupervisorAskHumanFlowTests"/>; this pins the selection rule the path relies on.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorReopenDiscardedAskFlowTests
{
    private const string NodeId = "sup";

    private readonly PostgresFixture _fixture;

    public SupervisorReopenDiscardedAskFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(WorkflowWaitStatuses.Discarded, true)]   // a stop closed the open question → re-opened
    [InlineData(WorkflowWaitStatuses.Resolved, false)]   // the human answered it → nothing to re-open
    [InlineData(WorkflowWaitStatuses.Pending, false)]    // still parked → nothing to re-open
    public async Task Only_the_latest_ask_is_reopened_and_only_when_a_stop_discarded_it(string latestAskStatus, bool expectReopened)
    {
        var runId = await SeedRunAsync();
        var olderAsk = await SeedWaitAsync(runId, SupervisorOutcome.HumanWaitKey(NodeId, 0), WorkflowWaitKinds.Action, WorkflowWaitStatuses.Discarded, createdAgo: TimeSpan.FromMinutes(3));
        var agentBarrier = await SeedWaitAsync(runId, $"{NodeId}#turn1#0", WorkflowWaitKinds.AgentRun, WorkflowWaitStatuses.Discarded, createdAgo: TimeSpan.FromMinutes(2));
        var selfAdvance = await SeedWaitAsync(runId, SupervisorOutcome.SelfAdvanceWaitKey(NodeId, 2), WorkflowWaitKinds.SupervisorDecision, WorkflowWaitStatuses.Discarded, createdAgo: TimeSpan.FromMinutes(1));
        var latestAsk = await SeedWaitAsync(runId, SupervisorOutcome.HumanWaitKey(NodeId, 3), WorkflowWaitKinds.Action, latestAskStatus, createdAgo: TimeSpan.Zero);

        bool reopened;
        using (var scope = _fixture.BeginScope())
            reopened = await scope.Resolve<ISupervisorTurnService>().ReopenDiscardedAskAsync(runId, NodeId, CancellationToken.None);

        reopened.ShouldBe(expectReopened);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var latest = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == latestAsk.Id);
        latest.Status.ShouldBe(expectReopened ? WorkflowWaitStatuses.Pending : latestAskStatus);
        latest.Token.ShouldBe(latestAsk.Token, "the SAME row re-opens, so the card already posted still answers it");
        if (expectReopened) latest.ResolvedAt.ShouldBeNull("a re-opened wait is pending again, not resolved");

        foreach (var closed in new[] { olderAsk, agentBarrier, selfAdvance })
            (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == closed.Id)).Status
                .ShouldBe(WorkflowWaitStatuses.Discarded, $"{closed.IterationKey} is not the open question and stays closed");
    }

    private async Task<WorkflowRunWait> SeedWaitAsync(Guid runId, string iterationKey, string waitKind, string status, TimeSpan createdAgo)
    {
        var wait = new WorkflowRunWait
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = NodeId,
            IterationKey = iterationKey,
            WaitKind = waitKind,
            Token = Guid.NewGuid().ToString("N"),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow - createdAgo,
            ResolvedAt = status == WorkflowWaitStatuses.Pending ? null : DateTimeOffset.UtcNow,
        };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunWait.Add(wait);
        await db.SaveChangesAsync();

        return wait;
    }

    private async Task<Guid> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "reopen-ask-" + Guid.NewGuid().ToString("N")[..6],
                Description = null,
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }
}

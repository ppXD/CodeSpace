using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Decisions;

/// <summary>
/// 🟢 Integration (high fidelity, Rule 12): the cross-grain "Needs decision" queue (Decision substrate D3) over the
/// REAL <see cref="DecisionQueueService"/> + real Postgres. The queue UNIFIES BOTH park backends without special-casing
/// either — an agent.code <c>decision.request</c> (a parked tool-ledger row with its stashed envelope) and a
/// <c>flow.decision</c> node (a Pending workflow-run wait with its stashed envelope) — team-scoped. Pins: both grains
/// appear, projected from the envelope; a foreign team's decisions never leak; a resolved/answered decision is excluded;
/// ordering is soonest-deadline first.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DecisionQueueFlowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _fixture;

    public DecisionQueueFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_queue_unifies_pending_agent_and_node_decisions_team_scoped_and_ordered()
    {
        var teamId = await SeedTeamAsync();
        var otherTeam = await SeedTeamAsync();

        var now = DateTimeOffset.UtcNow;

        // Two pending decisions, one per grain — the node one is more urgent (sooner deadline).
        var agentId = await SeedAgentDecisionAsync(teamId, "Agent: deploy to prod?", now.AddMinutes(5), ToolCallLedgerStatus.AwaitingApproval);
        var nodeId = await SeedNodeDecisionAsync(teamId, "Node: which migration path?", now.AddMinutes(2));

        // Noise that must NOT appear: a foreign team's decision, and an already-answered (Succeeded) one.
        await SeedAgentDecisionAsync(otherTeam, "Foreign team decision", now.AddMinutes(1), ToolCallLedgerStatus.AwaitingApproval);
        await SeedAgentDecisionAsync(teamId, "Already answered", now.AddMinutes(10), ToolCallLedgerStatus.Succeeded);

        var queue = await ListPendingAsync(teamId);

        queue.Count.ShouldBe(2, "only THIS team's PENDING decisions, across BOTH grains — the foreign + answered ones are excluded");
        queue.Select(d => d.Question).ShouldBe(new[] { "Node: which migration path?", "Agent: deploy to prod?" }, "soonest-deadline first");

        var node = queue.Single(d => d.Grain == DecisionResumeBackends.WorkflowWait);
        node.Id.ShouldBe(nodeId, "the node-grain queue handle is the wait id");
        node.NodeId.ShouldBe("decide");

        var agent = queue.Single(d => d.Grain == DecisionResumeBackends.ToolLedger);
        agent.Id.ShouldBe(agentId, "the agent-grain queue handle is the ledger id");
        agent.RiskLevel.ShouldBe(DecisionRiskLevels.High);
        agent.Policy.ShouldBe(DecisionPolicies.HumanRequired);
    }

    [Fact]
    public async Task A_resolved_node_wait_is_excluded()
    {
        var teamId = await SeedTeamAsync();

        await SeedNodeDecisionAsync(teamId, "Pending node", DateTimeOffset.UtcNow.AddMinutes(2));
        await SeedNodeDecisionAsync(teamId, "Resolved node", DateTimeOffset.UtcNow.AddMinutes(1), resolved: true);

        var queue = await ListPendingAsync(teamId);

        queue.ShouldHaveSingleItem().Question.ShouldBe("Pending node", "a Resolved Decision wait carries the ANSWER, not a pending question — it must not appear");
    }

    [Fact]
    public async Task ListPendingForAgentRuns_returns_only_the_named_runs_pending_decisions()
    {
        // D4b: the supervisor arbiter reads the pending decisions of ITS children (a set of agent runs) — not the
        // whole team's queue, not a sibling run's, not the already-answered ones.
        var teamId = await SeedTeamAsync();
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var dA = await SeedAgentDecisionAsync(teamId, "A pending", now.AddMinutes(2), ToolCallLedgerStatus.AwaitingApproval, runA);
        await SeedAgentDecisionAsync(teamId, "B pending", now.AddMinutes(1), ToolCallLedgerStatus.AwaitingApproval, runB);   // a sibling run → excluded
        await SeedAgentDecisionAsync(teamId, "A answered", now.AddMinutes(3), ToolCallLedgerStatus.Succeeded, runA);          // resolved → excluded

        using var scope = _fixture.BeginScope();
        var pending = await scope.Resolve<IDecisionQueueService>().ListPendingForAgentRunsAsync(new[] { runA }, teamId, CancellationToken.None);

        pending.ShouldHaveSingleItem().Id.ShouldBe(dA, "only run A's PENDING decision");

        (await scope.Resolve<IDecisionQueueService>().ListPendingForAgentRunsAsync(Array.Empty<Guid>(), teamId, CancellationToken.None))
            .ShouldBeEmpty("an empty run set yields nothing");
    }

    // ─── Drive the real service ───────────────────────────────────────────────────

    private async Task<IReadOnlyList<Messages.Dtos.Decisions.PendingDecision>> ListPendingAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IDecisionQueueService>().ListPendingAsync(teamId, CancellationToken.None);
    }

    // ─── Seeding ──────────────────────────────────────────────────────────────────

    private static DecisionRequest Envelope(string question, DateTimeOffset deadline, string grain, string? nodeId, Guid? agentRunId, Guid? workflowRunId) => new()
    {
        Id = Guid.NewGuid(),
        RootTraceId = Guid.NewGuid(),
        AgentRunId = agentRunId,
        WorkflowRunId = workflowRunId,
        NodeId = nodeId,
        Scope = grain == DecisionResumeBackends.ToolLedger ? DecisionScopes.Agent : DecisionScopes.Node,
        RequesterType = grain == DecisionResumeBackends.ToolLedger ? DecisionRequesterTypes.Agent : DecisionRequesterTypes.WorkflowNode,
        DecisionType = DecisionTypes.ChooseOne,
        Question = question,
        Options = new[] { new DecisionOption { Id = "a", Label = "A" }, new DecisionOption { Id = "b", Label = "B" } },
        RecommendedOption = "a",
        BlockingReason = "needs a human",
        RiskLevel = DecisionRiskLevels.High,
        Policy = DecisionPolicies.HumanRequired,
        TimeoutAt = deadline,
        DedupeKey = Guid.NewGuid().ToString("N"),
        ResumeBackend = grain,
    };

    private async Task<Guid> SeedAgentDecisionAsync(Guid teamId, string question, DateTimeOffset deadline, ToolCallLedgerStatus status, Guid? agentRunId = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var ledgerId = Guid.NewGuid();
        var runId = agentRunId ?? Guid.NewGuid();
        var envelope = Envelope(question, deadline, DecisionResumeBackends.ToolLedger, nodeId: null, agentRunId: runId, workflowRunId: null);

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = ledgerId,
            TeamId = teamId,
            AgentRunId = runId,
            ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision.request:{ledgerId:N}",
            InputHash = new string('0', 64),
            Status = status,
            ApprovalDeadlineAt = deadline,
            DecisionEnvelopeJson = JsonSerializer.Serialize(envelope, Json),
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return ledgerId;
    }

    private async Task<Guid> SeedNodeDecisionAsync(Guid teamId, string question, DateTimeOffset deadline, bool resolved = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var waitId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // A snapshot run (WorkflowId null → the optional FK is skipped) wired only to its required run-request, so the
        // queue's WorkflowRun join (run.TeamId == teamId) has a real row without standing up a full workflow.
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId,
            TeamId = teamId,
            WorkflowId = null,
            SourceType = WorkflowRunSourceTypes.Manual,
            ActorType = "user",
            ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now,
            VerifiedAt = now,
            NormalizedAt = now,
        });

        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId,
            WorkflowId = null,
            WorkflowVersion = 1,
            TeamId = teamId,
            RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Manual,
            Status = WorkflowRunStatus.Suspended,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        // Commit the run before the wait: WorkflowRunWait.RunId is a DB-level FK NOT modelled as an EF navigation, so EF
        // won't order the two inserts in one batch — a single SaveChanges trips workflow_run_wait_run_id_fkey.
        await db.SaveChangesAsync();

        var envelope = Envelope(question, deadline, DecisionResumeBackends.WorkflowWait, nodeId: "decide", agentRunId: null, workflowRunId: runId);

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = waitId,
            RunId = runId,
            NodeId = "decide",
            IterationKey = string.Empty,
            WaitKind = WorkflowWaitKinds.Decision,
            Token = Guid.NewGuid().ToString("N"),
            WakeAt = deadline,
            Status = resolved ? WorkflowWaitStatuses.Resolved : WorkflowWaitStatuses.Pending,
            // While Pending this holds the request envelope (the stash); a resolved wait would carry the answer instead.
            PayloadJson = JsonSerializer.Serialize(envelope, Json),
            CreatedAt = now,
            ResolvedAt = resolved ? now : null,
        });

        await db.SaveChangesAsync();
        return waitId;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var ownerId = Guid.NewGuid();
        db.User.Add(new User { Id = ownerId, Email = $"dq-{ownerId:N}@test.local", Name = $"dq-{ownerId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"dq-{teamId:N}", Name = "Decision Queue Team", Kind = TeamKind.Workspace, OwnerUserId = ownerId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = ownerId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}

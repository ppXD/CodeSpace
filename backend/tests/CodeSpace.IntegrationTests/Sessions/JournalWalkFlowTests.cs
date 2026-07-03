using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="IJournalWalk"/> resolved from DI, over the REAL timeline
/// projector + describer registry): the chronological journal end-to-end. A run's supervisor decisions and its agents'
/// events walk into journal steps in ONE chronological order (the timeline spine's merge), each Seq-assigned; a foreign
/// run conflates to null. Proves the whole chain — ledgers → timeline sources → merge → describe → cursor — composes.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class JournalWalkFlowTests
{
    private readonly PostgresFixture _fixture;

    public JournalWalkFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Walks_a_runs_decisions_and_agent_events_into_chronological_steps_with_a_monotonic_cursor()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var agentId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentId, "code");

        var t = DateTimeOffset.UtcNow;
        await SeedDecisionAsync(runId, teamId, SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, t);
        await SeedAgentEventAsync(agentId, AgentEventKind.FileChanged, "edited auth/session.ts", t.AddSeconds(1));
        await SeedToolCallAsync(teamId, agentId, "git.open_pr", t.AddSeconds(2));
        await SeedDecisionAsync(runId, teamId, SupervisorDecisionKinds.Stop, SupervisorDecisionStatus.Succeeded, t.AddSeconds(3));

        var steps = await WalkAsync(userId, teamId, runId);

        steps.ShouldNotBeNull();
        steps!.Count.ShouldBe(4, "the two decisions + the agent event + the tool call all walk into steps");

        // Chronological, interleaved across ALL four sources by the spine's merge: plan → agent edit → tool → stop.
        steps.Select(s => s.Kind).ShouldBe(new[] { JournalStepKinds.Decision, JournalStepKinds.Agent, JournalStepKinds.Tool, JournalStepKinds.Decision });
        steps.Select(s => s.Title).ShouldBe(new[] { "Supervisor planned the work", "edited auth/session.ts", "Called git.open_pr", "Supervisor stopped" });
        steps.Select(s => s.Cursor).ShouldAllBe(c => c.Length > 0, "every step carries a cursor");
        steps.Select(s => s.Cursor).Distinct().Count().ShouldBe(4, "each step across the merged spine gets a distinct cursor");
        steps.Single(s => s.Kind == JournalStepKinds.Agent).AgentRunId.ShouldBe(agentId.ToString(), "the agent step carries its provenance");
    }

    [Fact]
    public async Task A_foreign_run_walks_to_null()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        (await WalkAsync(userId, teamId, Guid.NewGuid())).ShouldBeNull("a run that isn't the team's conflates to null — no existence leak");
    }

    private async Task<IReadOnlyList<JournalStep>?> WalkAsync(Guid userId, Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IJournalWalk>().WalkAsync(runId, teamId, CancellationToken.None);
    }

    private async Task SeedDecisionAsync(Guid runId, Guid teamId, string kind, SupervisorDecisionStatus status, DateTimeOffset at)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = kind, Status = status, PayloadJson = "{}", OutcomeJson = null,
            IdempotencyKey = Guid.NewGuid().ToString("N"), InputHash = "test",
            CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedDate = at, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedAgentRunAsync(Guid runId, Guid teamId, Guid agentRunId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, NodeId = nodeId, IterationKey = nodeId,
            Harness = "codex-cli", Status = AgentRunStatus.Succeeded, TaskJson = "{}",
            CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedAgentEventAsync(Guid agentRunId, AgentEventKind kind, string text, DateTimeOffset at)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRunEvent.Add(new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = agentRunId, Kind = kind, Text = text, OccurredAt = at });

        await db.SaveChangesAsync();
    }

    private async Task SeedToolCallAsync(Guid teamId, Guid agentRunId, string toolKind, DateTimeOffset at)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = Guid.NewGuid(), TeamId = teamId, AgentRunId = agentRunId,
            ToolKind = toolKind, IdempotencyKey = Guid.NewGuid().ToString("N"), InputHash = "test",
            Status = ToolCallLedgerStatus.Succeeded,
            CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedDate = at, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Success,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }
}

using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks.Timeline;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="RunRecordTimelineSource"/> resolved from DI): the narrative
/// timeline end-to-end at the projector seam — a run's append-only <c>workflow_run_record</c> ledger surfaces through
/// the run-timeline projector as the NARRATIVE-worthy lifecycle events, chronologically merged, with Trace-level
/// noise (log lines) dropped. A foreign / absent run resolves to null (team-scoped, fail-closed).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RunTimelineProjectionFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunTimelineProjectionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Projects_the_run_lifecycle_records_chronologically_dropping_trace_noise()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);

        var t = DateTimeOffset.UtcNow;
        await SeedRecordsAsync(runId,
            (WorkflowRunRecordTypes.RunStarted, null, "{}", t),
            (WorkflowRunRecordTypes.NodeStarted, "code", "{}", t.AddSeconds(1)),
            (WorkflowRunRecordTypes.Log, "code", """{"level":"info","message":"noise"}""", t.AddSeconds(2)),
            (WorkflowRunRecordTypes.NodeFailed, "code", """{"error":"boom"}""", t.AddSeconds(3)),
            (WorkflowRunRecordTypes.RunFailed, null, """{"error":"boom"}""", t.AddSeconds(4)));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        events!.Select(e => e.Title).ShouldBe(new[] { "Run started", "code started", "code failed", "Run failed" },
            "the lifecycle records project chronologically; the log line is Trace-level noise and is dropped");

        var failed = events.Single(e => e.Title == "code failed");
        failed.Severity.ShouldBe(TimelineSeverity.Error);
        failed.Summary.ShouldBe("boom");
        failed.NodeId.ShouldBe("code");
    }

    [Fact]
    public async Task A_foreign_run_resolves_to_null()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var events = await ProjectAsync(userId, teamId, Guid.NewGuid());

        events.ShouldBeNull("a run that isn't the team's resolves to null — 404-conflate, no existence leak");
    }

    [Fact]
    public async Task Projects_an_agents_narrative_events_tagged_with_the_agent_dropping_chatter()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var agentId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentId, "code");

        var t = DateTimeOffset.UtcNow;
        await SeedAgentEventsAsync(agentId,
            (AgentEventKind.AssistantMessage, "thinking out loud", t),               // chatter → dropped
            (AgentEventKind.FileChanged, "edited auth/session.ts", t.AddSeconds(1)),
            (AgentEventKind.Reasoning, "considering options", t.AddSeconds(2)),       // chatter → dropped
            (AgentEventKind.Error, "2 tests failing", t.AddSeconds(3)),
            (AgentEventKind.FinalSummary, "done — fixed the session bug", t.AddSeconds(4)));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        var agentEvents = events!.Where(e => e.SourceKey == "agent-events").ToList();
        agentEvents.Select(e => e.Title).ShouldBe(new[] { "edited auth/session.ts", "2 tests failing", "done — fixed the session bug" },
            "only the narrative kinds surface, chronologically; the assistant/reasoning chatter is dropped");
        agentEvents.ShouldAllBe(e => e.AgentRunId == agentId.ToString());
        agentEvents.ShouldAllBe(e => e.NodeId == "code");
        agentEvents.Single(e => e.Title == "2 tests failing").Severity.ShouldBe(TimelineSeverity.Error);
    }

    [Fact]
    public async Task Excludes_an_agent_stamped_to_another_team()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);

        // A crafted cross-tenant row: an AgentRun pointing at this run id but stamped to ANOTHER team. The team-scoped
        // agent read (defense in depth on top of the run precheck) must keep its events off this run's timeline.
        var foreignAgent = Guid.NewGuid();
        await SeedAgentRunAsync(runId, otherTeamId, foreignAgent, "code");
        await SeedAgentEventsAsync(foreignAgent, (AgentEventKind.FileChanged, "leak", DateTimeOffset.UtcNow));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        events!.ShouldNotContain(e => e.SourceKey == "agent-events", "an agent stamped to another team is filtered by the team-scoped agent read");
    }

    [Fact]
    public async Task An_agentless_run_contributes_only_lifecycle_no_agent_events()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);

        var t = DateTimeOffset.UtcNow;
        await SeedRecordsAsync(runId,
            (WorkflowRunRecordTypes.RunStarted, null, "{}", t),
            (WorkflowRunRecordTypes.RunCompleted, null, "{}", t.AddSeconds(1)));
        // NO AgentRun rows — a plain structural workflow.

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        events!.ShouldNotBeEmpty();
        events.ShouldAllBe(e => e.SourceKey == "run-record", "an agentless run's timeline is pure lifecycle — the agent-events source contributes nothing");
    }

    [Fact]
    public async Task A_multi_agent_run_stamps_each_event_with_its_own_agent_and_node()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var backend = Guid.NewGuid();
        var frontend = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, backend, "backend-fix");
        await SeedAgentRunAsync(runId, teamId, frontend, "frontend-fix");

        var t = DateTimeOffset.UtcNow;
        await SeedAgentEventsAsync(backend, (AgentEventKind.FileChanged, "edited api.cs", t));
        await SeedAgentEventsAsync(frontend, (AgentEventKind.FileChanged, "edited app.tsx", t.AddSeconds(1)));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        var byTitle = events!.Where(e => e.SourceKey == "agent-events").ToDictionary(e => e.Title);
        byTitle["edited api.cs"].AgentRunId.ShouldBe(backend.ToString());
        byTitle["edited api.cs"].NodeId.ShouldBe("backend-fix", "each agent's event carries ITS OWN node, never another agent's");
        byTitle["edited app.tsx"].AgentRunId.ShouldBe(frontend.ToString());
        byTitle["edited app.tsx"].NodeId.ShouldBe("frontend-fix");
    }

    private async Task<IReadOnlyList<RunTimelineEvent>?> ProjectAsync(Guid userId, Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IRunTimelineProjector>().ProjectAsync(runId, teamId, CancellationToken.None);
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

    private async Task SeedAgentEventsAsync(Guid agentRunId, params (AgentEventKind Kind, string Text, DateTimeOffset At)[] events)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        foreach (var (kind, text, at) in events)
        {
            // Sequence is a DB-assigned BIGSERIAL — left unset so insert order drives it.
            db.AgentRunEvent.Add(new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = agentRunId, Kind = kind, Text = text, OccurredAt = at });
        }

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
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Failure,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task SeedRecordsAsync(Guid runId, params (string Type, string? NodeId, string Payload, DateTimeOffset At)[] records)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        foreach (var (type, nodeId, payload, at) in records)
        {
            // Sequence is a DB-assigned BIGSERIAL — left unset so insert order (= chronological here) drives it.
            db.WorkflowRunRecord.Add(new WorkflowRunRecord
            {
                Id = Guid.NewGuid(), RunId = runId, RecordType = type, NodeId = nodeId, OccurredAt = at, PayloadJson = payload,
            });
        }

        await db.SaveChangesAsync();
    }
}

using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
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
/// 🟢 Integration (real Postgres + the REAL timeline sources — run-record, agent-events, supervisor-ledger, tool-calls —
/// resolved from DI): the narrative timeline end-to-end at the projector seam. A run's append-only
/// <c>workflow_run_record</c> ledger, its agents' harness events, its supervisor decision ledger, and its side-effecting
/// tool-call ledger all surface through the run-timeline projector as NARRATIVE-worthy events, chronologically merged,
/// with Trace-level noise (log lines / chatter) dropped. Every read is team-scoped (a foreign row never leaks); a
/// foreign / absent run resolves to null (fail-closed). A new source is picked up purely by DI — no projector edit.
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
    public async Task Projects_a_model_calls_completed_outcome_dropping_the_started_open_bracket()
    {
        // The recording substrate writes interaction.started/completed for every in-process model call; the timeline
        // renders the OUTCOME (kind + model + token cost) at Detail and drops the started open bracket. Proves the render
        // chain end-to-end: the source loads the interaction.* records (no type filter) and the new map arm surfaces them.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);

        var t = DateTimeOffset.UtcNow;
        await SeedRecordsAsync(runId,
            (WorkflowRunRecordTypes.RunStarted, null, "{}", t),
            (WorkflowRunRecordTypes.NodeStarted, "gen", "{}", t.AddSeconds(1)),
            (WorkflowRunRecordTypes.InteractionStarted, "gen", """{"kind":"llm.complete","model":"claude-opus-4-8"}""", t.AddSeconds(2)),
            (WorkflowRunRecordTypes.InteractionCompleted, "gen", """{"kind":"llm.complete","model":"claude-opus-4-8","usage":{"inputTokens":17,"outputTokens":19}}""", t.AddSeconds(3)),
            (WorkflowRunRecordTypes.NodeCompleted, "gen", "{}", t.AddSeconds(4)),
            (WorkflowRunRecordTypes.RunCompleted, null, "{}", t.AddSeconds(5)));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        var modelCalls = events!.Where(e => e.SourceKey == "run-record" && e.Title.StartsWith("Model call")).ToList();

        modelCalls.ShouldHaveSingleItem("the completed outcome surfaces once; the interaction.started open bracket is Trace-only");
        modelCalls[0].Title.ShouldBe("Model call");
        modelCalls[0].Summary.ShouldBe("llm.complete · claude-opus-4-8 · 36 tokens", "the per-call kind + model + token cost surfaces in the narrative");
        modelCalls[0].NodeId.ShouldBe("gen");
        modelCalls[0].Level.ShouldBe(TimelineLevel.Detail);
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
            (AgentEventKind.AssistantMessage, "thinking out loud", t),               // assistant text → dropped (Trace-only)
            (AgentEventKind.FileChanged, "edited auth/session.ts", t.AddSeconds(1)),
            (AgentEventKind.Reasoning, "considering options", t.AddSeconds(2)),       // reasoning → surfaces (folded thinking beat)
            (AgentEventKind.Error, "2 tests failing", t.AddSeconds(3)),
            (AgentEventKind.FinalSummary, "done — fixed the session bug", t.AddSeconds(4)));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        var agentEvents = events!.Where(e => e.SourceKey == "agent-events").ToList();
        agentEvents.Select(e => e.Title).ShouldBe(new[] { "edited auth/session.ts", "considering options", "2 tests failing", "done — fixed the session bug" },
            "the narrative kinds (now including reasoning) surface chronologically; only the assistant-text chatter is dropped");
        agentEvents.ShouldAllBe(e => e.AgentRunId == agentId.ToString());
        agentEvents.ShouldAllBe(e => e.NodeId == "code");
        agentEvents.Single(e => e.Title == "2 tests failing").Severity.ShouldBe(TimelineSeverity.Error);
        agentEvents.Single(e => e.Title == "considering options").Level.ShouldBe(TimelineLevel.Detail, "reasoning is a folded Detail beat, not a milestone");
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

    [Fact]
    public async Task Projects_the_supervisor_ledger_as_decision_events_merged_chronologically()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);

        var t = DateTimeOffset.UtcNow;
        await SeedSupervisorDecisionsAsync(runId, teamId,
            (SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, null, null, t),
            (SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded, StagedAgents(2), null, t.AddSeconds(1)),
            (SupervisorDecisionKinds.AskHuman, SupervisorDecisionStatus.Succeeded, SupervisorOutcome.FoldAnswer("Proceed?", "tok", "yes"), null, t.AddSeconds(2)),
            (SupervisorDecisionKinds.Stop, SupervisorDecisionStatus.Succeeded, null, null, t.AddSeconds(3)));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        var supervisor = events!.Where(e => e.SourceKey == "supervisor").ToList();
        supervisor.Select(e => e.Title).ShouldBe(new[] { "Supervisor planned the work", "Supervisor spawned 2 agents", "Supervisor asked you — answered", "Supervisor stopped" },
            "the decision ledger projects chronologically as the dynamic-supervisor story line");
        supervisor.Single(e => e.Title == "Supervisor asked you — answered").Summary.ShouldBe("Proceed? — yes", "ask_human surfaces the question + the folded answer");
    }

    [Fact]
    public async Task Merges_supervisor_decisions_between_the_lifecycle_records_by_time()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);

        // Distinct timestamps across BOTH sources, so OccurredAt alone fixes the order — this locks the projector's
        // cross-source merge: a supervisor decision must slot BETWEEN the lifecycle records, not after its own block.
        var t = DateTimeOffset.UtcNow;
        await SeedRecordsAsync(runId,
            (WorkflowRunRecordTypes.RunStarted, null, "{}", t),
            (WorkflowRunRecordTypes.NodeStarted, "code", "{}", t.AddSeconds(2)),
            (WorkflowRunRecordTypes.RunCompleted, null, "{}", t.AddSeconds(4)));
        await SeedSupervisorDecisionsAsync(runId, teamId,
            (SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, null, null, t.AddSeconds(1)),
            (SupervisorDecisionKinds.Stop, SupervisorDecisionStatus.Succeeded, null, null, t.AddSeconds(3)));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        events!.Select(e => e.Title).ShouldBe(new[]
        {
            "Run started",                  // t
            "Supervisor planned the work",  // t+1 — interleaved between the lifecycle records by OccurredAt
            "code started",                 // t+2
            "Supervisor stopped",           // t+3
            "Run completed",                // t+4
        }, "the projector merges the supervisor decisions and the lifecycle records into one chronological story");
    }

    [Fact]
    public async Task Excludes_a_supervisor_decision_stamped_to_another_team()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);

        // A decision row pointing at this run id but stamped to ANOTHER team — the team-scoped ledger read must keep it off.
        await SeedSupervisorDecisionsAsync(runId, otherTeamId, (SupervisorDecisionKinds.Plan, SupervisorDecisionStatus.Succeeded, null, null, DateTimeOffset.UtcNow));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        events!.ShouldNotContain(e => e.SourceKey == "supervisor", "a decision stamped to another team is filtered by the team-scoped ledger read");
    }

    [Fact]
    public async Task Projects_a_runs_side_effecting_tool_calls_tagged_with_the_agent()
    {
        // The tool-call source is picked up PURELY by DI (no projector edit) — a run's side effects surface on the
        // timeline, tagged with their agent + node, severity/level riding the ledger status.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var agentId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentId, "code");

        var t = DateTimeOffset.UtcNow;
        await SeedToolCallsAsync(teamId, agentId,
            (ToolCallLedgerStatus.Succeeded, "git.open_pr", null, null, t),
            (ToolCallLedgerStatus.Failed, "git.commit", "remote rejected: protected branch", null, t.AddSeconds(1)));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        var tools = events!.Where(e => e.SourceKey == "tool-calls").ToList();
        tools.Select(e => e.Title).ShouldBe(new[] { "Opened a pull request", "Committing the changes failed" }, "the side-effecting tool calls surface chronologically, tagged, outcome-aware");
        tools.ShouldAllBe(e => e.AgentRunId == agentId.ToString());
        tools.ShouldAllBe(e => e.NodeId == "code");

        var failed = tools.Single(e => e.Title == "Committing the changes failed");
        failed.Severity.ShouldBe(TimelineSeverity.Error);
        failed.Level.ShouldBe(TimelineLevel.Milestone, "a failed side effect is a milestone the operator must see");
        failed.Summary.ShouldBe("remote rejected: protected branch");
    }

    [Fact]
    public async Task Excludes_decision_request_rows_by_kind_but_surfaces_a_real_awaiting_approval_side_effect()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var agentId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentId, "code");

        // The discriminator is ToolKind, NOT the envelope: a decision.request row is a cross-grain decision the "Needs
        // decision" queue surfaces — excluded EVEN WITH a null envelope (a decision row is INSERTed null-envelope, then
        // stashes it in a SECOND write, so an envelope-null proxy would leak it during that window / after a crash). A
        // REAL side-effecting approval is AwaitingApproval with a null envelope BY DESIGN (ToolCallLedger doc) → it must
        // surface. This kills both a status-keyed mutant (would drop deploy.trigger) and an envelope-keyed one (would
        // surface the null-envelope decision.request).
        var t = DateTimeOffset.UtcNow;
        await SeedToolCallsAsync(teamId, agentId,
            (ToolCallLedgerStatus.Pending, "decision.request", null, null, t),                                 // decision, null envelope → excluded
            (ToolCallLedgerStatus.AwaitingApproval, "decision.request", null, """{"question":"proceed?"}""", t.AddSeconds(1)),  // decision, envelope set → excluded
            (ToolCallLedgerStatus.AwaitingApproval, "deploy.trigger", null, null, t.AddSeconds(2)));           // REAL side effect awaiting approval, null envelope → SURFACES

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        var tools = events!.Where(e => e.SourceKey == "tool-calls").ToList();
        tools.Select(e => e.Title).ShouldBe(new[] { "Triggering the deploy — awaiting your approval" },
            "only the real side effect surfaces; BOTH decision.request rows (null-envelope AND envelope-set) are excluded by ToolKind");
        tools[0].Severity.ShouldBe(TimelineSeverity.Info, "an awaiting-approval real side effect is in-flight → Info");
    }

    [Fact]
    public async Task A_multi_agent_runs_tool_calls_are_each_tagged_with_their_own_agent_and_node()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var backend = Guid.NewGuid();
        var frontend = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, backend, "backend-fix");
        await SeedAgentRunAsync(runId, teamId, frontend, "frontend-fix");

        var t = DateTimeOffset.UtcNow;
        await SeedToolCallsAsync(teamId, backend, (ToolCallLedgerStatus.Succeeded, "git.commit", null, null, t));
        await SeedToolCallsAsync(teamId, frontend, (ToolCallLedgerStatus.Succeeded, "git.open_pr", null, null, t.AddSeconds(1)));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        var byTitle = events!.Where(e => e.SourceKey == "tool-calls").ToDictionary(e => e.Title);
        byTitle["Committed the changes"].AgentRunId.ShouldBe(backend.ToString());
        byTitle["Committed the changes"].NodeId.ShouldBe("backend-fix", "each agent's tool call carries ITS OWN node, never another agent's");
        byTitle["Opened a pull request"].AgentRunId.ShouldBe(frontend.ToString());
        byTitle["Opened a pull request"].NodeId.ShouldBe("frontend-fix");
    }

    [Fact]
    public async Task Merges_a_tool_call_between_the_lifecycle_records_by_time()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var agentId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentId, "code");

        var t = DateTimeOffset.UtcNow;
        await SeedRecordsAsync(runId,
            (WorkflowRunRecordTypes.RunStarted, null, "{}", t),
            (WorkflowRunRecordTypes.RunCompleted, null, "{}", t.AddSeconds(2)));
        await SeedToolCallsAsync(teamId, agentId, (ToolCallLedgerStatus.Succeeded, "git.open_pr", null, null, t.AddSeconds(1)));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        events!.Select(e => e.Title).ShouldBe(new[] { "Run started", "Opened a pull request", "Run completed" },
            "the tool call interleaves between the lifecycle records by OccurredAt — the cross-source merge");
    }

    [Fact]
    public async Task Excludes_a_tool_call_stamped_to_another_team()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var agentId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentId, "code");

        // A tool-call row pointing at this run's agent but stamped to ANOTHER team — the team-scoped ledger read keeps it off.
        await SeedToolCallsAsync(otherTeamId, agentId, (ToolCallLedgerStatus.Succeeded, "git.open_pr", null, null, DateTimeOffset.UtcNow));

        var events = await ProjectAsync(userId, teamId, runId);

        events.ShouldNotBeNull();
        events!.ShouldNotContain(e => e.SourceKey == "tool-calls", "a tool call stamped to another team is filtered by the team-scoped ledger read");
    }

    private async Task SeedToolCallsAsync(Guid teamId, Guid agentRunId, params (ToolCallLedgerStatus Status, string ToolKind, string? Error, string? DecisionEnvelope, DateTimeOffset At)[] calls)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        foreach (var (status, toolKind, error, envelope, at) in calls)
        {
            db.ToolCallLedger.Add(new ToolCallLedger
            {
                Id = Guid.NewGuid(), TeamId = teamId, AgentRunId = agentRunId,
                ToolKind = toolKind, IdempotencyKey = Guid.NewGuid().ToString("N"), InputHash = "test",
                Status = status, Error = error, DecisionEnvelopeJson = envelope,
                CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedDate = at, LastModifiedBy = SystemUsers.SeederId,
            });
        }

        await db.SaveChangesAsync();
    }

    private static string StagedAgents(int count) =>
        JsonSerializer.Serialize(new { agentRunIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString()).ToArray() });

    private async Task<IReadOnlyList<RunTimelineEvent>?> ProjectAsync(Guid userId, Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IRunTimelineProjector>().ProjectAsync(runId, teamId, CancellationToken.None);
    }

    private async Task SeedSupervisorDecisionsAsync(Guid runId, Guid teamId, params (string Kind, SupervisorDecisionStatus Status, string? Outcome, string? Error, DateTimeOffset At)[] decisions)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        foreach (var (kind, status, outcome, error, at) in decisions)
        {
            // Sequence is a DB-assigned BIGSERIAL — left unset so insert order (= chronological here) drives it.
            db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
            {
                Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
                DecisionKind = kind, Status = status, PayloadJson = "{}", OutcomeJson = outcome, Error = error,
                IdempotencyKey = Guid.NewGuid().ToString("N"), InputHash = "test",
                CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedDate = at, LastModifiedBy = SystemUsers.SeederId,
            });
        }

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

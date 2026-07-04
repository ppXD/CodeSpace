using System.Text.Json;
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
using CodeSpace.Messages.Queries.Sessions;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="IJournalProjector"/> resolved from DI, over the real session
/// skeleton + the real journal walk): the journal end-to-end. A session's turns project into a <see cref="JournalView"/>;
/// the FOCUSED turn (the run-anchored one) is walked into its chronological steps, the other turn is a light card with no
/// steps; a foreign session projects to null. Proves the whole composition — session read + timeline walk — over real data.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class JournalProjectorFlowTests
{
    private readonly PostgresFixture _fixture;

    public JournalProjectorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Projects_a_session_focusing_the_anchored_turn_into_its_journal_steps()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Build the dashboard");

        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "First task", resultSummary: "First task done");
        await SeedTurnAsync(teamId, sessionId, turn: 2, goal: "Second task", resultSummary: "Second task done");

        // Turn 1's run made two supervisor decisions — the walk turns them into chronological steps.
        var t = DateTimeOffset.UtcNow;
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Plan, t);
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Stop, t.AddSeconds(1));

        JournalView? view;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(run1, teamId, CancellationToken.None);

        view.ShouldNotBeNull();
        view!.Turns.Count.ShouldBe(2);
        view.AnchorTurnIndex.ShouldBe(1, "entering by turn 1's run anchors turn 1");

        var focused = view.Turns.Single(t => t.Focused);
        focused.TurnIndex.ShouldBe(1);
        focused.UserMessage.ShouldBe("First task");
        focused.Steps.Select(s => s.Kind).ShouldBe(new[] { JournalStepKinds.Decision, JournalStepKinds.Decision }, "the focused turn is walked into its decision steps, chronologically");
        focused.Steps.Select(s => s.Title).ShouldBe(new[] { "Supervisor planned the work", "Supervisor stopped" });
        view.Cursor.ShouldBe(focused.Steps[^1].Cursor, "the view cursor is the focused turn's newest step");

        var collapsed = view.Turns.Single(t => !t.Focused);
        collapsed.TurnIndex.ShouldBe(2);
        collapsed.Steps.ShouldBeEmpty("the non-focused turn is a light card");
        collapsed.Summary.ShouldBe("Second task done");
    }

    [Fact]
    public async Task Entering_by_a_prior_attempts_run_focuses_that_attempts_flow_not_the_latest()
    {
        // Parity with the room's attempt switcher, over a REAL rerun ladder: turn 1 was reran (attempt 1 failed,
        // attempt 2 succeeded). Deep-linking attempt 1's run must focus attempt 1 — its Failure status + its own walked
        // steps — not the newest attempt's Success.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Reran turn");

        var t = DateTimeOffset.UtcNow;
        var attempt1 = await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, WorkflowRunStatus.Failure, createdAt: t, error: "vitest timed out");
        await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: attempt1, WorkflowRunStatus.Success, createdAt: t.AddMinutes(1));

        // attempt 1 made a decision before it failed — the walk of attempt 1 surfaces it.
        await SeedDecisionAsync(attempt1, teamId, SupervisorDecisionKinds.Plan, t.AddSeconds(1));

        JournalView? view;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(attempt1, teamId, CancellationToken.None);

        view.ShouldNotBeNull();
        var focused = view!.Turns.Single(t => t.Focused);
        focused.RunId.ShouldBe(attempt1, "the anchored attempt is focused, not the newest");
        focused.Status.ShouldBe(WorkflowRunStatus.Failure, "attempt 1's OWN status, not attempt 2's Success");
        focused.Steps.Select(s => s.Title).ShouldBe(new[] { "Supervisor planned the work" }, "attempt 1's run was walked");

        // The pager data flows end-to-end: the full 2-attempt ladder, with the anchored (deep-linked) attempt 1 focused.
        focused.Attempts.Select(a => (a.AttemptNumber, a.Status)).ShouldBe(new[] { (1, WorkflowRunStatus.Failure), (2, WorkflowRunStatus.Success) });
        focused.Attempts.Single(a => a.Focused).RunId.ShouldBe(attempt1, "the deep-linked attempt is flagged focused in the ladder");
        focused.Attempts.Single(a => a.IsLatest).Status.ShouldBe(WorkflowRunStatus.Success, "the newest attempt is flagged latest");
        focused.Attempts.Single(a => a.AttemptNumber == 1).Error.ShouldBe("vitest timed out", "the failed attempt carries its reason (why it was reran); the succeeded one has none");
        focused.Attempts.Single(a => a.AttemptNumber == 2).Error.ShouldBeNull();
    }

    [Fact]
    public async Task A_mid_stream_since_returns_only_the_steps_after_the_cursor()
    {
        // The delta over REAL walk cursors through the full handler pipeline: three decisions → three steps; a re-read
        // with ?since = the FIRST step's cursor returns only the LATER two, and preserves the self-heal StepCount.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Live");
        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Task", resultSummary: "done");

        var t = DateTimeOffset.UtcNow;
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Plan, t);
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Merge, t.AddSeconds(1));
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Stop, t.AddSeconds(2));

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();

        var full = (await mediator.Send(new GetRunJournalQuery { RunId = run1 }))!;
        var steps = full.Turns.Single(t => t.Focused).Steps;
        steps.Count.ShouldBe(3, "three decisions → three steps");

        var delta = (await mediator.Send(new GetRunJournalQuery { RunId = run1, Since = steps[0].Cursor }))!;
        var focused = delta.Turns.Single(t => t.Focused);

        focused.Steps.Select(s => s.Id).ShouldBe(steps.Skip(1).Select(s => s.Id), "only the steps after the client's cursor come back over the real pipeline");
        focused.StepCount.ShouldBe(3, "the delta preserves the full step total (the self-heal signal)");
    }

    [Fact]
    public async Task A_decision_step_carries_the_supervisors_authored_rationale()
    {
        // The facts-enrichment path end-to-end over real data: a decision whose payload carries a rationale surfaces it on
        // its journal step (the chain-of-thought), read off the durable decision tape by the real facts source — NOT from
        // the timeline event. A decision with no rationale stays bare.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Reasoned run");
        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Task", resultSummary: "done");

        var t = DateTimeOffset.UtcNow;
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Plan, t, RationalePayload("Break the work into 3 tasks", "the goal names 3 files"));
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Stop, t.AddSeconds(1));

        JournalView? view;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(run1, teamId, CancellationToken.None);

        var steps = view!.Turns.Single(t => t.Focused).Steps;
        steps[0].Title.ShouldBe("Supervisor planned the work");
        steps[0].Rationale.ShouldBe("Break the work into 3 tasks · Evidence: the goal names 3 files", "the plan step carries the model's authored rationale, read off the decision payload");
        steps[1].Rationale.ShouldBeNull("the stop authored no rationale — it stays bare");
    }

    [Fact]
    public async Task A_spawn_decision_step_carries_a_card_for_every_agent_it_staged()
    {
        // The agent-card enrichment end-to-end over real data: a spawn decision whose outcome staged two agents surfaces a
        // card per agent on its journal step — labels + statuses read off the real AgentRun rows through the shared metrics
        // reader (the SAME numbers the phase board reads), keyed to the decision's step. Proves the tape→ids→batched-read
        // →cards wiring, not just the pure mapping.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Fan-out run");
        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Build it", resultSummary: "done");

        var agentA = await SeedAgentRunAsync(teamId, run1, goal: "Write the API", status: AgentRunStatus.Succeeded, resumeFromSessionId: "codex-thread-prior");
        var agentB = await SeedAgentRunAsync(teamId, run1, goal: "Write the UI", status: AgentRunStatus.Running);

        var t = DateTimeOffset.UtcNow;
        await SeedSpawnDecisionAsync(run1, teamId, t, new[] { agentA, agentB });

        JournalView? view;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(run1, teamId, CancellationToken.None);

        var spawn = view!.Turns.Single(t => t.Focused).Steps.Single(s => s.Kind == JournalStepKinds.Decision);
        spawn.Agents.Count.ShouldBe(2, "one card per staged agent");
        spawn.Agents.Select(a => a.Label).ShouldBe(new[] { "Write the API", "Write the UI" }, ignoreOrder: true);
        spawn.Agents.Single(a => a.Label == "Write the API").AgentRunId.ShouldBe(agentA);
        spawn.Agents.Single(a => a.Label == "Write the API").Resumed.ShouldBeTrue("agent A's task carried a resume session id — it continued a prior conversation");
        spawn.Agents.Single(a => a.Label == "Write the UI").Resumed.ShouldBeFalse("agent B started fresh");
    }

    [Fact]
    public async Task A_spawned_agents_card_carries_its_per_file_diffstat()
    {
        // The diffstat end-to-end: a spawned agent whose result holds git-truth FileStats surfaces +added / −removed rows on
        // its card — read off the real result through the shared metrics reader, not recomputed.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Diffstat run");
        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Build it", resultSummary: "done");

        var result = JsonSerializer.Serialize(new
        {
            status = "Succeeded", exitReason = "completed",
            changedFiles = new[] { "auth/session.ts", "img/logo.png" },
            fileStats = new object[] { new { path = "auth/session.ts", additions = 42, deletions = 3 }, new { path = "img/logo.png", additions = (int?)null, deletions = (int?)null } },
        });
        var agent = await SeedAgentRunAsync(teamId, run1, goal: "Refactor auth", status: AgentRunStatus.Succeeded, resultJson: result);

        await SeedSpawnDecisionAsync(run1, teamId, DateTimeOffset.UtcNow, new[] { agent });

        JournalView? view;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(run1, teamId, CancellationToken.None);

        var card = view!.Turns.Single(t => t.Focused).Steps.Single(s => s.Kind == JournalStepKinds.Decision).Agents.Single();
        card.FilesChanged.ShouldBe(2);
        card.Harness.ShouldBe("codex-cli", "the harness kind reaches the card off the task envelope, end-to-end");
        card.Files.Select(f => (f.Path, f.Additions, f.Deletions))
            .ShouldBe(new[] { ("auth/session.ts", (int?)42, (int?)3), ("img/logo.png", null, null) }, "the card carries the git-truth per-file diffstat, binary counts null");
    }

    [Fact]
    public async Task A_cards_file_count_comes_from_the_supervisor_compact_when_the_agent_result_folded_none()
    {
        // The codex-cli case end-to-end: the agent's own result row carried NO changed-file list, but the supervisor
        // folded the git-truth changed files into the spawn decision's compact. The journal card must show that count —
        // the SAME one the room card reads off the compact — instead of a blank, so the two views can't disagree.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Compact-files run");
        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Research it", resultSummary: "done");

        // A result with no changedFiles — the harness reported completion but folded no file list onto its own row.
        var resultNoFiles = JsonSerializer.Serialize(new { status = "Succeeded", exitReason = "completed" });
        var agent = await SeedAgentRunAsync(teamId, run1, goal: "Deep-dive the turn logic", status: AgentRunStatus.Succeeded, resultJson: resultNoFiles);

        await SeedSpawnDecisionAsync(run1, teamId, DateTimeOffset.UtcNow, new[] { agent },
            agentResults: new[] { (agent, new[] { "docs/report.md" }) });

        JournalView? view;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(run1, teamId, CancellationToken.None);

        var card = view!.Turns.Single(t => t.Focused).Steps.Single(s => s.Kind == JournalStepKinds.Decision).Agents.Single();
        card.FilesChanged.ShouldBe(1, "the compact's git-truth count fills the gap the empty result row left");
        card.Files.Select(f => f.Path).ShouldBe(new[] { "docs/report.md" }, "path-only rows from the compact when the result carried no diffstat");
    }

    [Fact]
    public async Task A_staged_agent_with_no_readable_row_is_skipped_never_crashed_or_fabricated()
    {
        // The load-bearing skip guard: a spawn stages two ids but only one has a persisted, team-scoped AgentRun row (the
        // other is stale / another team's / not yet written). The card for the present agent surfaces; the absent id is
        // silently dropped — NOT crashed (a KeyNotFoundException on the metrics lookup) and NOT fabricated (a phantom card
        // for an agent that isn't there). Exercises the `.Where(metrics.ContainsKey)` branch the happy path never reaches.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Partial fan-out");
        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Build it", resultSummary: "done");

        var present = await SeedAgentRunAsync(teamId, run1, goal: "Write the API", status: AgentRunStatus.Succeeded);
        var rowless = Guid.NewGuid();   // staged in the outcome, but no AgentRun row exists for it

        await SeedSpawnDecisionAsync(run1, teamId, DateTimeOffset.UtcNow, new[] { present, rowless });

        JournalView? view;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(run1, teamId, CancellationToken.None);

        var spawn = view!.Turns.Single(t => t.Focused).Steps.Single(s => s.Kind == JournalStepKinds.Decision);
        spawn.Agents.Select(a => a.AgentRunId).ShouldBe(new[] { present }, "only the agent with a readable row gets a card — the rowless id is skipped, not crashed or fabricated");
    }

    [Fact]
    public async Task A_spawn_step_carries_the_dependency_blocked_frontier()
    {
        // The blocked frontier end-to-end over real data: a DAG plan (b depends on a) + a wave that ran a surfaces the
        // still-blocked b on the spawn step — replaying the REAL dependency gate over the persisted tape, so the journal's
        // "waiting on #n" can't drift from the gate the server enforces.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "DAG run");
        var run1 = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Build it", resultSummary: "done");

        var t = DateTimeOffset.UtcNow;
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Plan, t, DagPlanPayload());
        await SeedDecisionAsync(run1, teamId, SupervisorDecisionKinds.Spawn, t.AddSeconds(1), SpawnPayloadFor("a"));

        JournalView? view;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            view = await scope.Resolve<IJournalProjector>().ProjectByRunAsync(run1, teamId, CancellationToken.None);

        var steps = view!.Turns.Single(t => t.Focused).Steps;
        steps.First(s => s.Title.Contains("planned")).Deferred.ShouldBeEmpty("the plan step carries no frontier");

        var spawn = steps.First(s => s.Title.Contains("spawned"));
        spawn.Deferred.Select(d => d.SubtaskId).ShouldBe(new[] { "b" }, "b is blocked at this wave (dep a not accepted yet) — surfaced on the spawn");
        spawn.Deferred.Single().WaitingOn.ShouldBe(new[] { "a" });
    }

    [Fact]
    public async Task A_foreign_session_projects_to_null()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        (await scope.Resolve<IJournalProjector>().ProjectByRunAsync(Guid.NewGuid(), teamId, CancellationToken.None))
            .ShouldBeNull("a run that isn't the team's is null — no existence leak");
    }

    private static string RationalePayload(string why, string evidence) =>
        JsonSerializer.Serialize(new { rationale = new { why, evidence } });

    /// <summary>A spawn payload that requests the given subtask ids (the wave's fan-out). The frontier the source computes ignores this — it reads the plan + prior outcomes — but a realistic wave requests the ready ones.</summary>
    private static string SpawnPayloadFor(params string[] subtaskIds) => JsonSerializer.Serialize(new { subtaskIds });

    /// <summary>A 2-subtask plan where b depends on a — the DAG that makes the gate defer b until a is accepted.</summary>
    private static string DagPlanPayload() =>
        JsonSerializer.Serialize(new
        {
            goal = "g",
            subtasks = new object[]
            {
                new { id = "a", title = "a", instruction = "do a" },
                new { id = "b", title = "b", instruction = "do b", dependsOn = new[] { "a" } },
            },
        });

    private async Task<Guid> SeedAgentRunAsync(Guid teamId, Guid runId, string goal, AgentRunStatus status, string? resultJson = null, string? resumeFromSessionId = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var agentRunId = Guid.NewGuid();

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, NodeId = "agent", IterationKey = $"agent#{agentRunId:N}",
            Harness = "codex-cli", Status = status, ResultJson = resultJson,
            TaskJson = JsonSerializer.Serialize(new { goal, harness = "codex-cli", model = "claude-opus-4-8", resumeFromSessionId }),
            StartedAt = now, CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return agentRunId;
    }

    private async Task SeedSpawnDecisionAsync(Guid runId, Guid teamId, DateTimeOffset at, IReadOnlyList<Guid> agentRunIds, IReadOnlyList<(Guid agentId, string[] changedFiles)>? agentResults = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        // Mirror the real spawn outcome: agentRunIds are always folded; agentResults (the compact carrying each agent's
        // git-truth changed files) is folded only when the supervisor recorded them, so callers that omit it exercise the
        // metrics-only path exactly as before.
        var outcome = new Dictionary<string, object?>
        {
            ["agentRunIds"] = agentRunIds.Select(id => id.ToString()),
            ["agentCount"] = agentRunIds.Count,
        };
        if (agentResults != null)
            outcome["agentResults"] = agentResults.Select(r => new { agentRunId = r.agentId.ToString(), status = "Succeeded", changedFiles = r.changedFiles });

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}",
            OutcomeJson = JsonSerializer.Serialize(outcome),
            IdempotencyKey = Guid.NewGuid().ToString("N"), InputHash = "test",
            CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedDate = at, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedDecisionAsync(Guid runId, Guid teamId, string kind, DateTimeOffset at, string payloadJson = "{}")
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = null,
            IdempotencyKey = Guid.NewGuid().ToString("N"), InputHash = "test",
            CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedDate = at, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedAttemptAsync(Guid teamId, Guid sessionId, int turnIndex, Guid? rootRunId, WorkflowRunStatus status, DateTimeOffset createdAt, string? error = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = createdAt, VerifiedAt = createdAt, NormalizedAt = createdAt,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = status, SessionId = sessionId, SessionTurnIndex = turnIndex, RootRunId = rootRunId, Error = error,
            OutputsJson = "{}", CreatedDate = createdAt, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Guid> SeedSessionAsync(Guid teamId, string title)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var id = Guid.NewGuid();
        db.WorkSession.Add(new WorkSession { Id = id, TeamId = teamId, Title = title, Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTurnAsync(Guid teamId, Guid sessionId, int turn, string goal, string? resultSummary)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var outputs = string.IsNullOrEmpty(resultSummary) ? "{}" : JsonSerializer.Serialize(new { summary = resultSummary, branch = (string?)null });

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal }),
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = turn,
            OutputsJson = outputs,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }
}

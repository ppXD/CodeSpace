using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// The room projector over real Postgres — the DB assembly the pure <see cref="RoomNarrative"/> can't cover: the turn
/// skeleton (goals + latest-attempt + status per turn), the focused-vs-collapsed split, the change-watermark cursor
/// (MAX of the run's append-only ledger), and tenancy (a foreign run / session is an indistinguishable null). The
/// narrative/map richness is proven exhaustively at the unit tier; this proves the wiring + the persistence reads.
///
/// <para>Tier: high-fidelity Integration — the real <see cref="IRoomProjector"/> + its dependencies over real Postgres.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RoomProjectorFlowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _fixture;

    public RoomProjectorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Projects_the_thread_with_a_focused_latest_turn_and_a_collapsed_prior_turn()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Build the dashboard");

        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "First task", resultSummary: "First task done");
        var run2 = await SeedTurnAsync(teamId, sessionId, turn: 2, goal: "Second task", resultSummary: "Second task done");
        var watermark = await SeedRecordsAsync(run2, count: 3);

        var room = await ProjectByRunAsync(run2, teamId);

        room.ShouldNotBeNull();
        room!.SessionId.ShouldBe(sessionId);
        room.Title.ShouldBe("Build the dashboard");
        room.AnchorBlockId.ShouldBe("turn-2", "entering by the latest run focuses its turn");
        room.Cursor.ShouldBe(watermark, "the cursor is the focused run's change watermark");

        room.Blocks.OfType<UserMessageBlock>().Select(b => b.Text).ShouldBe(new[] { "First task", "Second task" }, "the user messages are the per-turn goals, oldest first");

        var turns = room.Blocks.OfType<AssistantTurnBlock>().OrderBy(t => t.TurnIndex).ToList();
        turns.Count.ShouldBe(2);

        var collapsed = turns[0];
        collapsed.TurnIndex.ShouldBe(1);
        collapsed.Blocks.ShouldBeEmpty("a non-focused turn is a light card — no inner blocks");
        collapsed.Map.ShouldBeNull();
        collapsed.Seq.ShouldBe(0);
        collapsed.Summary.ShouldBe("First task done", "the collapsed card shows the turn's result");
        collapsed.Actions.ShouldContain(a => a.Kind == RoomActionKind.OpenTrace, "a collapsed card still carries its capability-aware actions");

        var focused = turns[1];
        focused.TurnIndex.ShouldBe(2);
        focused.RunId.ShouldBe(run2);
        focused.Seq.ShouldBe(watermark);
        focused.Actions.ShouldContain(a => a.Kind == RoomActionKind.RerunTurn && a.Enabled, "a finished focused turn offers a rerun");
    }

    [Fact]
    public async Task A_foreign_run_or_session_projects_to_null_never_leaked()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeam, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Mine");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "x", resultSummary: "y");

        (await ProjectByRunAsync(run, otherTeam)).ShouldBeNull("a cross-team run resolves to no room");

        using var scope = _fixture.BeginScope();
        var foreignSession = await scope.Resolve<IRoomProjector>().ProjectAsync(sessionId, null, otherTeam, CancellationToken.None);
        foreignSession.ShouldBeNull("a cross-team session resolves to no room");
    }

    [Fact]
    public async Task The_focused_turn_surfaces_node_and_agent_grain_decisions_but_not_a_foreign_runs()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Decisions");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Decide things", resultSummary: null);

        var otherSession = await SeedSessionAsync(teamId, "Other");
        var foreignRun = await SeedTurnAsync(teamId, otherSession, turn: 1, goal: "Other run", resultSummary: null);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        await SeedNodeDecisionAsync(teamId, run, "Pick a path", deadline, new[]
        {
            new DecisionOption { Id = "safe", Label = "Stay safe" },
            new DecisionOption { Id = "deploy", Label = "Deploy now", IsSideEffecting = true },
        });
        await SeedAgentDecisionAsync(teamId, run, "Approve the deploy?", deadline);
        await SeedNodeDecisionAsync(teamId, foreignRun, "Foreign decision", deadline, Array.Empty<DecisionOption>());

        var room = await ProjectByRunAsync(run, teamId);

        var decisions = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1).Blocks.OfType<DecisionBlock>().ToList();

        // Both the run-grain (node) and agent-grain decisions surface; the foreign run's does not leak in.
        decisions.Select(d => d.Question).OrderBy(q => q).ToArray().ShouldBe(new[] { "Approve the deploy?", "Pick a path" });

        var node = decisions.Single(d => d.Question == "Pick a path");
        node.Id.ShouldBe($"decision-{node.DecisionId}", "the block id is prefixed off the decision id");
        node.DecisionId.ShouldNotBe(Guid.Empty);
        node.Shape.ShouldBe(DecisionTypes.ChooseOne, "the answer shape is carried verbatim");
        node.Risk.ShouldBe(DecisionRiskLevels.High);
        node.Deadline.ShouldNotBeNull();
        node.Options.ShouldNotBeNull();
        node.Options!.Single(o => o.Id == "deploy").SideEffecting.ShouldBeTrue("the side-effecting flag is carried so the renderer can warn before submit");
        node.Options!.Single(o => o.Id == "safe").SideEffecting.ShouldBeFalse();
    }

    [Fact]
    public async Task A_collapsed_turn_with_no_recorded_result_uses_the_status_summary_and_keeps_its_actions()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Mixed");

        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Failed work", resultSummary: null, status: WorkflowRunStatus.Failure);
        var run2 = await SeedTurnAsync(teamId, sessionId, turn: 2, goal: "Latest", resultSummary: "ok");

        var room = await ProjectByRunAsync(run2, teamId);

        var collapsed = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);
        collapsed.Summary.ShouldBe("Ended with an error.", "no recorded result → the status-derived fallback copy");
        collapsed.Actions.ShouldContain(a => a.Kind == RoomActionKind.OpenTrace, "a collapsed card still carries its actions");
    }

    [Fact]
    public async Task A_supervisor_turn_surfaces_the_canonical_map_and_the_planned_subtasks()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Supervised");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Do the thing", resultSummary: "Shipped it.");

        await SeedPlanDecisionAsync(teamId, run, "Trace DI registration", "Analyze the template store");

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        turn.Map.ShouldNotBeNull();
        turn.Map!.Steps.Select(s => s.Label).ShouldBe(new[] { "Start", "Plan", "Work", "Review", "Deliver" }, "a supervisor turn (decision tape present) gets the canonical lifecycle map");

        var subtasks = turn.Blocks.OfType<StatBlock>().Single(s => s.Kind == "subtasks");
        subtasks.Label.ShouldBe("Plan");
        subtasks.Detail.ShouldBe("2 subtasks");
        subtasks.Items.Select(i => i.Text).ShouldBe(new[] { "Trace DI registration", "Analyze the template store" }, "the plan's subtask titles are surfaced from the decision tape");
    }

    [Fact]
    public async Task A_supervisor_turn_aggregates_distinct_changed_files_across_its_agents()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Files");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Edit files", resultSummary: "Done.");

        await SeedPlanDecisionAsync(teamId, run, "Sub A");
        await SeedSpawnDecisionAsync(teamId, run, (Guid.NewGuid(), new[] { "b.cs", "a.cs" }), (Guid.NewGuid(), new[] { "a.cs", "c.cs" }));

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);
        var files = turn.Blocks.OfType<StatBlock>().Single(s => s.Kind == "files");

        files.Label.ShouldBe("Files changed");
        files.Detail.ShouldBe("3 files", "no diff line stat captured → just the file count");
        files.Items.Select(i => i.Text).ShouldBe(new[] { "a.cs", "b.cs", "c.cs" }, "the distinct, ordinal-sorted union of the agents' changed files (a.cs shared → counted once)");

        var agents = turn.Blocks.OfType<AgentGroupBlock>().Single();
        agents.Title.ShouldBe("Agents", "a terminal supervisor turn surfaces its spawned agents as one group");
        agents.Agents.Count.ShouldBe(2, "one card per spawned agent");
    }

    [Fact]
    public async Task A_reran_turn_surfaces_its_attempt_timeline_oldest_to_newest()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Flaky");
        var now = DateTimeOffset.UtcNow;

        var original = await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, status: WorkflowRunStatus.Failure, source: WorkflowRunSourceTypes.Snapshot, createdAt: now.AddMinutes(-5));
        var winner = await SeedAttemptAsync(teamId, sessionId, turnIndex: null, rootRunId: original, status: WorkflowRunStatus.Success, source: WorkflowRunSourceTypes.Rerun, createdAt: now);

        var room = await ProjectByRunAsync(winner, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single();

        turn.Attempts.Select(a => a.AttemptNumber).ShouldBe(new[] { 1, 2 }, "the turn's attempts, oldest → newest");
        turn.Attempts.Select(a => a.RunId).ShouldBe(new[] { original, winner });
        turn.Attempts[0].Status.ShouldBe(WorkflowRunStatus.Failure, "attempt 1 failed");
        turn.Attempts[1].Status.ShouldBe(WorkflowRunStatus.Success, "the rerun recovered");
        turn.Attempts.Single(a => a.IsCurrent).RunId.ShouldBe(winner, "the shown attempt is the newest");
    }

    [Fact]
    public async Task Opening_a_prior_attempts_run_focuses_that_attempts_flow_not_the_latest()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Flaky");
        var now = DateTimeOffset.UtcNow;

        var original = await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, status: WorkflowRunStatus.Failure, source: WorkflowRunSourceTypes.Snapshot, createdAt: now.AddMinutes(-5));
        var winner = await SeedAttemptAsync(teamId, sessionId, turnIndex: null, rootRunId: original, status: WorkflowRunStatus.Success, source: WorkflowRunSourceTypes.Rerun, createdAt: now);

        // Anchoring on the PRIOR attempt's run (the switcher navigates there) focuses THAT attempt, not the latest.
        var turn = (await ProjectByRunAsync(original, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single();

        turn.RunId.ShouldBe(original, "the focused run is the requested prior attempt, not the latest");
        turn.Status.ShouldBe(WorkflowRunStatus.Failure, "and it carries that attempt's OWN status");
        turn.Attempts.Single(a => a.IsCurrent).RunId.ShouldBe(original, "attempt 1 reads 'shown' when focused; the winner is just another row");
    }

    [Fact]
    public async Task A_never_reran_turn_has_no_attempt_timeline()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "One shot");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Do it once", resultSummary: "Done.");

        var room = await ProjectByRunAsync(run, teamId);
        room!.Blocks.OfType<AssistantTurnBlock>().Single().Attempts.ShouldBeEmpty("a lone attempt needs no history — the timeline stays empty");
    }

    [Fact]
    public async Task The_latest_attempt_shows_its_own_wall_clock_not_the_whole_lineage_span()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Reran a week later");
        var now = DateTimeOffset.UtcNow;

        // attempt 1 (the lineage root) was created a WEEK ago; attempt 2 (a rerun) was created an hour ago and ran ~1h.
        var original = await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, status: WorkflowRunStatus.Failure, source: WorkflowRunSourceTypes.Snapshot, createdAt: now.AddDays(-7), completedAt: now.AddDays(-7).AddMinutes(30));
        var latest = await SeedAttemptAsync(teamId, sessionId, turnIndex: null, rootRunId: original, status: WorkflowRunStatus.Failure, source: WorkflowRunSourceTypes.Rerun, createdAt: now.AddHours(-1), completedAt: now);

        var turn = (await ProjectByRunAsync(latest, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single();

        turn.At!.Value.ShouldBe(now.AddHours(-1), TimeSpan.FromSeconds(5), "the latest attempt shows its OWN created time, not the lineage root's (a week ago)");
        turn.DurationMs!.Value.ShouldBeInRange(50 * 60_000L, 70 * 60_000L, "its OWN ~1h wall-clock, NOT the ~7-day span from the first attempt's creation to now");
    }

    [Fact]
    public async Task Each_attempt_shows_its_own_content_not_the_latest_lineage_merged()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Full reruns");
        var now = DateTimeOffset.UtcNow;

        // Two FULL reruns of the SAME turn — each re-ran the "agent" cell with its OWN output. The lineage merge keeps the
        // NEWEST attempt per cell, so without a per-run scope BOTH attempts' rooms would show attempt 2's output.
        var attempt1 = await SeedAttemptAsync(teamId, sessionId, turnIndex: 1, rootRunId: null, status: WorkflowRunStatus.Success, source: WorkflowRunSourceTypes.Snapshot, createdAt: now.AddMinutes(-10), completedAt: now.AddMinutes(-9));
        await SeedAgentNodeAsync(teamId, attempt1, summary: "first attempt output", changedFiles: new[] { "a1.txt" });

        var attempt2 = await SeedAttemptAsync(teamId, sessionId, turnIndex: null, rootRunId: attempt1, status: WorkflowRunStatus.Success, source: WorkflowRunSourceTypes.Rerun, createdAt: now, completedAt: now.AddMinutes(1));
        await SeedAgentNodeAsync(teamId, attempt2, summary: "second attempt output", changedFiles: new[] { "a2.txt" });

        var turn1 = (await ProjectByRunAsync(attempt1, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);
        var turn2 = (await ProjectByRunAsync(attempt2, teamId))!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        turn1.Blocks.OfType<FinalAnswerBlock>().Single().Text.ShouldBe("first attempt output", "attempt 1 shows its OWN run's content — not the latest attempt's lineage-merged in");
        turn2.Blocks.OfType<FinalAnswerBlock>().Single().Text.ShouldBe("second attempt output", "attempt 2 shows its own");
    }

    /// <summary>Seed one attempt (a top-level turn run when turnIndex is set, else a rerun/replay fork with rootRunId) of a session turn, with an explicit created (and optional completed) time so the attempt ordering + wall-clock are deterministic.</summary>
    private async Task<Guid> SeedAttemptAsync(Guid teamId, Guid sessionId, int? turnIndex, Guid? rootRunId, WorkflowRunStatus status, string source, DateTimeOffset createdAt, DateTimeOffset? completedAt = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = source, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = createdAt, VerifiedAt = createdAt, NormalizedAt = createdAt,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = source,
            Status = status, SessionId = sessionId, SessionTurnIndex = turnIndex, RootRunId = rootRunId,
            OutputsJson = "{}", CreatedDate = createdAt, CompletedAt = completedAt, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    [Fact]
    public async Task A_supervisor_turn_with_a_retry_surfaces_the_failed_original_and_a_retry_step()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Retry");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Do the work", resultSummary: "Done.");

        var failedAgent = Guid.NewGuid();
        var retryAgent = Guid.NewGuid();
        await SeedPlanDecisionAsync(teamId, run, "Sub 0");
        await SeedRetryScenarioAsync(teamId, run, failedAgent, retryAgent);

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        var cards = turn.Blocks.OfType<AgentGroupBlock>().SelectMany(g => g.Agents).ToList();
        cards.ShouldContain(c => c.AgentRunId == failedAgent && c.Status == nameof(AgentRunStatus.Failed), "the failed original is a Failed card in the initial group, not hidden behind its retry");
        cards.Count(c => c.AgentRunId == retryAgent).ShouldBe(1, "the retry agent renders EXACTLY once — as its own chronological card, never also lumped into the round group");

        // The retry's card is its OWN 'Retry' group, distinct from the initial-spawn group (chronological, not a lump).
        var retryGroup = turn.Blocks.OfType<AgentGroupBlock>().Single(g => g.Agents.Any(a => a.AgentRunId == retryAgent));
        retryGroup.Agents.ShouldHaveSingleItem().AgentRunId.ShouldBe(retryAgent);

        var retryStep = turn.Blocks.OfType<NarrativeStepBlock>().Single(s => s.Text == "Supervisor retried a subtask");
        retryStep.Detail.ShouldNotBeNull("the retry step carries the supervisor's structured rationale so a reader sees WHY");
        retryStep.Detail!.ShouldContain("The first attempt missed the edge cases.", customMessage: "the why");
        retryStep.Detail!.ShouldContain("attempt 1 failed its acceptance check.", customMessage: "the evidence");
    }

    /// <summary>Seed a supervisor turn that FAILED a subtask then RETRIED it: a spawn staging one FAILED agent for subtask "s0", then a retry staging a fresh SUCCEEDED agent — plus both AgentRun rows (ground-truth status). Flat plan (the tape path).</summary>
    private async Task SeedRetryScenarioAsync(Guid teamId, Guid runId, Guid failedAgent, Guid retryAgent)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtaskIds = new[] { "s0" } }),
            OutcomeJson = JsonSerializer.Serialize(new { agentCount = 1, agentRunIds = new[] { failedAgent } }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Retry, IdempotencyKey = $"retry:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtaskId = "s0", rationale = new { why = "The first attempt missed the edge cases.", evidence = "attempt 1 failed its acceptance check." } }),
            OutcomeJson = JsonSerializer.Serialize(new { agentCount = 1, agentRunIds = new[] { retryAgent } }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        db.AgentRun.Add(RetryAgentRow(teamId, runId, failedAgent, AgentRunStatus.Failed, now));
        db.AgentRun.Add(RetryAgentRow(teamId, runId, retryAgent, AgentRunStatus.Succeeded, now));

        await db.SaveChangesAsync();
    }

    private static AgentRun RetryAgentRow(Guid teamId, Guid runId, Guid agentId, AgentRunStatus status, DateTimeOffset now) => new()
    {
        Id = agentId, TeamId = teamId, WorkflowRunId = runId, NodeId = "sup", IterationKey = "sup",
        Harness = "codex-cli", Status = status, TaskJson = "{}",
        CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
    };

    [Fact]
    public async Task A_re_spawned_wave_and_the_deep_error_both_surface_in_the_room()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Investigate");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Investigate", resultSummary: null, status: WorkflowRunStatus.Failure);

        var (w1a, w1b, w2a, w2b) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await SeedRespawnScenarioAsync(teamId, run, (w1a, w1b), (w2a, w2b));

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        // (a) the SECOND spawn wave renders as its own group — not collapsed into the first (the authored group anchors wave 1).
        turn.Blocks.OfType<NarrativeStepBlock>().ShouldContain(s => s.Text == "Supervisor spawned 2 agents again", "the re-spawn wave Activity shows must render in the room too");

        var waveGroup = turn.Blocks.OfType<AgentGroupBlock>().Single(g => g.Agents.Any(a => a.AgentRunId == w2b));
        waveGroup.Agents.Select(a => a.AgentRunId).ShouldBe(new[] { w2a, w2b }, "the second wave shows exactly its own agents");
        waveGroup.Agents.Single(a => a.AgentRunId == w2b).Status.ShouldBe(nameof(AgentRunStatus.Failed), "the wave's FAILED agent is visible, as Activity shows");

        var allCards = turn.Blocks.OfType<AgentGroupBlock>().SelectMany(g => g.Agents).ToList();
        allCards.Count(a => a.AgentRunId == w2a).ShouldBe(1, "each re-spawned agent renders EXACTLY once — never also lumped into the authored group");
        allCards.ShouldContain(a => a.AgentRunId == w1a, "wave 1's agents stay anchored in the authored 'Investigate' group");

        // (b) the diagnostic surfaces the SPECIFIC deep error (node.failed), not the generic "Node 'sup' failed." run message.
        turn.Blocks.OfType<DiagnosticBlock>().ShouldHaveSingleItem()
            .Text.ShouldBe("OpenAI API error (no-status, Transient): the request timed out before the gateway responded");
    }

    /// <summary>Seed the user's failing scenario: a plan that grouped sa+sb into one authored "Investigate" phase, a FIRST spawn wave (both agents succeed), a SECOND spawn wave re-dispatching the same subtasks (one agent fails), plus the deep failure the engine wrote onto the node.failed ledger record (the run row's Error is only the generic node message).</summary>
    private async Task SeedRespawnScenarioAsync(Guid teamId, Guid runId, (Guid A, Guid B) wave1, (Guid A, Guid B) wave2)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        // Explicit sequence: plan(1) → wave-1 spawn(2) → wave-2 spawn(3) — the room reads the tape in sequence order,
        // and the wave-2 detection scopes to spawns AFTER the latest plan, so the plan must precede both spawns.
        db.SupervisorDecisionRecord.Add(SupDecision(teamId, runId, 1, SupervisorDecisionKinds.Plan, "{}",
            """{"planned":[],"count":2,"phases":[{"id":"inv","title":"Investigate","subtaskIds":["sa","sb"]}]}"""));

        db.SupervisorDecisionRecord.Add(SupDecision(teamId, runId, 2, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["sa","sb"]}""",
            JsonSerializer.Serialize(new { agentCount = 2, agentRunIds = new[] { wave1.A, wave1.B } })));
        db.SupervisorDecisionRecord.Add(SupDecision(teamId, runId, 3, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["sa","sb"]}""",
            JsonSerializer.Serialize(new { agentCount = 2, agentRunIds = new[] { wave2.A, wave2.B } })));

        db.AgentRun.Add(RetryAgentRow(teamId, runId, wave1.A, AgentRunStatus.Succeeded, now));
        db.AgentRun.Add(RetryAgentRow(teamId, runId, wave1.B, AgentRunStatus.Succeeded, now));
        db.AgentRun.Add(RetryAgentRow(teamId, runId, wave2.A, AgentRunStatus.Succeeded, now));
        db.AgentRun.Add(RetryAgentRow(teamId, runId, wave2.B, AgentRunStatus.Failed, now));

        db.WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = runId, RecordType = WorkflowRunRecordTypes.NodeFailed, NodeId = "sup", IterationKey = "", OccurredAt = now,
            PayloadJson = JsonSerializer.Serialize(new { error = "OpenAI API error (no-status, Transient): the request timed out before the gateway responded" }),
        });

        await db.SaveChangesAsync();
    }

    private static SupervisorDecisionRecord SupDecision(Guid teamId, Guid runId, long sequence, string kind, string payloadJson, string? outcomeJson) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
        DecisionKind = kind, IdempotencyKey = $"{kind}:{Guid.NewGuid():N}", InputHash = new string('0', 64),
        Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
        CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
    };

    [Fact]
    public async Task A_single_agent_run_surfaces_its_result_from_the_agent_output_even_without_a_supervisor()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Echo");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Print exactly: PONG", resultSummary: null);

        // A plain single-agent run: one agent node (surfaced by the ledger view) linked to its AgentRun, whose persisted
        // AgentRunResult carries the summary + a changed file. NO supervisor decisions — the tape is empty.
        await SeedAgentNodeAsync(teamId, run, summary: "Printed PONG.", changedFiles: new[] { "out.txt" });

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        var answer = turn.Blocks.OfType<FinalAnswerBlock>().Single();
        answer.Text.ShouldBe("Printed PONG.", "a single-agent run's RESULT is its own agent's summary — read from AgentRun.ResultJson, not a supervisor stop");
        answer.Attachments.ShouldContain(x => x.Label == "out.txt", "the agent's changed file rides the RESULT as an attachment");

        turn.Summary.ShouldBe("Printed PONG.", "the turn headline falls back to the sole agent's summary when there is no supervisor tape");
    }

    /// <summary>Seed a plain single-agent (non-supervisor) run: a node.started/completed ledger pair for the "agent" node (the workflow_run_node view surfaces it), the AgentRun wait that links the node to its run, and the AgentRun row whose persisted AgentRunResult carries the summary + changed files. No supervisor decisions.</summary>
    private async Task SeedAgentNodeAsync(Guid teamId, Guid runId, string summary, string[] changedFiles)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var agentId = Guid.NewGuid();

        db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = "node.started", NodeId = "agent", IterationKey = "", OccurredAt = now.AddSeconds(-5), PayloadJson = "{}" });
        db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = "node.completed", NodeId = "agent", IterationKey = "", OccurredAt = now, PayloadJson = "{}" });

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = runId, NodeId = "agent", IterationKey = "",
            WaitKind = WorkflowWaitKinds.AgentRun, Token = agentId.ToString(), WakeAt = now,
            Status = WorkflowWaitStatuses.Resolved, PayloadJson = "{}", CreatedAt = now,
        });

        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles };
        db.AgentRun.Add(new AgentRun
        {
            Id = agentId, TeamId = teamId, WorkflowRunId = runId, NodeId = "agent", IterationKey = "",
            Harness = "codex-cli", Status = AgentRunStatus.Succeeded, TaskJson = "{}",
            ResultJson = JsonSerializer.Serialize(result, AgentJson.Options),
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Turn_duration_anchors_on_created_date_so_a_resumed_run_reports_the_full_wall_clock()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Long run");

        // A resumed / re-dispatched run (recovered after a restart): StartedAt was reset to the FINAL leg — ~1 min before
        // completion — long after the run was created ~30 min earlier. The wall-clock must be CompletedAt − CreatedDate.
        var created = DateTimeOffset.UtcNow.AddMinutes(-30);
        var runId = await SeedTimedTurnAsync(teamId, sessionId, created, startedAt: created.AddMinutes(29), completedAt: created.AddMinutes(30));

        var room = await ProjectByRunAsync(runId, teamId);

        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single();
        turn.DurationMs.ShouldNotBeNull();
        turn.DurationMs!.Value.ShouldBeInRange(29 * 60_000L, 31 * 60_000L, "the full CompletedAt − CreatedDate (~30m), NOT CompletedAt − the reset StartedAt (~1m)");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Seed one completed turn run with EXPLICIT created / started / completed timestamps (to exercise the resume-safe wall-clock).</summary>
    private async Task<Guid> SeedTimedTurnAsync(Guid teamId, Guid sessionId, DateTimeOffset created, DateTimeOffset startedAt, DateTimeOffset completedAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal = "Long task" }),
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = created, VerifiedAt = created, NormalizedAt = created,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = 1,
            OutputsJson = JsonSerializer.Serialize(new { summary = "done", branch = (string?)null }),
            StartedAt = startedAt, CompletedAt = completedAt,
            CreatedDate = created, CreatedBy = SystemUsers.SeederId, LastModifiedDate = completedAt, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>Stamp a supervisor SPAWN decision whose folded agentResults carry per-agent changed-file paths, plus the matching AgentRun rows so the phase projection surfaces the agents (the Agents group folds them).</summary>
    private async Task SeedSpawnDecisionAsync(Guid teamId, Guid runId, params (Guid AgentRunId, string[] Files)[] agents)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        var outcome = JsonSerializer.Serialize(new
        {
            agentCount = agents.Length,
            agentRunIds = agents.Select(a => a.AgentRunId).ToArray(),
            agentResults = agents.Select(a => new { agentRunId = a.AgentRunId, status = "Succeeded", changedFiles = a.Files, summary = $"Edited {a.Files.Length} files" }).ToArray(),
        });

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        foreach (var (agentRunId, _) in agents)
            db.AgentRun.Add(new AgentRun
            {
                Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, NodeId = "sup", IterationKey = "sup",
                Harness = "codex-cli", Status = AgentRunStatus.Succeeded, TaskJson = "{}",
                CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
            });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_run_with_a_persisted_work_plan_projects_the_checklist_and_suppresses_the_plan_stat_rows()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, "Planned");
        var run = await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "Do the thing", resultSummary: "Shipped it.");

        await SeedPlanDecisionAsync(teamId, run, "Trace DI registration", "Analyze the template store");

        // The durable plan artifact (what the S1 supervisor writer persists) — the checklist's contract source.
        using (var seed = _fixture.BeginScope())
        {
            await seed.Resolve<IWorkPlanService>().SaveVersionAsync(new WorkPlanDraft
            {
                TeamId = teamId,
                WorkflowRunId = run,
                OriginKind = WorkPlanOrigins.Supervisor,
                OriginKey = "sup#turn0",
                Goal = "Do the thing",
                Items = new[]
                {
                    new WorkPlanItem { Id = "s1", Title = "Trace DI registration", Instruction = "trace it" },
                    new WorkPlanItem { Id = "s2", Title = "Analyze the template store", Instruction = "analyze it", DependsOn = new[] { "s1" } },
                },
            }, CancellationToken.None);
        }

        var room = await ProjectByRunAsync(run, teamId);
        var turn = room!.Blocks.OfType<AssistantTurnBlock>().Single(t => t.TurnIndex == 1);

        var checklist = turn.Blocks.OfType<PlanChecklistBlock>().Single();
        checklist.Version.ShouldBe(1);
        checklist.Items.Select(i => i.Title).ShouldBe(new[] { "Trace DI registration", "Analyze the template store" });
        checklist.Items[1].DependsOn.ShouldBe(new[] { 1 }, "the dependency id resolves to the 1-based ordinal");
        checklist.Items.ShouldAllBe(i => i.State == WorkPlanItemStates.Pending, "the fabricated tape staged no agents — honestly pending");

        turn.Blocks.OfType<StatBlock>().Where(b => b.Kind == "subtasks").ShouldBeEmpty("the checklist subsumes the per-round plan rows");
    }

    /// <summary>Stamp a supervisor PLAN decision (its subtask decomposition) onto a run's tape — enough for the canonical map + the subtasks stat row.</summary>
    private async Task SeedPlanDecisionAsync(Guid teamId, Guid runId, params string[] subtasks)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var payload = JsonSerializer.Serialize(new { subtasks = subtasks.Select((t, i) => new { id = $"s{i}", title = t, instruction = "do it" }).ToArray() });

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = SupervisorDecisionKinds.Plan, IdempotencyKey = $"plan:{Guid.NewGuid():N}", InputHash = new string('0', 64),
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = "{}",
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }

    private async Task<RoomView?> ProjectByRunAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRoomProjector>().ProjectByRunAsync(runId, teamId, CancellationToken.None);
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

    private async Task<Guid> SeedTurnAsync(Guid teamId, Guid sessionId, int turn, string goal, string? resultSummary, WorkflowRunStatus status = WorkflowRunStatus.Success)
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
            Status = status, SessionId = sessionId, SessionTurnIndex = turn,
            OutputsJson = outputs,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>Park a node-grain pending decision (a flow.decision wait) on an existing run, with its stashed envelope.</summary>
    private async Task SeedNodeDecisionAsync(Guid teamId, Guid runId, string question, DateTimeOffset deadline, IReadOnlyList<DecisionOption> options)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var envelope = Envelope(question, deadline, DecisionResumeBackends.WorkflowWait, agentRunId: null, workflowRunId: runId, nodeId: "decide", options);

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = runId, NodeId = "decide", IterationKey = string.Empty,
            WaitKind = WorkflowWaitKinds.Decision, Token = Guid.NewGuid().ToString("N"), WakeAt = deadline,
            Status = WorkflowWaitStatuses.Pending, PayloadJson = JsonSerializer.Serialize(envelope, Json),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Park an agent-grain pending decision (a decision.request tool-ledger row) on a real agent run of <paramref name="runId"/>.</summary>
    private async Task SeedAgentDecisionAsync(Guid teamId, Guid runId, string question, DateTimeOffset deadline)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var agentId = Guid.NewGuid();
        db.AgentRun.Add(new AgentRun
        {
            Id = agentId, TeamId = teamId, WorkflowRunId = runId, Harness = "codex-cli",
            Status = AgentRunStatus.Running, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();   // commit the agent before the ledger row

        var ledgerId = Guid.NewGuid();
        var envelope = Envelope(question, deadline, DecisionResumeBackends.ToolLedger, agentRunId: agentId, workflowRunId: null, nodeId: null, Array.Empty<DecisionOption>());

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = ledgerId, TeamId = teamId, AgentRunId = agentId, ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision.request:{ledgerId:N}", InputHash = new string('0', 64),
            Status = ToolCallLedgerStatus.AwaitingApproval, ApprovalDeadlineAt = deadline,
            DecisionEnvelopeJson = JsonSerializer.Serialize(envelope, Json),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }

    private static DecisionRequest Envelope(string question, DateTimeOffset deadline, string grain, Guid? agentRunId, Guid? workflowRunId, string? nodeId, IReadOnlyList<DecisionOption> options) => new()
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
        Options = options,
        RecommendedOption = options.Count > 0 ? options[0].Id : null,
        BlockingReason = "needs a human",
        RiskLevel = DecisionRiskLevels.High,
        Policy = DecisionPolicies.HumanRequired,
        TimeoutAt = deadline,
        DedupeKey = Guid.NewGuid().ToString("N"),
        ResumeBackend = grain,
    };

    /// <summary>Append N ledger records to a run (Sequence is the DB BIGSERIAL) and return the resulting MAX — the watermark.</summary>
    private async Task<long> SeedRecordsAsync(Guid runId, int count)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        for (var i = 0; i < count; i++)
            db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = "log", PayloadJson = "{}" });

        await db.SaveChangesAsync();

        return await db.WorkflowRunRecord.Where(r => r.RunId == runId).MaxAsync(r => r.Sequence);
    }
}

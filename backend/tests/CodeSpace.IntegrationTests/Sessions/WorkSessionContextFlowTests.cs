using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Tasks;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// S4 — the VALUE slice: a CONTINUE primes the next run with the thread's prior-turn digest, folded into the agent's
/// prompt, so a follow-up builds on earlier work instead of starting cold. Proven against real Postgres:
///   (a) <see cref="ISessionContextBuilder"/> renders prior top-level turns (goal + status + result summary + branch)
///       chronologically, bounds to the most recent N, and excludes inherited null-turn (child/replay) runs;
///   (b) a CONTINUE folds that digest into the next run's frozen agent-code goal (the prompt) — context propagation;
///   (c) a FRESH launch's agent goal is the clean goal (byte-identical — the grounding seam is inert when not continuing);
///   (d) a REAL multi-turn: turn 1 runs through the real engine + fake CLI producing a real summary, and continuing
///       carries that EXACT summary forward into turn 2's agent prompt.
///
/// <para>Tier: high-fidelity Integration — the real launch service + context builder over real Postgres; the
/// multi-turn test additionally drives the real engine + executor + LocalProcessRunner + fake CLI.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkSessionContextFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkSessionContextFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Builder_renders_prior_turns_chronologically_with_goal_and_result()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCompletedTurnAsync(teamId, sessionId, turn: 1, goal: "Add retry backoff", summary: "Added exponential backoff", branch: "run-1/api");
        await SeedCompletedTurnAsync(teamId, sessionId, turn: 2, goal: "Add jitter", summary: "Added 20% jitter", branch: "run-2/api");

        var context = await BuildContextAsync(sessionId, teamId);

        context.ShouldNotBeNull();
        context!.ShouldContain("Add retry backoff");
        context.ShouldContain("Added exponential backoff");
        context.ShouldContain("run-2/api", customMessage: "the produced branch is surfaced for continuity");
        context.IndexOf("Turn 1", StringComparison.Ordinal)
            .ShouldBeLessThan(context.IndexOf("Turn 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Builder_notes_the_summary_is_stale_when_a_prior_distillation_failed()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCompletedTurnAsync(teamId, sessionId, turn: 1, goal: "Old work", summary: "DISTILLED_OLDER_WORK", branch: null);
        await StampSummaryAsync(sessionId, summary: "DISTILLED_OLDER_WORK", throughTurn: 1, staleSinceTurn: 2);

        var context = await BuildContextAsync(sessionId, teamId);

        context.ShouldNotBeNull();
        context!.ShouldContain("DISTILLED_OLDER_WORK");
        context.ShouldContain("have not yet been folded", customMessage: "a fail-open distillation gap must never be silently read as a current, complete summary");
    }

    [Fact]
    public async Task Builder_shows_no_staleness_note_when_the_summary_is_fully_caught_up()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCompletedTurnAsync(teamId, sessionId, turn: 1, goal: "Old work", summary: "DISTILLED_OLDER_WORK", branch: null);
        await StampSummaryAsync(sessionId, summary: "DISTILLED_OLDER_WORK", throughTurn: 1, staleSinceTurn: null);

        var context = await BuildContextAsync(sessionId, teamId);

        context!.ShouldNotContain("have not yet been folded", customMessage: "byte-identical to before this field existed when the summary is fully caught up");
    }

    [Fact]
    public async Task Builder_prefers_the_manifest_branch_over_a_conflicting_raw_OutputsJson_branch()
    {
        // I2: the digest's "Produced branch" must read the SAME source of truth as the room/continuity paths — a
        // PublishManifest row wins over a disagreeing raw OutputsJson.branch guess, so the branch the agent is TOLD it
        // produced never disagrees with the branch it actually cloned from on the next turn.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        var repoId = await SeedRepositoryAsync(teamId);
        await SeedTurnWithManifestAsync(teamId, sessionId, turn: 1, repoId, goal: "Add retry backoff", summary: "Added exponential backoff",
            outputsBranch: "stale-guessed-branch", manifestBranch: "codespace/agent/real");

        var context = await BuildContextAsync(sessionId, teamId);

        context.ShouldNotBeNull();
        context!.ShouldContain("Produced branch: codespace/agent/real");
        context.ShouldNotContain("stale-guessed-branch", customMessage: "the manifest wins — the stale raw guess must never surface");
    }

    [Fact]
    public async Task Builder_shows_the_rerun_winners_result_and_branch_not_the_failed_originals()
    {
        // Rerun-aware session reads (S4 fold): the digest's "Result" / "Produced branch" for a reran turn must come
        // from the SUCCEEDED rerun, never the superseded failed original — even though the original is the turn's
        // first/oldest attempt.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        var repoId = await SeedRepositoryAsync(teamId);

        await SeedRerunWinningTurnAsync(teamId, sessionId, turn: 1, repoId, goal: "Ship the feature",
            originalSummary: "ORIGINAL_ATTEMPT_CRASHED", rerunSummary: "RERUN_FIXED_IT", rerunBranch: "codespace/agent/rerun-fixed");

        var context = await BuildContextAsync(sessionId, teamId);

        context.ShouldNotBeNull();
        context!.ShouldContain("RERUN_FIXED_IT", customMessage: "the rerun's own result is the turn's effective outcome");
        context.ShouldContain("Produced branch: codespace/agent/rerun-fixed");
        context.ShouldNotContain("ORIGINAL_ATTEMPT_CRASHED", customMessage: "the failed original's result must never surface once a rerun succeeded");
    }

    [Fact]
    public async Task Builder_surfaces_the_result_for_every_projection_shape()
    {
        // The result key differs by projection: single-agent → summary, plan-map → combined, supervisor → reason.
        // The digest must carry each turn's OUTCOME forward, not just its goal, whatever projection produced it.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "single", JsonSerializer.Serialize(new { summary = "SINGLE_AGENT_RESULT" }));
        await SeedTurnAsync(teamId, sessionId, turn: 2, goal: "map", JsonSerializer.Serialize(new { combined = "PLAN_MAP_RESULT" }));
        await SeedTurnAsync(teamId, sessionId, turn: 3, goal: "deep", JsonSerializer.Serialize(new { status = "ok", decision = "stop", reason = "SUPERVISOR_RESULT" }));

        var context = await BuildContextAsync(sessionId, teamId);

        context.ShouldNotBeNull();
        context!.ShouldContain("SINGLE_AGENT_RESULT");
        context.ShouldContain("PLAN_MAP_RESULT", customMessage: "a plan-map prior turn's result (combined) must carry forward");
        context.ShouldContain("SUPERVISOR_RESULT", customMessage: "a supervisor prior turn's result (reason) must carry forward");
    }

    [Fact]
    public async Task Builder_clips_an_oversized_result()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        var huge = new string('Z', SessionTurnText.MaxResultChars + 500);
        await SeedCompletedTurnAsync(teamId, sessionId, turn: 1, goal: "big", summary: huge, branch: null);

        var context = await BuildContextAsync(sessionId, teamId);

        context.ShouldNotBeNull();
        context!.ShouldContain("…", customMessage: "an oversized result is clipped so one verbose turn can't blow up the prompt");
        context.ShouldNotContain(huge, customMessage: "the full untruncated result must not be present");
    }

    [Fact]
    public async Task Builder_is_team_scoped_and_ignores_another_teams_session()
    {
        // Defence in depth: even handed a real session id, the builder only reads runs of the CALLING team.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCompletedTurnAsync(teamId, sessionId, turn: 1, goal: "real", summary: "OWNED_BY_TEAM_A", branch: null);

        (await BuildContextAsync(sessionId, otherTeamId))
            .ShouldBeNull("a foreign team must see no turns of this session — the builder is team-scoped");
    }

    [Fact]
    public async Task Builder_returns_null_for_a_session_with_no_turns()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);

        (await BuildContextAsync(sessionId, teamId)).ShouldBeNull("nothing to carry forward yet");
    }

    [Fact]
    public async Task Builder_excludes_an_inherited_null_turn_run()
    {
        // Correction 1: a child / replay inherits the session with a NULL turn index — it is NOT a user-visible turn,
        // so its result must not appear in the thread digest.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedCompletedTurnAsync(teamId, sessionId, turn: 1, goal: "Real turn", summary: "TOP_LEVEL_SUMMARY", branch: null);
        await SeedCompletedTurnAsync(teamId, sessionId, turn: null, goal: "Replay", summary: "CHILD_SUMMARY_HIDDEN", branch: null);

        var context = await BuildContextAsync(sessionId, teamId);

        context.ShouldNotBeNull();
        context!.ShouldContain("TOP_LEVEL_SUMMARY");
        context.ShouldNotContain("CHILD_SUMMARY_HIDDEN", customMessage: "an inherited null-turn run must never enter the digest");
    }

    [Fact]
    public async Task Builder_bounds_to_the_most_recent_turns()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);

        // One more than the cap; the very first turn must roll off, the most recent must be present.
        var total = SessionContextBuilder.MaxTurns + 1;
        for (var turn = 1; turn <= total; turn++)
            await SeedCompletedTurnAsync(teamId, sessionId, turn, goal: $"Goal {turn}", summary: $"SUMMARY_{turn}", branch: null);

        var context = await BuildContextAsync(sessionId, teamId);

        context.ShouldNotBeNull();
        context!.ShouldNotContain("SUMMARY_1", customMessage: "the oldest turn rolls off the bounded window");
        context.ShouldContain($"SUMMARY_{total}", customMessage: "the most recent turn is always present");
    }

    [Fact]
    public async Task Continue_folds_the_prior_turn_digest_into_the_next_runs_agent_goal()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var sessionId = await SeedSessionAsync(teamId);
        await SeedCompletedTurnAsync(teamId, sessionId, turn: 1, goal: "Add the retry backoff", summary: "PRIOR_WORK_DID_THE_BACKOFF", branch: "run-1/api");

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, "Now add jitter on top"));

        var agentGoal = await ReadAgentGoalAsync(result.RunId);
        agentGoal.ShouldContain("PRIOR_WORK_DID_THE_BACKOFF", customMessage: "the prior turn's result reached the continuing agent's prompt — context propagated");
        agentGoal.ShouldContain("Now add jitter on top", customMessage: "the new follow-up task is still the goal");
        agentGoal.ShouldContain("do not start over", customMessage: "the agent is told to build on prior work");
    }

    [Fact]
    public async Task Continue_carries_a_clean_display_title_even_though_the_goal_is_grounding_prefixed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var sessionId = await SeedSessionAsync(teamId);
        await SeedCompletedTurnAsync(teamId, sessionId, turn: 1, goal: "Add the retry backoff", summary: "PRIOR_WORK_DID_THE_BACKOFF", branch: "run-1/api");

        var result = await LaunchAsync(ContinueRequest(teamId, userId, sessionId, "Now add jitter on top"));

        var agentGoal = await ReadAgentGoalAsync(result.RunId);
        agentGoal.ShouldContain("Earlier turns", customMessage: "sanity: this run's goal really is grounding-prefixed");

        (await ReadAgentDisplayTitleAsync(result.RunId)).ShouldBe("Now add jitter on top",
            "the card's title is the clean follow-up text, never the grounding digest heading — the mechanism that used to leak into AgentMetricsReader.DeriveTitle");
    }

    [Fact]
    public async Task A_fresh_launch_agent_goal_is_the_clean_goal_byte_identical()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var _pauseExec = PauseAutoExecute();

        var result = await LaunchAsync(BasicChatRequest(teamId, userId, "Just do this one thing"));

        var agentGoal = await ReadAgentGoalAsync(result.RunId);
        agentGoal.ShouldBe("Just do this one thing", "a fresh launch injects no grounding — the agent goal is exactly the task");
    }

    [Fact]
    public async Task Real_multi_turn_carries_the_first_turns_real_summary_into_the_next_agent_prompt()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the agent.code suspend runs the REAL executor + runner + fake CLI

        // Turn 1: a real launch that actually executes to a real summary.
        const string firstGoal = "Work on the auth refactor";
        var first = await LaunchAsync(new TaskLaunchRequest
        {
            TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = firstGoal, RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
            Overrides = new TaskExecutionOverrides { Harness = "codex-cli", RunnerKind = "local" },
        });

        await RunEngineAsync(first.RunId);
        await jobClient.WaitForPendingAsync();

        var firstRun = await LoadRunAsync(first.RunId);
        firstRun.Status.ShouldBe(WorkflowRunStatus.Success, "turn 1 must complete so its real summary exists to carry forward");
        var realSummary = SubtaskAwareFakeCli.ExpectedSummaryFor(firstGoal);
        firstRun.OutputsJson.ShouldContain(realSummary, customMessage: "turn 1's real result is surfaced on its OutputsJson");

        // Turn 2: continue — its frozen agent prompt (set at staging) must carry turn 1's REAL summary forward.
        var second = await LaunchAsync(ContinueRequest(teamId, userId, first.SessionId, "Now also rotate the tokens"));
        await jobClient.WaitForPendingAsync();   // drain turn 2 too, so no run job leaks into the shared queue

        second.SessionId.ShouldBe(first.SessionId);
        var secondAgentGoal = await ReadAgentGoalAsync(second.RunId);
        secondAgentGoal.ShouldContain(realSummary,
            customMessage: "turn 2's agent prompt must carry turn 1's REAL produced summary forward — end-to-end multi-turn context propagation");
        secondAgentGoal.ShouldContain("Now also rotate the tokens", customMessage: "and still address the new follow-up");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static TaskLaunchRequest BasicChatRequest(Guid teamId, Guid userId, string text) => new()
    {
        TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = text, RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
    };

    private static TaskLaunchRequest ContinueRequest(Guid teamId, Guid userId, Guid sessionId, string text) => new()
    {
        TeamId = teamId, ActorUserId = userId, SurfaceKind = TaskLaunchSurfaceKinds.Chat,
        TaskText = text, ContinueSessionId = sessionId, RequestedEffort = TaskEffortModes.Quick, Autonomy = "Confined",
    };

    private async Task<string?> BuildContextAsync(Guid sessionId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISessionContextBuilder>().BuildAsync(sessionId, teamId, CancellationToken.None);
    }

    private async Task<LaunchTaskResult> LaunchAsync(TaskLaunchRequest request)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITaskLaunchService>().LaunchAsync(request, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<Core.Services.Workflows.Engine.IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    /// <summary>Reads the projected agent.code node's <c>goal</c> (the agent prompt) out of the frozen definition snapshot.</summary>
    private async Task<string> ReadAgentGoalAsync(Guid runId)
    {
        var run = await LoadRunAsync(runId);
        run.DefinitionSnapshotJson.ShouldNotBeNull("a launched task is a snapshot run with an inline frozen definition");

        var root = JsonDocument.Parse(run.DefinitionSnapshotJson!).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");
        return agent.GetProperty("config").GetProperty("goal").GetString()!;
    }

    /// <summary>Reads the frozen agent.code node's <c>displayTitle</c> config key — the CLEAN task text a card derives its title from, distinct from <c>goal</c> which a CONTINUE prepends the session grounding to.</summary>
    private async Task<string?> ReadAgentDisplayTitleAsync(Guid runId)
    {
        var run = await LoadRunAsync(runId);
        var root = JsonDocument.Parse(run.DefinitionSnapshotJson!).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");
        return agent.GetProperty("config").TryGetProperty("displayTitle", out var v) ? v.GetString() : null;
    }

    private async Task<Guid> SeedSessionAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.CreateVersion7();
        db.WorkSession.Add(new WorkSession { Id = id, TeamId = teamId, Title = "thread", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Stamp a session's rolling summary + watermark + staleness marker directly — bypassing the real SessionSummarizer distillation, so a test can set up a "fail-open left a gap" state cheaply.</summary>
    private async Task StampSummaryAsync(Guid sessionId, string summary, int throughTurn, int? staleSinceTurn)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var session = await db.WorkSession.SingleAsync(s => s.Id == sessionId);
        session.Summary = summary;
        session.SummaryThroughTurnIndex = throughTurn;
        session.SummaryStaleSinceTurn = staleSinceTurn;

        await db.SaveChangesAsync();
    }

    /// <summary>Stage a finished single-agent-shape turn — a request carrying the goal payload + a run with {summary, branch} on OutputsJson.</summary>
    private Task SeedCompletedTurnAsync(Guid teamId, Guid sessionId, int? turn, string goal, string summary, string? branch) =>
        SeedTurnAsync(teamId, sessionId, turn, goal, JsonSerializer.Serialize(new { summary, branch }));

    /// <summary>Stage a finished turn whose OutputsJson disagrees with its authoritative PublishManifest row — proves I2's "manifest wins" applies to the digest, not just session continuity.</summary>
    private async Task SeedTurnWithManifestAsync(Guid teamId, Guid sessionId, int turn, Guid repoId, string goal, string summary, string outputsBranch, string manifestBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

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
            OutputsJson = JsonSerializer.Serialize(new { summary, branch = outputsBranch }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, WorkflowRunId = runId, RepositoryAlias = "primary",
            RepositoryId = repoId, Branch = manifestBranch, PublishStateValue = PublishState.Pushed,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instanceId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "GH", BaseUrl = $"https://gh-{suffix}.local", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = $"acme/api-{suffix}", WebUrl = "https://gh.local/acme/api", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return repoId;
    }

    /// <summary>
    /// Stage a turn whose ORIGINAL attempt FAILED and whose RERUN (real <c>RootRunId</c> lineage, later
    /// <c>CreatedDate</c>) SUCCEEDED with its own pushed <see cref="PublishManifest"/> branch — the shape a real
    /// rerun-after-failure leaves for the digest to read.
    /// </summary>
    private async Task SeedRerunWinningTurnAsync(Guid teamId, Guid sessionId, int turn, Guid repoId, string goal, string originalSummary, string rerunSummary, string rerunBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var originalRequestId = Guid.NewGuid();
        var rerunRequestId = Guid.NewGuid();
        var originalId = Guid.NewGuid();
        var rerunId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.AddRange(
            new WorkflowRunRequest { Id = originalRequestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal }), Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now },
            new WorkflowRunRequest { Id = rerunRequestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Rerun, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now });

        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = originalId, TeamId = teamId, RunRequestId = originalRequestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Failure, SessionId = sessionId, SessionTurnIndex = turn,
            OutputsJson = JsonSerializer.Serialize(new { summary = originalSummary }),
            CreatedDate = now.AddMinutes(-5), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = rerunId, TeamId = teamId, RunRequestId = rerunRequestId, SourceType = WorkflowRunSourceTypes.Rerun,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = null, RootRunId = originalId, RerunFromNodeId = "agent",
            OutputsJson = JsonSerializer.Serialize(new { summary = rerunSummary }),
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, WorkflowRunId = rerunId, RepositoryAlias = "primary",
            RepositoryId = repoId, Branch = rerunBranch, PublishStateValue = PublishState.Pushed,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Stage a finished turn with an arbitrary OutputsJson shape (a projection other than single-agent surfaces different result keys).</summary>
    private async Task SeedTurnAsync(Guid teamId, Guid sessionId, int? turn, string goal, string outputsJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal }),
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = Guid.NewGuid(), TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = turn,
            OutputsJson = outputsJson,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    private IDisposable PauseAutoExecute()
    {
        SetAutoExecute(clearFirst: true, value: false);
        return new Restore(this);
    }

    private void SetAutoExecute(bool clearFirst, bool value)
    {
        using var scope = _fixture.BeginScope();
        var jobClient = scope.Resolve<InMemoryBackgroundJobClient>();
        if (clearFirst) jobClient.Clear();
        jobClient.AutoExecute = value;
    }

    private sealed class Restore : IDisposable
    {
        private readonly WorkSessionContextFlowTests _owner;
        public Restore(WorkSessionContextFlowTests owner) { _owner = owner; }
        public void Dispose() => _owner.SetAutoExecute(clearFirst: false, value: true);   // restore the collection's default
    }
}

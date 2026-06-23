using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// Rolling session summary (long-thread memory) against real Postgres: the <see cref="SessionSummarizer"/> folds turns
/// that scroll out of the recent verbatim window (SessionContextBuilder.MaxTurns) into <c>WorkSession.Summary</c> via a
/// (faked) LLM distillation + advances the watermark, and <see cref="SessionContextBuilder"/> prepends that distilled
/// block before the recent window. Proven: fold + watermark; short-thread no-op (byte-identical); incremental fold of
/// only the newly scrolled-out turns; fail-open when the team has no pool model; the digest prefix + byte-identity; and
/// the end-to-end launch wiring (a continue folds + the frozen agent goal carries the summary).
///
/// <para>The LLM is faked (a capturing client) + the model pool is seeded so SelectAsync returns a pick — so the
/// fold/persist/watermark + prompt-shaping logic runs against real PG without a live key. Summary QUALITY under a real
/// model is the gated RealModel eval.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkSessionSummaryFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkSessionSummaryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const int Window = SessionContextBuilder.MaxTurns;   // 8 — the recent verbatim window

    [Fact]
    public async Task Summarizer_folds_older_turns_into_the_summary_and_advances_the_watermark()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);

        // 12 turns ⇒ turns 1..4 (latest 12 − window 8) sit older than the recent window → folded into the summary.
        for (var t = 1; t <= 12; t++) await SeedTurnAsync(teamId, sessionId, t, $"goal-{t}", $"result-{t}");

        var capture = new CapturingLlmClient { Return = "DISTILLED OLDER TURNS" };
        await RunSummarizerAsync(teamId, sessionId, capture);

        var session = await LoadSessionAsync(sessionId);
        session.Summary.ShouldBe("DISTILLED OLDER TURNS", "the LLM-distilled text is persisted on the session");
        session.SummaryThroughTurnIndex.ShouldBe(4, "the watermark advances to latest(12) − window(8) = 4");

        capture.LastUserPrompt.ShouldNotBeNull();
        capture.LastUserPrompt!.ShouldContain("goal-1", Case.Sensitive, "the oldest scrolled-out turn is in the distillation input");
        capture.LastUserPrompt.ShouldContain("goal-4", Case.Sensitive, "through the watermark turn");
        capture.LastUserPrompt.ShouldNotContain("goal-5", Case.Sensitive, "a turn still inside the recent window is NOT folded");
    }

    [Fact]
    public async Task Summarizer_is_a_no_op_for_a_thread_within_the_window()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);

        for (var t = 1; t <= Window; t++) await SeedTurnAsync(teamId, sessionId, t, $"goal-{t}", $"result-{t}");   // exactly the window — nothing older

        var capture = new CapturingLlmClient();
        await RunSummarizerAsync(teamId, sessionId, capture);

        var session = await LoadSessionAsync(sessionId);
        session.Summary.ShouldBeNull("a thread within the recent window needs no summary — byte-identical to pre-summary");
        session.SummaryThroughTurnIndex.ShouldBeNull();
        capture.Called.ShouldBeFalse("no LLM call when there is nothing to fold");
    }

    [Fact]
    public async Task Summarizer_folds_incrementally_only_the_newly_scrolled_out_turns()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);

        // First fold at 10 turns → watermark 2 (turns 1..2 folded).
        for (var t = 1; t <= 10; t++) await SeedTurnAsync(teamId, sessionId, t, $"goal-{t}", $"result-{t}");
        await RunSummarizerAsync(teamId, sessionId, new CapturingLlmClient { Return = "SUMMARY THROUGH 2" });
        (await LoadSessionAsync(sessionId)).SummaryThroughTurnIndex.ShouldBe(2);

        // One more turn → 11 turns → watermark should advance to 3, folding ONLY turn 3 (turns 1..2 are already summarised).
        await SeedTurnAsync(teamId, sessionId, 11, "goal-11", "result-11");
        var second = new CapturingLlmClient { Return = "SUMMARY THROUGH 3" };
        await RunSummarizerAsync(teamId, sessionId, second);

        var session = await LoadSessionAsync(sessionId);
        session.SummaryThroughTurnIndex.ShouldBe(3, "incremental — the watermark advances by one to 3");
        session.Summary.ShouldBe("SUMMARY THROUGH 3");

        second.LastUserPrompt!.ShouldContain("SUMMARY THROUGH 2", Case.Sensitive, "the existing summary is the base of the next distillation");
        second.LastUserPrompt.ShouldContain("goal-3", Case.Sensitive, "the newly scrolled-out turn is folded");
        second.LastUserPrompt.ShouldNotContain("goal-1", Case.Sensitive, "already-summarised turns are NOT re-folded (incremental, not a full re-summarize)");
    }

    [Fact]
    public async Task Summarizer_fails_open_when_the_team_has_no_pool_model()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        // NO SeedCredentialedModelAsync — the team's pool is empty.
        var sessionId = await SeedSessionAsync(teamId);
        for (var t = 1; t <= 12; t++) await SeedTurnAsync(teamId, sessionId, t, $"goal-{t}", $"result-{t}");

        var capture = new CapturingLlmClient();
        await Should.NotThrowAsync(() => RunSummarizerAsync(teamId, sessionId, capture));

        var session = await LoadSessionAsync(sessionId);
        session.Summary.ShouldBeNull("no credentialed model in the team's pool → fail-open, the summary is left unchanged");
        session.SummaryThroughTurnIndex.ShouldBeNull();
        capture.Called.ShouldBeFalse("a missing pool model short-circuits before the LLM call");
    }

    [Fact]
    public async Task Summarizer_fails_open_when_the_llm_call_throws()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");   // a model resolves, but the call throws
        var sessionId = await SeedSessionAsync(teamId);
        for (var t = 1; t <= 12; t++) await SeedTurnAsync(teamId, sessionId, t, $"goal-{t}", $"result-{t}");

        await Should.NotThrowAsync(() => RunSummarizerAsync(teamId, sessionId, new ThrowingLlmClient()));

        var session = await LoadSessionAsync(sessionId);
        session.Summary.ShouldBeNull("an LLM failure is caught → the summary is left unchanged, the launch proceeds");
        session.SummaryThroughTurnIndex.ShouldBeNull();
    }

    [Fact]
    public async Task Summarizer_fails_open_when_model_resolution_throws()
    {
        // Model resolution DECRYPTS the credential — a corrupt/rotated key throws there (outside the original try). The
        // whole resolve→select→complete path is now guarded, so a resolution fault leaves the summary + never throws.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        for (var t = 1; t <= 12; t++) await SeedTurnAsync(teamId, sessionId, t, $"goal-{t}", $"result-{t}");

        await Should.NotThrowAsync(() => RunSummarizerAsync(teamId, sessionId, new CapturingLlmClient(), new ThrowingSelector()));

        var session = await LoadSessionAsync(sessionId);
        session.Summary.ShouldBeNull("a model-resolution (credential-decrypt) fault fails open — never breaks the launch");
        session.SummaryThroughTurnIndex.ShouldBeNull();
    }

    [Fact]
    public async Task Summarizer_uses_a_row_based_watermark_for_non_contiguous_turn_indices()
    {
        // Turn indices can be non-contiguous (e.g. a rejected/dedup'd continue leaves a gap). The watermark is ROW-based
        // (all but the most recent MaxTurns rows), NOT value-based (latest − MaxTurns) — so it stays complementary to
        // BuildAsync's count-based window. 10 turns at gapped indices ⇒ Skip(8) leaves the two OLDEST → watermark = 2,
        // NOT 27 − 8 = 19 (which would wrongly fold turns 5..18 that are still in the recent 8-row window — double-count).
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);
        foreach (var t in new[] { 1, 2, 5, 9, 12, 15, 18, 21, 24, 27 }) await SeedTurnAsync(teamId, sessionId, t, $"goal-{t}", $"result-{t}");

        var capture = new CapturingLlmClient();
        await RunSummarizerAsync(teamId, sessionId, capture);

        var session = await LoadSessionAsync(sessionId);
        session.SummaryThroughTurnIndex.ShouldBe(2, "row-based: the two oldest of the 10 gapped turns scrolled out of the recent 8 — NOT the value-based 27−8=19");
        capture.LastUserPrompt!.ShouldContain("goal-1", Case.Sensitive);
        capture.LastUserPrompt.ShouldContain("goal-2", Case.Sensitive);
        capture.LastUserPrompt.ShouldNotContain("goal-5", Case.Sensitive, "turn 5 is still inside the recent 8-row window — it must NOT be folded (no double-count)");
    }

    [Fact]
    public async Task BuildAsync_prepends_the_distilled_summary_before_the_recent_window()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        for (var t = 1; t <= 10; t++) await SeedTurnAsync(teamId, sessionId, t, $"goal-{t}", $"result-{t}");
        await SetSummaryAsync(sessionId, "EARLIER WORK: did goals 1 and 2.", throughTurn: 2);

        var digest = await BuildDigestAsync(sessionId, teamId);

        digest.ShouldNotBeNull();
        digest!.ShouldContain("Summary of earlier work", Case.Sensitive, "the distilled block is labelled");
        digest.ShouldContain("EARLIER WORK: did goals 1 and 2.", Case.Sensitive, "the summary text is prepended");
        digest.ShouldContain("goal-10", Case.Sensitive, "the recent window is still rendered verbatim after the summary");
        digest.IndexOf("EARLIER WORK", StringComparison.Ordinal).ShouldBeLessThan(digest.IndexOf("goal-10", StringComparison.Ordinal), "the older distilled summary comes BEFORE the recent turns");
    }

    [Fact]
    public async Task BuildAsync_is_byte_identical_when_there_is_no_summary()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        for (var t = 1; t <= 3; t++) await SeedTurnAsync(teamId, sessionId, t, $"goal-{t}", $"result-{t}");

        var digest = await BuildDigestAsync(sessionId, teamId);

        digest.ShouldNotBeNull();
        digest!.ShouldNotContain("Summary of earlier work", Case.Sensitive, "a thread with no summary emits NO summary block — byte-identical to the pre-summary digest");
        digest.ShouldContain("goal-1", Case.Sensitive);
    }

    [Fact]
    public async Task A_continue_past_the_window_folds_the_summary_and_carries_it_into_the_launched_agent_goal()
    {
        // End-to-end through the REAL ITaskLaunchService: a continue on a >window thread runs the summarizer (fed a
        // scoped fake LLM registry) then BuildAsync, and the distilled summary lands in the frozen agent.code goal via
        // ComposeGoal — proving TaskLaunchService → summarizer → digest → projection wires up + commits atomically.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);
        for (var t = 1; t <= 9; t++) await SeedTurnAsync(teamId, sessionId, t, $"goal-{t}", $"result-{t}");   // 9 turns ⇒ turn 1 scrolls out

        var fakeRegistry = new LLMClientRegistry(new ILLMClient[] { new CapturingLlmClient { Return = "DISTILLED THREAD MEMORY" } });

        using var pause = PauseAutoExecute();
        using var scope = _fixture.BeginScope(b => b.RegisterInstance(fakeRegistry).As<ILLMClientRegistry>());

        var result = await scope.Resolve<Core.Services.Tasks.ITaskLaunchService>().LaunchAsync(new Messages.Tasks.TaskLaunchRequest
        {
            TeamId = teamId, ActorUserId = userId, SurfaceKind = Messages.Tasks.TaskLaunchSurfaceKinds.Chat,
            TaskText = "Keep going on the thread", ContinueSessionId = sessionId,
            RequestedEffort = Messages.Tasks.Effort.TaskEffortModes.Quick, Autonomy = "Confined",
        }, CancellationToken.None);

        // The summary was folded + persisted atomically with the continue run.
        (await LoadSessionAsync(sessionId)).Summary.ShouldBe("DISTILLED THREAD MEMORY");

        // …and it reached the launched agent's goal (the prompt) through the grounding seam.
        ReadAgentGoal(await LoadRunSnapshotAsync(result.RunId)).ShouldContain("DISTILLED THREAD MEMORY", Case.Sensitive,
            customMessage: "the rolling summary of older turns must reach the continuing agent's prompt end-to-end");
    }

    [Fact]
    public async Task Two_continues_through_the_launch_each_advance_the_watermark()
    {
        // The composition the direct-summarizer + single-continue tests don't span: the launch service runs the
        // summarizer on EVERY continue, advancing the watermark incrementally across turns end-to-end.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);
        for (var t = 1; t <= 9; t++) await SeedTurnAsync(teamId, sessionId, t, $"goal-{t}", $"result-{t}");   // 9 prior turns

        using var pause = PauseAutoExecute();

        await LaunchContinueWithFakeLlmAsync(teamId, userId, sessionId, "SUMMARY v1");
        (await LoadSessionAsync(sessionId)).SummaryThroughTurnIndex.ShouldBe(1, "first continue (9 prior turns − window 8) folds the single scrolled-out turn");

        // The first continue created turn 10; the second now sees 10 prior turns → one more scrolls out → watermark advances.
        await LaunchContinueWithFakeLlmAsync(teamId, userId, sessionId, "SUMMARY v2");
        (await LoadSessionAsync(sessionId)).SummaryThroughTurnIndex.ShouldBe(2, "the second continue advances the watermark incrementally through the launch service");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private async Task LaunchContinueWithFakeLlmAsync(Guid teamId, Guid userId, Guid sessionId, string summaryReturn)
    {
        var fakeRegistry = new LLMClientRegistry(new ILLMClient[] { new CapturingLlmClient { Return = summaryReturn } });
        using var scope = _fixture.BeginScope(b => b.RegisterInstance(fakeRegistry).As<ILLMClientRegistry>());

        await scope.Resolve<Core.Services.Tasks.ITaskLaunchService>().LaunchAsync(new Messages.Tasks.TaskLaunchRequest
        {
            TeamId = teamId, ActorUserId = userId, SurfaceKind = Messages.Tasks.TaskLaunchSurfaceKinds.Chat,
            TaskText = "continue the thread", ContinueSessionId = sessionId,
            RequestedEffort = Messages.Tasks.Effort.TaskEffortModes.Quick, Autonomy = "Confined",
        }, CancellationToken.None);
    }

    private async Task RunSummarizerAsync(Guid teamId, Guid sessionId, ILLMClient client, IModelPoolSelector? selector = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var summarizer = new SessionSummarizer(db, new LLMClientRegistry(new[] { client }), selector ?? scope.Resolve<IModelPoolSelector>(), NullLogger<SessionSummarizer>.Instance);

        await summarizer.EnsureSummaryUpToDateAsync(sessionId, teamId, CancellationToken.None);
        await db.SaveChangesAsync();   // the summarizer stages on the shared DbContext; the run starter commits in prod
    }

    private async Task<string?> BuildDigestAsync(Guid sessionId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISessionContextBuilder>().BuildAsync(sessionId, teamId, CancellationToken.None);
    }

    private async Task<WorkSession> LoadSessionAsync(Guid sessionId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkSession.AsNoTracking().SingleAsync(s => s.Id == sessionId);
    }

    private async Task<string> LoadRunSnapshotAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        return run.DefinitionSnapshotJson!;
    }

    private static string ReadAgentGoal(string snapshotJson)
    {
        var root = JsonDocument.Parse(snapshotJson).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");
        return agent.GetProperty("config").GetProperty("goal").GetString()!;
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
        private readonly WorkSessionSummaryFlowTests _owner;
        public Restore(WorkSessionSummaryFlowTests owner) { _owner = owner; }
        public void Dispose() => _owner.SetAutoExecute(clearFirst: false, value: true);
    }

    private async Task SetSummaryAsync(Guid sessionId, string summary, int throughTurn)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var session = await db.WorkSession.SingleAsync(s => s.Id == sessionId);
        session.Summary = summary;
        session.SummaryThroughTurnIndex = throughTurn;
        await db.SaveChangesAsync();
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

    /// <summary>Stage a finished top-level turn: a run bound to the session at <paramref name="turn"/>, its launch goal in the request payload + its result in OutputsJson — the SAME clean sources the digest + summarizer read.</summary>
    private async Task SeedTurnAsync(Guid teamId, Guid sessionId, int turn, string goal, string result)
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
            OutputsJson = JsonSerializer.Serialize(new { summary = result }),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>A capturing stand-in LLM client: records the user prompt + returns a canned summary, so the fold/persist/watermark + prompt-shaping run without a live model. Provider matches the seeded Anthropic pool credential so SelectAsync routes to it.</summary>
    private sealed class CapturingLlmClient : ILLMClient
    {
        public string Provider => "Anthropic";
        public string Return { get; init; } = "SUMMARY";
        public bool Called { get; private set; }
        public string? LastUserPrompt { get; private set; }

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken)
        {
            Called = true;
            LastUserPrompt = request.UserPrompt;
            return Task.FromResult(new LLMCompletion { Text = Return, Model = request.Model });
        }
    }

    /// <summary>An LLM client whose call THROWS — to prove the launch fails open (no write, no throw) on a distillation error.</summary>
    private sealed class ThrowingLlmClient : ILLMClient
    {
        public string Provider => "Anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated LLM failure");
    }

    /// <summary>A model-pool selector whose SelectAsync THROWS — simulating a credential-decrypt fault during model resolution (the path that must also fail open, not break the launch).</summary>
    private sealed class ThrowingSelector : IModelPoolSelector
    {
        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, bool requireStructured, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated credential decrypt fault");

        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, bool requireStructured, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}

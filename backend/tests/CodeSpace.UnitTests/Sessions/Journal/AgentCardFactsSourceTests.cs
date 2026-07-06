using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the agent-card mapping — the shared metrics projection + the supervisor allocation + the ledger compact → a
/// journal card. Pins the ground-truth passthrough (status · model · tokens · duration · cost), the token total (input +
/// output, but NULL when the agent reported no usage so it never reads as a measured zero), the room-aligned LABEL
/// (role → subtask title → instruction → neutral), and the FILES source (the ledger compact's git-truth files win over
/// the agent's own result row, so a codex-cli card whose result folded no changed-file list still shows the count the
/// room shows). The source's tape walk + batched read is integration-tested (it needs the real metrics reader over
/// Postgres); this pins the pure map.
/// </summary>
[Trait("Category", "Unit")]
public class AgentCardFactsSourceTests
{
    private static AgentRunMetrics Metrics(string? goal = "Build the login form", AgentRunStatus status = AgentRunStatus.Succeeded,
        int? inTok = 1200, int? outTok = 340, int tools = 6, string? model = "claude-opus-4-8", decimal? cost = 0.42m, long? durationMs = 45000,
        int? files = 3, FileDiffStat[]? stats = null, string? harness = "codex-cli", string? error = null) =>
        new()
        {
            Status = status, Goal = goal, Error = error, DurationMs = durationMs, InputTokens = inTok, OutputTokens = outTok,
            ToolCount = tools, Model = model, Harness = harness, CostUsd = cost, FilesChanged = files,
            ChangedFileStats = stats ?? new[] { new FileDiffStat("auth/session.ts", 42, 3) }, Resumed = true,
        };

    [Fact]
    public void Carries_the_failure_error_onto_a_failed_card_and_never_onto_a_succeeded_one()
    {
        AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(status: AgentRunStatus.Failed, error: "litellm.BadRequestError: Unexpected message role"), allocation: null, compact: null)
            .Error.ShouldBe("litellm.BadRequestError: Unexpected message role", "a failed card names WHY it failed, not a bare FAILED");

        AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(), allocation: null, compact: null)
            .Error.ShouldBeNull("a succeeded card never shows an error (the metrics gate it to null on success)");
    }

    private static AgentAllocation Alloc(string? role = null, string? subtask = null, string? id = null) => new(role, subtask, id);

    private static SupervisorAgentResult Compact(params string[] changedFiles) =>
        new() { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ChangedFiles = changedFiles };

    [Fact]
    public void Maps_the_ground_truth_metrics_onto_the_card()
    {
        var id = Guid.NewGuid();

        var card = AgentCardFactsSource.ToCard(id, Metrics(), allocation: null, compact: null);

        card.AgentRunId.ShouldBe(id);
        card.Label.ShouldBe("Build the login form");
        card.Status.ShouldBe(AgentRunStatus.Succeeded);
        card.Model.ShouldBe("claude-opus-4-8");
        card.Harness.ShouldBe("codex-cli", "the harness kind rides onto the card for its glyph");
        card.DurationMs.ShouldBe(45000);
        card.Tokens.ShouldBe(1540, "input + output");
        card.ToolCount.ShouldBe(6);
        card.CostUsd.ShouldBe(0.42m);
        card.FilesChanged.ShouldBe(3);
        card.Files.Select(f => (f.Path, f.Additions, f.Deletions)).ShouldBe(new[] { ("auth/session.ts", (int?)42, (int?)3) }, "the per-file diffstat rides onto the card");
        card.Resumed.ShouldBeTrue("the resume provenance rides onto the card");
        card.Review.ShouldBeNull("an un-reviewed agent carries no verdict chip");
    }

    [Fact]
    public void The_latest_reviewer_verdict_rides_onto_the_card()
    {
        var verdict = new JournalReviewVerdict { Approved = false, Rationale = "placeholder hack", Issues = new[] { "hack committed (evidence: feature.txt line 1)" }, ReviewerRunId = Guid.NewGuid(), ReviewerHarness = "claude-code", Scope = JournalReviewVerdict.OutputScope };

        var card = AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(), allocation: null, compact: null, review: verdict);

        card.Review.ShouldBe(verdict, "the adversarial exchange is legible ON the producer's card — verdict, evidence, and the reviewer run to deep-link");
    }

    [Fact]
    public void Tokens_is_null_when_the_agent_reported_no_usage()
    {
        // Both halves null → null total, NOT 0 — a card must not claim a measured zero when usage is simply unknown.
        AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(inTok: null, outTok: null), allocation: null, compact: null).Tokens
            .ShouldBeNull("no reported usage is unknown, not zero");
    }

    [Fact]
    public void Tokens_sums_a_one_sided_usage()
    {
        AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(inTok: 900, outTok: null), allocation: null, compact: null).Tokens.ShouldBe(900, "a present half + a null half is that half, not null");
    }

    // ── Label: role → subtask title → instruction → neutral (mirrors RoomNarrative.ToCard) ──

    [Fact]
    public void Label_prefers_the_subtask_id_so_it_correlates_with_the_deferred_labels()
    {
        // The id is the SAME slug the deferred "waiting on {id}" labels use, so keying the card header on it lets the
        // reader line a card up with its dependents. The human title rides as AssignedSubtask (the hover + drawer).
        var card = AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(goal: "【並行任務】(long instruction)"),
            Alloc(role: "researcher", subtask: "定義軌跡規範 + 分析現有代碼", id: "spec-and-analyze"), compact: null);

        card.Label.ShouldBe("spec-and-analyze", "the subtask id names the card so it correlates with 'waiting on spec-and-analyze'");
        card.AssignedSubtask.ShouldBe("定義軌跡規範 + 分析現有代碼", "the readable title rides along for the hover + drawer");
    }

    [Fact]
    public void Label_prefers_the_allocated_role_over_the_instruction_when_there_is_no_id()
    {
        AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(goal: "Deep-dive the whole session-room turn logic and report"), Alloc(role: "researcher", subtask: "Research current turn logic"), compact: null)
            .Label.ShouldBe("researcher", "with no subtask id, a model-authored role names the agent, not its long instruction");
    }

    [Fact]
    public void A_map_or_flow_agent_with_no_allocation_keeps_its_goal_and_carries_no_assigned_subtask()
    {
        // The label change is generic: a map/flow agent has no allocation → no id → its goal string still names it, and
        // AssignedSubtask stays null (no false hover/strip). This is the case that must NOT regress into "spec-and-…".
        var card = AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(goal: "分析當前Workflow架構的Generic特性"), allocation: null, compact: null);

        card.Label.ShouldBe("分析當前Workflow架構的Generic特性");
        card.AssignedSubtask.ShouldBeNull();
    }

    [Fact]
    public void Label_uses_the_subtask_title_when_there_is_no_role()
    {
        AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(goal: "Deep-dive the whole session-room turn logic and report"), Alloc(subtask: "Research current turn logic"), compact: null)
            .Label.ShouldBe("Research current turn logic", "the short planned-subtask title wins over the raw instruction");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Label_falls_back_to_the_instruction_when_the_agent_was_not_allocated_to_a_subtask(string? subtask)
    {
        // A homogeneous spawn / flat plan yields no role/title — the card then shows the instruction (matching the room's Goal fallback), never a blanked-out row.
        AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(goal: "Summarize the findings"), Alloc(role: null, subtask: subtask), compact: null)
            .Label.ShouldBe("Summarize the findings");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_back_to_a_neutral_label_when_neither_allocation_nor_goal_names_the_agent(string? goal)
    {
        AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(goal: goal), allocation: null, compact: null).Label.ShouldBe("Agent", "an unnamed subtask still renders a card");
    }

    // ── Files: the ledger compact's git-truth wins, so a card can't disagree with the room ──

    [Fact]
    public void FilesChanged_uses_the_ledger_compact_when_the_agents_own_result_folded_none()
    {
        // The codex-cli case: the agent's own result row carried no changed-file list (metrics files empty), but the
        // supervisor folded the git-truth into the compact. The card must show that count — the same one the room shows.
        var card = AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(files: null, stats: Array.Empty<FileDiffStat>()), allocation: null, compact: Compact("docs/report.md"));

        card.FilesChanged.ShouldBe(1, "the compact's git-truth changed-file count fills the gap the result row left");
        card.Files.Select(f => (f.Path, f.Additions, f.Deletions)).ShouldBe(new[] { ("docs/report.md", (int?)null, (int?)null) }, "path-only rows from the compact when no diffstat was captured");
    }

    [Fact]
    public void FilesChanged_prefers_the_richer_metrics_diffstat_rows_when_present()
    {
        // Claude-code case: both sources have the files. The count comes from the compact (the room's source), but the
        // per-file +/- diffstat rows come from the metrics reader, which carries the added/removed line counts.
        var card = AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(files: 1), allocation: null, compact: Compact("auth/session.ts"));

        card.FilesChanged.ShouldBe(1);
        card.Files.Select(f => (f.Path, f.Additions, f.Deletions)).ShouldBe(new[] { ("auth/session.ts", (int?)42, (int?)3) }, "the diffstat rows survive when the metrics reader has them");
    }

    [Fact]
    public void FilesChanged_stays_from_the_metrics_reader_when_there_is_no_compact()
    {
        // No supervisor compact (e.g. a not-yet-folded outcome) → the card falls back to the metrics reader, unchanged from before.
        var card = AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(files: 3), allocation: null, compact: null);

        card.FilesChanged.ShouldBe(3);
        card.Files.Select(f => f.Path).ShouldBe(new[] { "auth/session.ts" });
    }
}

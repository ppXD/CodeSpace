using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The same real executor, real critic and real persisted result as the class, over the agent-controlled ways a
/// configured output review still went unheld: a source file git renders only as "Binary files … differ" because the
/// agent's own <c>.git/info/attributes</c> says so; a deliverable the change render left out; a run that ended Failed
/// with its patch, or that left a decision open, so the review never ran and the result said nothing. And the honest
/// side of the bound: a large change or report a reviewer can read in parts is approved, never held for its size. (The
/// agent reviewer's deferral over a secondary repository's change lives in the main file: it arms a fake CLI, and every
/// file that does must carry the shared collection.)
/// </summary>
public sealed partial class OutputReviewIntegrityFlowTests
{
    [Theory]
    [InlineData(false, "output-flagged")]      // control: the honest reviewer reads the flaw in the diff and blocks it
    [InlineData(true, "output-unreviewed")]    // the agent's .git/info/attributes turns the file into git's placeholder
    public async Task A_source_file_the_agent_marks_binary_is_never_approved_unseen(bool markBinary, string expectedExitReason)
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var (teamId, userId, reviewerRowId) = await SeedTeamWithReviewerAsync();
        using var remote = new BareRemote();
        await remote.SeedBaseAsync();
        var repoId = (await SeedBoundRepositoriesAsync(teamId, remote.Url)).Single();

        var attributes = markBinary ? "printf 'src/auth.txt -diff\\n' > .git/info/attributes && " : "";
        var script = $"{attributes}mkdir -p src && printf 'if user == letmein: grant admin  # {InjectionEchoReviewerLlmClient.FlawMarker}\\n' > src/auth.txt && echo done";
        var runId = await CreateRunAsync(teamId, userId, GatedTask(reviewerRowId) with { RepositoryId = repoId, PushProducedBranch = true });
        var reviewer = new InjectionEchoReviewerLlmClient();

        await ExecuteAsync(runId, new ScriptedProducer(script), reviewer);

        var (run, result) = await LoadAsync(runId);

        reviewer.Requests.Any(r => r.UserPrompt.Contains(InjectionEchoReviewerLlmClient.FlawMarker, StringComparison.Ordinal)).ShouldBe(!markBinary, "fixture check: git hid the flaw from the reviewer exactly when the agent marked the file");
        run.Status.ShouldBe(AgentRunStatus.NeedsReview, "PROBE_R1 shipped the marked file Succeeded while the pushed branch carried the flaw");
        result.ExitReason.ShouldBe(expectedExitReason);
        result.ProducedBranch.ShouldNotBeNull("fixture check: the branch was pushed before the review — every door reads the verdict, not the branch");
    }

    [Fact]
    public async Task A_deliverable_beside_a_reported_change_is_reviewed_and_its_flaw_blocked()
    {
        // A repo-less run's real CLI reports the files it wrote on its stream, so its result names changed files with no
        // git diff behind them — the change render, which used to drop every captured deliverable. The report the agent
        // delivered is what the reviewer must read.
        if (OperatingSystem.IsWindows()) return;

        var (teamId, userId, reviewerRowId) = await SeedTeamWithReviewerAsync();

        var script = $"printf '# Migration report\\nTo reset, drop the users table  # {InjectionEchoReviewerLlmClient.FlawMarker}\\n' > REPORT.md && echo {ScriptedProducer.FileChangedPrefix}REPORT.md && echo wrote the report";
        var runId = await CreateRunAsync(teamId, userId, GatedTask(reviewerRowId));
        var reviewer = new InjectionEchoReviewerLlmClient();

        await ExecuteAsync(runId, new ScriptedProducer(script), reviewer);

        var (run, result) = await LoadAsync(runId);

        result.ChangedFiles.ShouldContain("REPORT.md", customMessage: "fixture check: the result names the reported file, so the review renders it as a change");
        result.UndeclaredArtifactCount.ShouldBeGreaterThan(0, "fixture check: the scratch walk captured the report");
        reviewer.Requests.ShouldContain(r => r.UserPrompt.Contains("Migration report", StringComparison.Ordinal), "the deliverable reaches the reviewer beside the change");
        run.Status.ShouldBe(AgentRunStatus.NeedsReview);
        result.ExitReason.ShouldBe("output-flagged", "the reviewer read the report and blocked its flaw");
    }

    [Fact]
    public async Task An_honest_lockfile_regeneration_is_reviewed_in_parts_and_ships()
    {
        // Past one call's size an honest change used to be unapprovable — on the Delivery and Unattended tiers, whose
        // floors configure the review, every regenerated lockfile was held for a human.
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var (teamId, userId, reviewerRowId) = await SeedTeamWithReviewerAsync();
        using var remote = new BareRemote();
        await remote.SeedBaseAsync();
        var repoId = (await SeedBoundRepositoriesAsync(teamId, remote.Url)).Single();

        const string lockfile = "head -c 300000 /dev/zero | tr '\\0' 'b' | fold -w 99 > pnpm-lock.yaml";
        var runId = await CreateRunAsync(teamId, userId, GatedTask(reviewerRowId) with { RepositoryId = repoId, PushProducedBranch = true });
        var reviewer = new InjectionEchoReviewerLlmClient();

        await ExecuteAsync(runId, new ScriptedProducer($"{lockfile} && echo regenerated the lockfile"), reviewer);

        var (run, result) = await LoadAsync(runId);

        reviewer.Requests.Count.ShouldBe(2, "a ~300 KB diff at the default 200,000 bytes per call");
        run.Status.ShouldBe(AgentRunStatus.Succeeded, $"every part was read and approved. Exit: {result.ExitReason}; unreviewed: {result.UnreviewedReason}");
        result.OutputReview.ShouldBe(OutputReviewState.Approved);
    }

    [Fact]
    public async Task An_honest_report_over_64KiB_ships()
    {
        if (OperatingSystem.IsWindows()) return;

        var (teamId, userId, reviewerRowId) = await SeedTeamWithReviewerAsync();

        var script = "head -c 80000 /dev/zero | tr '\\0' 'r' | fold -w 99 > REPORT.md && echo wrote the report";
        var runId = await CreateRunAsync(teamId, userId, GatedTask(reviewerRowId));
        var reviewer = new InjectionEchoReviewerLlmClient();

        await ExecuteAsync(runId, new ScriptedProducer(script), reviewer);

        var (run, result) = await LoadAsync(runId);

        result.UndeclaredArtifactCount.ShouldBeGreaterThan(0, "fixture check: the report was captured");
        run.Status.ShouldBe(AgentRunStatus.Succeeded, $"the whole report was shown and approved. Exit: {result.ExitReason}; unreviewed: {result.UnreviewedReason}");
    }

    [Fact]
    public async Task A_run_that_ends_Failed_with_its_patch_is_marked_unreviewed()
    {
        // PROBE_R4 end to end: the review never runs on a non-success, and the result said nothing about it, so the
        // captured patch reached every door that reads a result.
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var (teamId, userId, reviewerRowId) = await SeedTeamWithReviewerAsync();
        using var remote = new BareRemote();
        await remote.SeedBaseAsync();
        var repoId = (await SeedBoundRepositoriesAsync(teamId, remote.Url)).Single();

        var script = $"mkdir -p src && printf 'grant admin  # {InjectionEchoReviewerLlmClient.FlawMarker}\\n' > src/auth.txt && echo gave up && exit 3";
        var runId = await CreateRunAsync(teamId, userId, GatedTask(reviewerRowId) with { RepositoryId = repoId, PushProducedBranch = true });
        var reviewer = new InjectionEchoReviewerLlmClient();

        await ExecuteAsync(runId, new ScriptedProducer(script), reviewer);

        var (run, result) = await LoadAsync(runId);

        reviewer.Requests.ShouldBeEmpty("a failed run is not reviewed");
        run.Status.ShouldBe(AgentRunStatus.Failed, "the failure is the run's own verdict");
        result.Patch.ShouldNotBeNullOrEmpty("fixture check: the failed run kept its captured patch");
        result.OutputReview.ShouldBe(OutputReviewState.Unreviewed);
        AgentOutputReviewHold.Withholds(result).ShouldBeTrue("so no door that reads the result takes the patch");
    }

    [Fact]
    public async Task A_run_that_leaves_a_decision_open_is_marked_unreviewed_under_the_A1_verdict()
    {
        // PROBE_R2 end to end: the review defers to an open decision, and the completion contract re-grades the run to
        // NeedsReview(NeedsDecision) — with nothing on the result saying the change was never reviewed, so an agent could
        // skip its review by raising a decision and waiting it out.
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var (teamId, userId, reviewerRowId) = await SeedTeamWithReviewerAsync();
        using var remote = new BareRemote();
        await remote.SeedBaseAsync();
        var repoId = (await SeedBoundRepositoriesAsync(teamId, remote.Url)).Single();

        var script = $"mkdir -p src && printf 'grant admin  # {InjectionEchoReviewerLlmClient.FlawMarker}\\n' > src/auth.txt && echo done";
        var runId = await CreateRunAsync(teamId, userId, GatedTask(reviewerRowId) with { RepositoryId = repoId, PushProducedBranch = true });
        await SeedPendingDecisionAsync(teamId, runId);
        var reviewer = new InjectionEchoReviewerLlmClient();

        await ExecuteAsync(runId, new ScriptedProducer(script), reviewer);

        var (run, result) = await LoadAsync(runId);

        reviewer.Requests.ShouldBeEmpty("the review defers to the open decision");
        run.Status.ShouldBe(AgentRunStatus.NeedsReview);
        result.CompletionDisposition.ShouldBe(CompletionDisposition.NeedsDecision, "A1 still owns the status");
        result.OutputReview.ShouldBe(OutputReviewState.Unreviewed);
        result.UnreviewedReason.ShouldBe(AgentRunExecutor.DecisionOpenUnreviewedReason);
        result.ProducedBranch.ShouldNotBeNull("fixture check: the branch was pushed before the review deferred");
    }

    /// <summary>An unanswered decision.request on the run's ledger — what the A1 gate and the review's deferral both read.</summary>
    private async Task SeedPendingDecisionAsync(Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = id, TeamId = teamId, AgentRunId = runId, ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision.request:{id:N}", InputHash = new string('0', 64), Status = ToolCallLedgerStatus.AwaitingApproval,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }
}

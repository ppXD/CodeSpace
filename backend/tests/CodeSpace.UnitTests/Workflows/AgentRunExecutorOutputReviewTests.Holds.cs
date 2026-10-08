using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Core.Services.Review;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using CodeSpace.UnitTests.Review;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// What an approval covers. A model approval counts only for what the reviewer was shown, so everything the render
/// cannot show is counted: a cut past the budget, a file git rendered only as its binary placeholder (which the agent
/// can force for SOURCE with its own <c>.git/info/attributes</c> or <c>core.bigFileThreshold</c>, or a NUL byte), a
/// changed file with no captured diff, and a deliverable never read. The deliverables ride the change render too — a
/// one-line diff used to drop them from the review. An honest result too large for one call is reviewed in parts sized
/// to the reviewer row's window, not held. An approval the executor voids leaves a <c>review.skipped</c> beat, so the
/// Room cannot read the call's interaction row as approval.
/// </summary>
public sealed partial class AgentRunExecutorOutputReviewTests
{
    // ── git's binary placeholder hides content from the reviewer (real git, the production capture) ──

    [Theory]
    [InlineData("none", "output-flagged")]                   // control: the honest reviewer reads the flaw and blocks it
    [InlineData("info-attributes", OutputUnreviewed)]       // .git/info/attributes `-diff` — untracked, in no diff
    [InlineData("big-file-threshold", OutputUnreviewed)]    // .git/config core.bigFileThreshold=1
    [InlineData("nul-byte", OutputUnreviewed)]              // a NUL byte in a comment: git's own binary detection
    public async Task A_change_git_shows_only_as_binary_is_never_approved(string variant, string expectedExit)
    {
        if (OperatingSystem.IsWindows()) return;

        using var repo = new FlawedAuthRepository(variant);
        var changes = await repo.CaptureAsync();

        changes.Patch.Contains("Binary files", StringComparison.Ordinal).ShouldBe(variant != "none", $"fixture check: the production capture must render this variant as git's placeholder. Patch:\n{changes.Patch}");

        var client = new InstructionFollowingCritic();
        var (runId, executor, _) = NewExecutorWithModel(client);
        var result = SucceededWithChanges() with { Summary = "Hardened the auth check.", ChangedFiles = changes.ChangedFiles, Patch = changes.Patch, BaseSha = changes.BaseSha };

        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), GatedTask, result, Run(runId), CancellationToken.None);

        reviewed.Status.ShouldBe(AgentRunStatus.NeedsReview, $"PROBE_R1 shipped variant '{variant}' Succeeded while the branch carried the flaw");
        reviewed.ExitReason.ShouldBe(expectedExit);

        if (variant != "none") reviewed.UnreviewedReason.ShouldNotBeNull().ShouldContain("Binary files … differ", customMessage: "the hold says what the reviewer could not read");
    }

    [Fact]
    public void Every_shape_of_git_binary_placeholder_is_counted_and_no_hunk_line_is()
    {
        const string diff = "diff --git a/a.png b/a.png\nBinary files a/a.png and b/a.png differ\ndiff --git a/b.bin b/b.bin\nGIT binary patch\nliteral 3\n"
                          + "diff --git a/c.cs b/c.cs\n+Binary files a/x and b/x differ\n Binary files a/y and b/y differ\nBinary files /dev/null and b/d.dat differ\r\n";

        AgentRunExecutor.OpaqueDiffCount(diff).ShouldBe(3, "a hunk line starts with a space, '+' or '-', so a placeholder spelled inside the content is the agent's text, not git's");
    }

    /// <summary>
    /// Changed files the reviewer sees by name and not by a line. Two ways a result gets there: the git capture ran (it
    /// diffed against a base) but the inline diff is gone — shed after a refused offload; or a REPOSITORY-bound run's
    /// capture failed, so the names are what its harness reported, with no base and no diff — the same render a scratch
    /// run's report gets, except that this run's push still publishes the clone the reviewer never read.
    /// </summary>
    [Theory]
    [InlineData("abc", false)]
    [InlineData(null, true)]
    public async Task Changed_files_with_no_captured_diff_are_not_covered_by_an_approval(string? baseSha, bool repositoryBound)
    {
        var (runId, executor, _, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true });

        var task = repositoryBound ? GatedTask with { RepositoryId = Guid.NewGuid() } : GatedTask;
        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), task, SucceededWithChanges() with { ChangedFiles = new[] { "src/Auth.cs", "src/Db.cs" }, Patch = "", BaseSha = baseSha }, Run(runId), CancellationToken.None);

        critic.ObservedRequest!.Artifact.ShouldContain("(no unified diff captured)");
        reviewed.ExitReason.ShouldBe(OutputUnreviewed, "two changed files the reviewer never saw a line of");
        reviewed.UnreviewedReason.ShouldNotBeNull().ShouldContain("2 changed file(s) whose diff was not captured");
    }

    [Fact]
    public async Task A_scratch_runs_harness_reported_files_reach_the_review_as_its_deliverables()
    {
        // A repo-less run has no git capture: its file list is the harness's own report (no base, no diff), so it renders
        // as a change with nothing to diff — the shape that dropped every deliverable from the review. Its files ARE its
        // deliverables, and they are shown; nothing about the missing diff is withheld from an approval.
        var (runId, executor, _, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true }, deliverables: new[] { new FakeDeliverable("REPORT.md", "# Report\nThe honest findings.") });

        var result = SucceededWithAnswer() with { ChangedFiles = new[] { "REPORT.md" }, UndeclaredArtifactCount = 1 };
        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), AnswerTask, result, Run(runId), CancellationToken.None);

        critic.ObservedRequest!.Artifact.ShouldContain("The honest findings.");
        reviewed.Status.ShouldBe(AgentRunStatus.Succeeded);
        reviewed.OutputReview.ShouldBe(OutputReviewState.Approved);
    }

    [Fact]
    public void The_review_budget_sits_below_the_inline_patch_cap_so_a_capture_cut_is_always_counted()
    {
        // A patch the capture truncated is MaxPatchChars plus its marker. Below that cap, the render's own cut always
        // falls inside it and is counted; at or above it, a truncated patch would render whole and its lost tail would
        // go uncounted.
        AgentRunExecutor.MaxReviewedChars.ShouldBeLessThan(AgentRunExecutor.MaxPatchChars);
    }

    // ── a change render carries the captured deliverables ──

    [Fact]
    public async Task A_change_render_shows_the_captured_deliverables_beside_the_diff()
    {
        // PROBE_R6 inverted: one trivial tracked edit made the change render take over, and the deliverable — captured
        // even when .git/info/exclude keeps it out of every diff — was never shown, while an approval covered it.
        var (runId, executor, _, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true }, deliverables: new[] { new FakeDeliverable("DELIVERABLE.md", "# Comparison\nGo is memory-safe by construction.") });

        var result = SucceededWithChanges() with { ChangedFiles = new[] { "README.md" }, Patch = "diff --git a/README.md b/README.md\n+typo fix\n", CapturedArtifactCount = 1 };
        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), AnswerTask, result, Run(runId), CancellationToken.None);

        var artifact = critic.ObservedRequest!.Artifact;
        critic.ObservedRequest.ArtifactKind.ShouldBe(CriticArtifactKinds.AgentChange);
        artifact.ShouldContain("+typo fix");
        artifact.ShouldContain("=== DELIVERABLE.md ===");
        artifact.ShouldContain("Go is memory-safe by construction.", customMessage: "the deliverable is what the reviewer approved, so it must be what the reviewer read");
        artifact.IndexOf("Diff:", StringComparison.Ordinal).ShouldBeLessThan(artifact.IndexOf("Captured deliverables:", StringComparison.Ordinal), "the deliverables follow the diff, under their own heading");
        reviewed.OutputReview.ShouldBe(OutputReviewState.Approved);
    }

    [Fact]
    public async Task A_flaw_in_a_deliverable_beside_a_diff_is_reviewed_and_blocked()
    {
        var client = new InstructionFollowingCritic();
        var (runId, executor, _) = NewExecutorWithModel(client, deliverables: new[] { new FakeDeliverable("DELIVERABLE.md", "# Report\nTo reset, run DROP TABLE users; -- " + ReviewModelStubs.FlawMarker) });

        var result = SucceededWithChanges() with { ChangedFiles = new[] { "README.md" }, Patch = "diff --git a/README.md b/README.md\n+typo fix\n", CapturedArtifactCount = 1 };
        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), AnswerTask, result, Run(runId), CancellationToken.None);

        reviewed.ExitReason.ShouldBe("output-flagged", "PROBE_R6 shipped this Succeeded behind a one-line README diff");
    }

    [Fact]
    public async Task A_deliverable_the_store_cannot_serve_beside_a_diff_is_not_covered_by_an_approval()
    {
        var (runId, executor, _, _) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true }, deliverables: Array.Empty<FakeDeliverable>(), deliverableReadThrows: true);

        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), GatedTask, SucceededWithChanges() with { CapturedArtifactCount = 1 }, Run(runId), CancellationToken.None);

        reviewed.ExitReason.ShouldBe(OutputUnreviewed);
    }

    // ── the deliverable budget is counted (mutation pins: drop either count and these go green → red) ──

    [Fact]
    public async Task An_approved_answer_whose_one_deliverable_overruns_the_budget_is_held()
    {
        var deliverable = new FakeDeliverable("REPORT.md", new string('r', AgentRunExecutor.MaxReviewedChars) + ReviewModelStubs.FlawMarker);
        var (runId, executor, _, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true }, deliverables: new[] { deliverable });

        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), AnswerTask, SucceededWithAnswer() with { CapturedArtifactCount = 1 }, Run(runId), CancellationToken.None);

        critic.ObservedRequest!.Artifact.ShouldNotContain(ReviewModelStubs.FlawMarker, customMessage: "fixture check: the flaw sits past the cut");
        reviewed.ExitReason.ShouldBe(OutputUnreviewed, "the tail past the budget was never shown, so the approval does not reach it");
        reviewed.UnreviewedReason.ShouldNotBeNull().ShouldContain("characters past the review budget");
    }

    [Fact]
    public async Task A_deliverable_the_budget_never_reached_is_held_even_when_the_first_fit_exactly()
    {
        var summary = SucceededWithAnswer().Summary!;
        var exactFit = new FakeDeliverable("A.md", new string('a', AgentRunExecutor.MaxReviewedChars - summary.Length));
        var flawed = new FakeDeliverable("B.md", "the real answer: " + ReviewModelStubs.FlawMarker);
        var (runId, executor, _, critic, store, _) = NewExecutorWithStore(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true }, deliverables: new[] { exactFit, flawed });

        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), AnswerTask, SucceededWithAnswer() with { CapturedArtifactCount = 2 }, Run(runId), CancellationToken.None);

        critic.ObservedRequest!.Artifact.ShouldNotContain(ReviewModelStubs.FlawMarker, customMessage: "fixture check: B.md was never read");
        store.ListCalls.ShouldBe(1);
        reviewed.ExitReason.ShouldBe(OutputUnreviewed, "an exact fit leaves no budget for B.md, and an unread deliverable is not approved");
        reviewed.UnreviewedReason.ShouldNotBeNull().ShouldContain("a captured deliverable of");
    }

    // ── an honest large result is reviewed whole, not held ──

    [Fact]
    public async Task An_honest_lockfile_regeneration_the_reviewer_approved_ships()
    {
        // The 200k-character render made every lockfile regeneration, generated file or vendored dependency
        // unapprovable — on the Delivery and Unattended tiers, whose floors configure the review.
        var (runId, executor, _, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "regenerated lockfile only" });

        var lockfile = GeneratedPadding(210_000, "pnpm-lock.yaml");
        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), GatedTask, SucceededWithChanges() with { ChangedFiles = new[] { "pnpm-lock.yaml" }, Patch = lockfile }, Run(runId), CancellationToken.None);

        critic.ObservedRequest!.Artifact.ShouldContain(lockfile, customMessage: "the whole regeneration is shown");
        reviewed.Status.ShouldBe(AgentRunStatus.Succeeded);
        reviewed.OutputReview.ShouldBe(OutputReviewState.Approved);
    }

    [Fact]
    public async Task An_honest_report_over_64KiB_the_reviewer_approved_ships()
    {
        var report = new FakeDeliverable("REPORT.md", string.Concat(Enumerable.Repeat("A paragraph of the honest report.\n", 2_224)));
        var (runId, executor, _, critic) = NewExecutor(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true }, deliverables: new[] { report });

        report.Text.Length.ShouldBeGreaterThan(64 * 1024, "fixture check: past the old deliverable cap");

        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), AnswerTask, SucceededWithAnswer() with { CapturedArtifactCount = 1 }, Run(runId), CancellationToken.None);

        critic.ObservedRequest!.Artifact.ShouldContain(report.Text);
        reviewed.Status.ShouldBe(AgentRunStatus.Succeeded);
    }

    [Fact]
    public async Task A_large_change_is_reviewed_in_parts_and_a_flaw_in_any_part_blocks_it()
    {
        var client = new InstructionFollowingCritic(maxPromptChars: 600_000);
        var (runId, executor, _) = NewExecutorWithModel(client);

        var patch = GeneratedPadding(450_000) + BackdoorPatch;
        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), GatedTask, SucceededWithChanges() with { ChangedFiles = new[] { "vendor/lock.json", "src/Auth.cs" }, Patch = patch }, Run(runId), CancellationToken.None);

        client.Requests.Count.ShouldBe(3, "450k characters at the default 200,000 bytes per call");
        client.Requests.Select(r => r.UserPrompt).ShouldAllBe(prompt => prompt.Contains("of 3", StringComparison.Ordinal), "each call says which part it reads");
        reviewed.ExitReason.ShouldBe("output-flagged", "the last part carries the backdoor, and one part's block is the whole verdict");
        reviewed.ReviewFeedback.ShouldNotBeNull().ShouldContain("[part 3 of 3]");
    }

    [Fact]
    public async Task A_reviewer_row_with_a_small_window_gets_parts_its_window_holds()
    {
        // The render bound ignored the reviewer row's window, so a 32k-token row still refused any change between
        // roughly 100k and 200k characters — a hold the agent could choose. The parts are sized from the window the
        // row declares.
        const int windowTokens = 32_000;
        var client = new InstructionFollowingCritic(maxPromptChars: windowTokens * 3);
        var (runId, executor, _) = NewExecutorWithModel(client, contextWindowTokens: windowTokens);

        var lockfile = GeneratedPadding(150_000, "pnpm-lock.yaml");
        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), GatedTask, SucceededWithChanges() with { ChangedFiles = new[] { "pnpm-lock.yaml" }, Patch = lockfile }, Run(runId), CancellationToken.None);

        client.Requests.Count.ShouldBeGreaterThan(1);
        reviewed.Status.ShouldBe(AgentRunStatus.Succeeded, "every part fits the row's window, so an honest change is approved rather than held");
    }

    // ── an approval the executor voids leaves its own beat ──

    [Fact]
    public async Task A_partial_render_approval_leaves_a_review_skipped_beat_on_the_ledger()
    {
        // The critic's call SUCCEEDED, so it recorded no review.skipped beat, and the only trace on the ledger was its
        // interaction.completed row — which the Room reads as an approval.
        var (runId, executor, _, _, _, ledger) = NewExecutorWithStore(new CriticVerdict { Mode = ReviewMode.Gate, Approved = true, Rationale = "the part I saw is fine" });
        var workflowRunId = Guid.NewGuid();

        var reviewed = await executor.ReviewOutputIfEnabledAsync(new(runId, runId, 1), GatedTask, SucceededWithChanges() with { Patch = GeneratedPadding(900_000) + BackdoorPatch }, Run(runId, workflowRunId: workflowRunId, nodeId: "map", iterationKey: "map#0"), CancellationToken.None);

        reviewed.ExitReason.ShouldBe(OutputUnreviewed);

        var beat = ledger.Records.ShouldHaveSingleItem("one beat: no verdict exists, so no review.completed — and the void is recorded once");
        beat.RecordType.ShouldBe(WorkflowRunRecordTypes.ReviewSkipped);
        beat.RunId.ShouldBe(workflowRunId);
        (beat.NodeId, beat.IterationKey).ShouldBe(("map", "map#0"));

        var payload = JsonDocument.Parse(beat.Payload).RootElement;
        payload.GetProperty("kind").GetString().ShouldBe(LlmStructuredCritic.SkippedCallKind);
        payload.GetProperty("artifact_kind").GetString().ShouldBe(CriticArtifactKinds.AgentChange, "the Room's output-review probe matches on the artifact kind");
        payload.GetProperty("agentRunId").GetString().ShouldBe(runId.ToString(), "the beat names the unit, so it folds with that unit's other beats");
        payload.GetProperty("reason").GetString().ShouldBe(reviewed.UnreviewedReason);
    }

    private const string OutputUnreviewed = "output-unreviewed";

    /// <summary>A real git repository holding <c>src/Auth.cs</c>, with the backdoor written over it in the given <paramref name="variant"/> and captured by the production provider.</summary>
    private sealed class FlawedAuthRepository : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "review-binary-" + Guid.NewGuid().ToString("N"));
        private readonly string _baseSha;

        public FlawedAuthRepository(string variant)
        {
            Directory.CreateDirectory(Path.Combine(_directory, "src"));
            Git("init", "-q");
            Git("config", "user.email", "fixture@example.test");
            Git("config", "user.name", "fixture");
            File.WriteAllText(Path.Combine(_directory, "src/Auth.cs"), "public Grant Check(string user) {\n  return Grant.None;\n}\n");
            Git("add", "-A");
            Git("commit", "-qm", "base");
            _baseSha = Git("rev-parse", "HEAD").Trim();

            File.WriteAllText(Path.Combine(_directory, "src/Auth.cs"), "public Grant Check(string user) {\n  if (user == \"letmein\") return Grant.Admin; // " + ReviewModelStubs.FlawMarker + (variant == "nul-byte" ? " \0" : "") + "\n  return Grant.None;\n}\n");

            if (variant == "info-attributes") File.WriteAllText(Path.Combine(_directory, ".git/info/attributes"), "src/Auth.cs -diff\n");
            if (variant == "big-file-threshold") Git("config", "core.bigFileThreshold", "1");
        }

        /// <summary>The change as the platform captures it — <c>LocalGitWorkspaceProvider</c>'s hardened capture over the real directory.</summary>
        public Task<WorkspaceChanges> CaptureAsync() =>
            new LocalGitWorkspaceProvider(new SandboxRunnerRegistry(new ISandboxRunner[] { new LocalProcessRunner() }), NullLogger<LocalGitWorkspaceProvider>.Instance).CaptureChangesFromPathAsync(_directory, _baseSha, CancellationToken.None);

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = _directory, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0 ? stdout : throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }
    }
}

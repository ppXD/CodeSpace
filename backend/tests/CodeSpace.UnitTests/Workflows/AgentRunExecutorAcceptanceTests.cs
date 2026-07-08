using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Messages.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The S5 OBJECTIVE oracle gate (<see cref="AgentRunExecutor.GradeAcceptanceIfPresentAsync"/>) — the
/// single-agent twin of the supervisor's per-unit fold gate. Fail-closed on a failing check or an ungradable
/// contract; byte-identical when no contract / non-success / multi-repo (deferred); the captured work is
/// always preserved (the STATUS tells the truth, the branch and diff stay for diagnosis).
/// </summary>
public class AgentRunExecutorAcceptanceTests
{
    [Fact]
    public async Task No_contract_is_byte_identical_and_never_grades()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "unused" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(acceptance: null), Succeeded(), CancellationToken.None);

        result.AcceptancePassed.ShouldBeNull();
        grader.Calls.ShouldBe(0, "no contract ⇒ the grader is never invoked");
    }

    [Fact]
    public async Task An_all_blank_command_reads_as_no_contract()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec(" ", "")), Succeeded(), CancellationToken.None);

        result.AcceptancePassed.ShouldBeNull();
        grader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_non_success_result_skips_the_gate()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var failed = Succeeded() with { Status = AgentRunStatus.Failed };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), failed, CancellationToken.None);

        result.ShouldBe(failed, "a failed run is already the truth — nothing to gate");
        grader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_multi_repo_result_defers_exactly_like_the_supervisor_unit_gate()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var multi = Succeeded() with { RepositoryResults = new[] { new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "other" } } };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), multi, CancellationToken.None);

        result.AcceptancePassed.ShouldBeNull("per-repo grading is a follow-on — deferred, never a blind primary-only grade");
        grader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_contract_with_no_branch_and_no_patch_fails_closed_without_grading()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var noBranch = Succeeded() with { ProducedBranch = null };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), noBranch, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe("acceptance-failed");
        result.AcceptancePassed.ShouldBe(false);
        result.AcceptanceDetail.ShouldBe("no-branch-or-repo");
        grader.Calls.ShouldBe(0, "there is nothing to clone — fail closed, never a phantom pass");
        grader.PatchCalls.ShouldBe(0);
    }

    // ── S2: no branch, but a recorded patch (a PatchOnly-mode producer, or a policy-blocked push) ─────

    [Fact]
    public async Task A_contract_with_no_branch_but_a_recorded_patch_grades_via_the_patch_not_fail_closed()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });

        var patchOnly = SucceededPatchOnly();
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), patchOnly, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Succeeded, "the patch was gradeable — no reason to fail closed");
        result.AcceptancePassed.ShouldBe(true);
        result.AcceptanceDetail.ShouldBe("exit 0");
        grader.Calls.ShouldBe(0, "no branch → the branch-based path is never invoked");
        grader.PatchCalls.ShouldBe(1);
        grader.LastPatchBaseSha.ShouldBe(patchOnly.BaseSha);
    }

    [Fact]
    public async Task A_patch_based_grade_that_fails_regrades_the_run_to_failed_exactly_like_a_branch_failure()
    {
        var (executor, _) = NewExecutor(new BenchmarkGrade { Passed = false, Detail = "exit 1" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), SucceededPatchOnly(), CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe("acceptance-failed");
        result.AcceptancePassed.ShouldBe(false);
        result.AcceptanceDetail.ShouldBe("exit 1");
    }

    [Fact]
    public async Task An_offloaded_patch_artifact_is_preferred_when_present_alongside_an_inline_patch()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });
        var artifactId = Guid.NewGuid();

        await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")),
            SucceededPatchOnly() with { Patch = "diff --git a/x b/x", PatchArtifactId = artifactId }, CancellationToken.None);

        grader.PatchCalls.ShouldBe(1, "an inline patch OR an artifact id both count as gradeable — either is threaded to GradePatchAsync, which resolves whichever the offloader needs");
    }

    // ── S2: no branch, no patch, expectsChanges decides the outcome ─────────────────────

    [Fact]
    public async Task No_branch_no_patch_and_expects_changes_false_is_a_vacuous_pass_not_a_failure()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "unused" });

        var noWork = Succeeded() with { ProducedBranch = null };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh"), expectsChanges: false), noWork, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Succeeded, "nothing about the run itself went wrong — the STATUS is untouched");
        result.AcceptancePassed.ShouldBe(true, "the correctly-predicted no-diff outcome is a PASS, never a failure");
        result.AcceptanceDetail.ShouldStartWith("not-applicable");
        grader.Calls.ShouldBe(0);
        grader.PatchCalls.ShouldBe(0);
    }

    [Fact]
    public async Task No_branch_no_patch_and_expects_changes_true_explicitly_fails_closed_exactly_like_the_default()
    {
        var (executor, _) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "unused" });

        var noWork = Succeeded() with { ProducedBranch = null };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh"), expectsChanges: true), noWork, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.AcceptanceDetail.ShouldBe("no-branch-or-repo");
    }

    [Fact]
    public async Task Expects_changes_false_is_ignored_when_a_branch_or_patch_actually_exists()
    {
        // false only excuses an ABSENCE — it never suppresses grading real, present work.
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh"), expectsChanges: false), Succeeded(), CancellationToken.None);

        result.AcceptancePassed.ShouldBe(true);
        result.AcceptanceDetail.ShouldBe("exit 0", "the branch was graded for real — not waved through as not-applicable");
        grader.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_passing_check_stamps_the_verdict_and_keeps_the_run_succeeded()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), Succeeded(), CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.AcceptancePassed.ShouldBe(true);
        result.AcceptanceDetail.ShouldBe("exit 0");
        grader.Calls.ShouldBe(1);
        grader.LastCommand.ShouldBe(new[] { "sh", "check.sh" });
    }

    [Fact]
    public async Task A_failing_check_regrades_the_run_to_failed_but_preserves_the_work()
    {
        var (executor, _) = NewExecutor(new BenchmarkGrade { Passed = false, Detail = "exit 1" });

        var succeeded = Succeeded();
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), succeeded, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed, "an objective oracle failing means the contract was NOT met — Failed is the truth");
        result.ExitReason.ShouldBe("acceptance-failed");
        result.AcceptancePassed.ShouldBe(false);
        result.AcceptanceDetail.ShouldBe("exit 1");
        result.ProducedBranch.ShouldBe(succeeded.ProducedBranch, "the captured work survives for diagnosis");
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_grader_error_fails_closed_rather_than_crashing_the_completion()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });
        grader.Throw = new InvalidOperationException("clone exploded");

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), Succeeded(), CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.AcceptanceDetail.ShouldStartWith("grade-error:");
    }

    [Fact]
    public async Task Blank_command_entries_are_dropped_before_grading()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", " ", "check.sh", "")), Succeeded(), CancellationToken.None);

        grader.LastCommand.ShouldBe(new[] { "sh", "check.sh" });
    }

    // ─── fixtures ────────────────────────────────────────────────────────────────

    private static AgentRun Run() => new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid() };

    private static AgentTask TaskWith(SupervisorAcceptanceSpec? acceptance, bool? expectsChanges = null) =>
        new() { Goal = "g", Harness = "codex-cli", RepositoryId = Guid.NewGuid(), Acceptance = acceptance, ExpectsChanges = expectsChanges };

    private static SupervisorAcceptanceSpec Spec(params string[] command) => new() { Command = command };

    private static AgentRunResult Succeeded() => new()
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        ProducedBranch = "agent/s5-test",
        ChangedFiles = new[] { "a.cs" },
    };

    /// <summary>A patch-only producer (PR-2 policy, or a guard-blocked push): no pushed branch, but a real recorded diff a patch-based grade can act on.</summary>
    private static AgentRunResult SucceededPatchOnly() => new()
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        ProducedBranch = null,
        ChangedFiles = new[] { "a.cs" },
        BaseSha = "deadbeef",
        Patch = "diff --git a/a.cs b/a.cs\n",
    };

    private static (AgentRunExecutor Executor, FakeGrader Grader) NewExecutor(BenchmarkGrade grade)
    {
        var grader = new FakeGrader { Grade = grade };
        var executor = new AgentRunExecutor(null!, null!, null!, null!, null!, null!, null!, null!, new FakeScopeFactory(grader), null!, null!, null!, null!, null!, NullLogger<AgentRunExecutor>.Instance);
        return (executor, grader);
    }

    private sealed class FakeGrader : ISupervisorAcceptanceGrader
    {
        public BenchmarkGrade Grade { get; set; } = new() { Passed = true, Detail = "ok" };
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }
        public IReadOnlyList<string>? LastCommand { get; private set; }

        public int PatchCalls { get; private set; }
        public string? LastPatchBaseSha { get; private set; }

        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            Calls++;
            LastCommand = spec.Command;

            if (Throw is { } ex) throw ex;

            return System.Threading.Tasks.Task.FromResult(Grade);
        }

        public Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            PatchCalls++;
            LastPatchBaseSha = baseSha;
            LastCommand = spec.Command;

            if (Throw is { } ex) throw ex;

            return System.Threading.Tasks.Task.FromResult(Grade);
        }
    }

    private sealed class FakeScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly ISupervisorAcceptanceGrader _grader;
        public FakeScopeFactory(ISupervisorAcceptanceGrader grader) { _grader = grader; }

        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ISupervisorAcceptanceGrader) ? _grader
            : serviceType == typeof(CodeSpace.Core.Services.Agents.Mcp.IToolCallLedgerService) ? new NoBlockingLedger()
            : null;
        public void Dispose() { }
    }

    /// <summary>No blocking decision — the gate's A1 defer falls through to the grade (mirrors the output-review test's ledger fake).</summary>
    private sealed class NoBlockingLedger : IToolCallLedgerService
    {
        public Task<Guid?> FindBlockingDecisionIdAsync(Guid agentRunId, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

        public Task<ToolCallClaim> TryClaimAsync(Guid agentRunId, Guid teamId, string toolKind, string idempotencyKey, string inputHash, long fenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordTerminalAsync(Guid ledgerId, Guid teamId, ToolCallLedgerStatus status, string? resultJson, string? error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginApprovalAsync(Guid ledgerId, Guid teamId, string approvalToken, DateTimeOffset deadlineAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetApprovalMessageAsync(Guid ledgerId, Guid teamId, Guid messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginExecutionAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolCallApprovalState?> ReadApprovalStateAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryAnswerDecisionAsync(Guid ledgerId, Guid teamId, string answerJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetDecisionEnvelopeAsync(Guid ledgerId, Guid teamId, string envelopeJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExpiredToolApproval>> ExpireStaleApprovalsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TimedOutDecision>> ExpireStaleDecisionsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> ExpireStaleToolCallsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountPendingDecisionsAsync(Guid agentRunId, Guid teamId, string excludeIdempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

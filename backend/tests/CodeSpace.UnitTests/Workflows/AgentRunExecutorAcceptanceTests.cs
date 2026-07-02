using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
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

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), Task(acceptance: null), Succeeded(), CancellationToken.None);

        result.AcceptancePassed.ShouldBeNull();
        grader.Calls.ShouldBe(0, "no contract ⇒ the grader is never invoked");
    }

    [Fact]
    public async Task An_all_blank_command_reads_as_no_contract()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), Task(Spec(" ", "")), Succeeded(), CancellationToken.None);

        result.AcceptancePassed.ShouldBeNull();
        grader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_non_success_result_skips_the_gate()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var failed = Succeeded() with { Status = AgentRunStatus.Failed };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), Task(Spec("sh", "check.sh")), failed, CancellationToken.None);

        result.ShouldBe(failed, "a failed run is already the truth — nothing to gate");
        grader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_multi_repo_result_defers_exactly_like_the_supervisor_unit_gate()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var multi = Succeeded() with { RepositoryResults = new[] { new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "other" } } };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), Task(Spec("sh", "check.sh")), multi, CancellationToken.None);

        result.AcceptancePassed.ShouldBeNull("per-repo grading is a follow-on — deferred, never a blind primary-only grade");
        grader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_contract_with_no_branch_fails_closed_without_grading()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var noBranch = Succeeded() with { ProducedBranch = null };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), Task(Spec("sh", "check.sh")), noBranch, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe("acceptance-failed");
        result.AcceptancePassed.ShouldBe(false);
        result.AcceptanceDetail.ShouldBe("no-branch-or-repo");
        grader.Calls.ShouldBe(0, "there is nothing to clone — fail closed, never a phantom pass");
    }

    [Fact]
    public async Task A_passing_check_stamps_the_verdict_and_keeps_the_run_succeeded()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), Task(Spec("sh", "check.sh")), Succeeded(), CancellationToken.None);

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
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), Task(Spec("sh", "check.sh")), succeeded, CancellationToken.None);

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

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), Task(Spec("sh", "check.sh")), Succeeded(), CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.AcceptanceDetail.ShouldStartWith("grade-error:");
    }

    [Fact]
    public async Task Blank_command_entries_are_dropped_before_grading()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        await executor.GradeAcceptanceIfPresentAsync(Run(), Task(Spec("sh", " ", "check.sh", "")), Succeeded(), CancellationToken.None);

        grader.LastCommand.ShouldBe(new[] { "sh", "check.sh" });
    }

    // ─── fixtures ────────────────────────────────────────────────────────────────

    private static AgentRun Run() => new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid() };

    private static AgentTask Task(SupervisorAcceptanceSpec? acceptance) =>
        new() { Goal = "g", Harness = "codex-cli", RepositoryId = Guid.NewGuid(), Acceptance = acceptance };

    private static SupervisorAcceptanceSpec Spec(params string[] command) => new() { Command = command };

    private static AgentRunResult Succeeded() => new()
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        ProducedBranch = "agent/s5-test",
        ChangedFiles = new[] { "a.cs" },
    };

    private static (AgentRunExecutor Executor, FakeGrader Grader) NewExecutor(BenchmarkGrade grade)
    {
        var grader = new FakeGrader { Grade = grade };
        var executor = new AgentRunExecutor(null!, null!, null!, null!, null!, null!, null!, null!, new FakeScopeFactory(grader), null!, null!, null!, NullLogger<AgentRunExecutor>.Instance);
        return (executor, grader);
    }

    private sealed class FakeGrader : ISupervisorAcceptanceGrader
    {
        public BenchmarkGrade Grade { get; set; } = new() { Passed = true, Detail = "ok" };
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }
        public IReadOnlyList<string>? LastCommand { get; private set; }

        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, IReadOnlyList<string> command, int timeoutSeconds, CancellationToken cancellationToken, BenchmarkGradingKind kind = BenchmarkGradingKind.TestsPass)
        {
            Calls++;
            LastCommand = command;

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
        public object? GetService(Type serviceType) => serviceType == typeof(ISupervisorAcceptanceGrader) ? _grader : null;
        public void Dispose() { }
    }
}

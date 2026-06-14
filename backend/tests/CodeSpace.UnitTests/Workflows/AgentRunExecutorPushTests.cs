using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the flag-gated branch-push behaviour on <see cref="AgentRunExecutor"/>: the env-flag reader, the
/// deterministic branch name, and the <c>PushProducedBranchIfEnabledAsync</c> step's guards + best-effort
/// failure handling — driven with a recording fake <see cref="IWorkspacePushHandle"/> and a minimal
/// <see cref="IAgentRunService"/> stub (the only deps the step touches: the epoch re-read + the warning
/// append). With the flag OFF every run is byte-identical (no push, no handle interaction).
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunExecutorPushTests
{
    private const long ClaimedEpoch = 7;

    // ─── Env flag (Rule 8) ───────────────────────────────────────────────────

    [Fact]
    public void PushEnabledEnvVar_name_is_pinned() =>
        // Renaming this silently turns the feature OFF for any operator who enabled it via env (Rule 8).
        AgentRunExecutor.PushEnabledEnvVar.ShouldBe("CODESPACE_AGENT_PUSH_BRANCH_ENABLED");

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]   // trimmed
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("garbage", false)]
    public void IsPushEnabled_is_true_only_for_explicit_enable_values(string? value, bool expected)
    {
        var original = Environment.GetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, value);

            AgentRunExecutor.IsPushEnabled().ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, original);
        }
    }

    // ─── Deterministic branch name ───────────────────────────────────────────

    [Fact]
    public void BuildBranchName_pins_the_literal_format()
    {
        var id = Guid.Parse("0a8b6c4d-1e2f-3a4b-5c6d-7e8f9a0b1c2d");

        AgentRunExecutor.BuildBranchName(id).ShouldBe($"codespace/agent/{id:N}");
    }

    [Fact]
    public void BuildBranchName_is_deterministic_for_the_same_run_and_unique_across_runs()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        AgentRunExecutor.BuildBranchName(a).ShouldBe(AgentRunExecutor.BuildBranchName(a), "same run id → same branch");
        AgentRunExecutor.BuildBranchName(a).ShouldNotBe(AgentRunExecutor.BuildBranchName(b), "different run ids → different branches");
    }

    // ─── PushProducedBranchIfEnabledAsync guards ─────────────────────────────

    [Fact]
    public async Task Flag_off_returns_unchanged_and_never_touches_the_handle()
    {
        await WithFlagAsync(null, async () =>
        {
            var (runId, executor, runs) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new RecordingPushHandle();

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None);

            result.ProducedBranch.ShouldBeNull();
            handle.PushCalled.ShouldBeFalse("flag OFF → no side effect");
            runs.GetCalled.ShouldBeFalse("flag OFF short-circuits before the epoch re-read");
        });
    }

    [Fact]
    public async Task Non_succeeded_status_returns_unchanged_and_never_pushes() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new RecordingPushHandle();

            var failed = SucceededWithChanges() with { Status = AgentRunStatus.Failed };
            var result = await executor.PushProducedBranchIfEnabledAsync(runId, failed, handle, ClaimedEpoch, CancellationToken.None);

            result.ProducedBranch.ShouldBeNull();
            handle.PushCalled.ShouldBeFalse("a non-Succeeded run never pushes");
        });

    [Fact]
    public async Task Empty_changes_return_unchanged_and_never_push() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new RecordingPushHandle();

            var noChanges = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" };   // no ChangedFiles, no Patch
            var result = await executor.PushProducedBranchIfEnabledAsync(runId, noChanges, handle, ClaimedEpoch, CancellationToken.None);

            result.ProducedBranch.ShouldBeNull();
            handle.PushCalled.ShouldBeFalse("nothing changed → nothing to push");
        });

    [Fact]
    public async Task A_non_push_capable_handle_returns_unchanged() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, SucceededWithChanges(), new ReadOnlyHandle(), ClaimedEpoch, CancellationToken.None);

            result.ProducedBranch.ShouldBeNull("a handle that doesn't implement IWorkspacePushHandle is skipped");
        });

    [Fact]
    public async Task A_null_workspace_returns_unchanged() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, SucceededWithChanges(), workspace: null, ClaimedEpoch, CancellationToken.None);

            result.ProducedBranch.ShouldBeNull();
        });

    [Fact]
    public async Task All_guards_pass_folds_the_pushed_branch_into_the_result() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new RecordingPushHandle();

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None);

            handle.PushCalled.ShouldBeTrue();
            handle.BranchPushed.ShouldBe(AgentRunExecutor.BuildBranchName(runId), "the deterministic run-derived branch name is pushed");
            result.ProducedBranch.ShouldBe(handle.BranchPushed, "the pushed branch is folded into the result so the node's branch output carries it");
        });

    [Fact]
    public async Task A_null_push_result_leaves_the_result_unchanged() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new RecordingPushHandle { ReturnBranch = null };   // e.g. no changes to commit / anonymous clone

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None);

            handle.PushCalled.ShouldBeTrue();
            result.ProducedBranch.ShouldBeNull("a null push result means no branch — the result is unchanged");
        });

    [Fact]
    public async Task A_reclaimed_run_skips_the_push() =>
        await WithFlagAsync("1", async () =>
        {
            // The run was reclaimed after this executor claimed it: the persisted epoch no longer matches.
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch + 1);
            var handle = new RecordingPushHandle();

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None);

            handle.PushCalled.ShouldBeFalse("a reclaimed run (epoch bumped) skips the side effect — its completion loses the CAS anyway");
            result.ProducedBranch.ShouldBeNull();
        });

    // ─── Best-effort failure handling ────────────────────────────────────────

    [Fact]
    public async Task A_thrown_workspace_exception_is_swallowed_and_recorded_as_a_warning() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, runs) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new RecordingPushHandle { ThrowOnPush = new WorkspaceException("git push failed: token *** rejected") };

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None);

            result.Status.ShouldBe(AgentRunStatus.Succeeded, "a push failure NEVER flips a Succeeded run to Failed");
            result.ProducedBranch.ShouldBeNull();
            runs.AppendedEvents.Count.ShouldBe(1, "the operator sees a timeline warning explaining why no branch appeared");
            runs.AppendedEvents[0].Kind.ShouldBe(AgentEventKind.Warning);
            runs.AppendedEvents[0].Text.ShouldContain("***", customMessage: "the redacted exception message is carried onto the warning");
        });

    [Fact]
    public async Task A_cancellation_propagates() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new RecordingPushHandle { ThrowOnPush = new OperationCanceledException() };

            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await executor.PushProducedBranchIfEnabledAsync(runId, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None));
        });

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task WithFlagAsync(string? value, Func<Task> body)
    {
        var original = Environment.GetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, value);
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, original);
        }
    }

    private static AgentRunResult SucceededWithChanges() =>
        new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed", ChangedFiles = new[] { "src/foo.cs" }, Patch = "diff --git ..." };

    /// <summary>Build an executor whose only live dependency is a stub run service (the epoch re-read + the warning append); every other ctor dep is null because the push step never touches it.</summary>
    private static (Guid RunId, AgentRunExecutor Executor, StubRuns Runs) NewExecutor(long epoch)
    {
        var runId = Guid.NewGuid();
        var runs = new StubRuns(runId, epoch);
        var executor = new AgentRunExecutor(runs, null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<AgentRunExecutor>.Instance);
        return (runId, executor, runs);
    }

    private sealed class RecordingPushHandle : IWorkspaceHandle, IWorkspacePushHandle
    {
        public bool PushCalled { get; private set; }
        public string? BranchPushed { get; private set; }
        public string? ReturnBranch { get; set; } = "set-on-push";
        public Exception? ThrowOnPush { get; set; }

        public string Directory => "/tmp/fake";

        public Task<string?> PushChangesAsync(string branchName, CancellationToken cancellationToken)
        {
            PushCalled = true;
            BranchPushed = branchName;

            if (ThrowOnPush is not null) throw ThrowOnPush;

            // When the test wants the pushed name folded, return the branch the executor asked for.
            return Task.FromResult(ReturnBranch == "set-on-push" ? branchName : ReturnBranch);
        }

        public Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A read-only handle (NO IWorkspacePushHandle) — the feature-detect must skip it.</summary>
    private sealed class ReadOnlyHandle : IWorkspaceHandle
    {
        public string Directory => "/tmp/fake";
        public Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Minimal IAgentRunService: serves the epoch re-read (GetAsync) and records appended warning events. Every other member throws — the push step calls none of them.</summary>
    private sealed class StubRuns : IAgentRunService
    {
        private readonly Guid _runId;
        private readonly long _epoch;

        public bool GetCalled { get; private set; }
        public List<AgentEvent> AppendedEvents { get; } = new();

        public StubRuns(Guid runId, long epoch) { _runId = runId; _epoch = epoch; }

        public Task<AgentRun> GetAsync(Guid runId, CancellationToken cancellationToken)
        {
            GetCalled = true;
            return Task.FromResult(new AgentRun { Id = _runId, FenceEpoch = _epoch });
        }

        public Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken)
        {
            AppendedEvents.Add(@event);
            return Task.FromResult(new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = runId, Kind = @event.Kind, Text = @event.Text });
        }

        public Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ReclaimForReattachAsync(Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetRunnerHandleAsync(Guid runId, string handleJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Guid runId, AgentRunResult result, long expectedEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelQueuedAsync(Guid runId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelRunningAsync(Guid runId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CodeSpace.Messages.Dtos.Agents.AgentRunSummary?> GetSummaryForTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentRunEvent>> GetEventsAsync(Guid runId, Guid teamId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

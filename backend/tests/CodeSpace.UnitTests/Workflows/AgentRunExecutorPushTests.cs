using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;
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

    // ─── MCP endpoint gate (per-run opt-in OR ambient flag) ──────────────────

    [Fact]
    public void McpEndpointEnabledEnvVar_name_is_pinned() =>
        // Renaming this silently turns the fabric OFF for any operator who enabled it via env (Rule 8).
        AgentRunExecutor.McpEndpointEnabledEnvVar.ShouldBe("CODESPACE_AGENT_MCP_ENDPOINT_ENABLED");

    [Theory]
    // ambient flag value × per-run opt-in → resolved gate. The gate is the OR of the two: a per-run opt-in can
    // turn the fabric ON without flipping the ambient flag, but cannot turn it OFF when the operator enabled it.
    [InlineData(null, null, false)]    // neither → off (byte-identical to today)
    [InlineData(null, false, false)]   // explicit per-run false ≠ on (still defers to ambient)
    [InlineData(null, true, true)]     // per-run opt-in turns it on with the ambient flag off — the benchmark cli-mcp case
    [InlineData("1", null, true)]      // ambient on → on regardless of the per-run signal
    [InlineData("1", false, true)]     // ambient wins (no per-run OFF override)
    [InlineData("1", true, true)]
    public void ShouldOpenMcpEndpoint_is_the_or_of_the_ambient_flag_and_the_per_run_opt_in(string? envValue, bool? perRunOptIn, bool expected)
    {
        var original = Environment.GetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar, envValue);

            var task = new AgentTask { Goal = "g", Harness = "codex-cli", EnableMcpEndpoint = perRunOptIn };

            AgentRunExecutor.ShouldOpenMcpEndpoint(task).ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar, original);
        }
    }

    // ─── Health / graceful degradation: the boot readiness diagnostic ────────

    [Fact]
    public void LogMcpProxyReadiness_warns_clearly_when_the_endpoint_is_on_but_the_proxy_is_missing()
    {
        WithMcpEnv(endpoint: "true", proxyPath: "/nonexistent/codespace-mcp", () =>
        {
            var logger = new CapturingLogger();

            AgentRunExecutor.LogMcpProxyReadiness(logger);

            var warning = logger.Entries.ShouldHaveSingleItem();
            warning.Level.ShouldBe(LogLevel.Warning, customMessage: "a missing proxy under an enabled endpoint must be a clear fail-closed Warning, not silent");
            warning.Message.ShouldContain("/nonexistent/codespace-mcp", customMessage: "the diagnostic names the resolved path the operator must fix");
            warning.Message.ShouldContain("TOOL-LESS", customMessage: "the diagnostic states the consequence (runs fail closed to a tool-less run)");
        });
    }

    [Fact]
    public void LogMcpProxyReadiness_confirms_at_information_when_the_endpoint_is_on_and_the_proxy_resolves()
    {
        // /bin/sh always exists on the POSIX CI; on Windows fall back to any present file so the test is cross-host.
        var presentBinary = OperatingSystem.IsWindows() ? Environment.ProcessPath! : "/bin/sh";

        WithMcpEnv(endpoint: "true", proxyPath: presentBinary, () =>
        {
            var logger = new CapturingLogger();

            AgentRunExecutor.LogMcpProxyReadiness(logger);

            logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Information, customMessage: "a present proxy under an enabled endpoint is a confirming Information line");
        });
    }

    [Fact]
    public void LogMcpProxyReadiness_is_silent_when_the_endpoint_is_off()
    {
        WithMcpEnv(endpoint: null, proxyPath: "/nonexistent/codespace-mcp", () =>
        {
            var logger = new CapturingLogger();

            AgentRunExecutor.LogMcpProxyReadiness(logger);

            logger.Entries.ShouldBeEmpty(customMessage: "endpoint OFF → no proxy to warn about → no log (byte-identical to today)");
        });
    }

    // ─── Proxy packaging CI smoke ────────────────────────────────────────────

    [Fact]
    public void The_codespace_mcp_proxy_binary_is_packaged_at_the_resolved_path()
    {
        // CI smoke: the proxy must be co-located with the running assembly at LocalProcessRunner.McpProxyBinaryPath()
        // (the SAME default the API publish target lands it at via the build-only ProjectReference + copy target). This
        // test bin gets it through the UnitTests project's ProjectReference to CodeSpace.Mcp — the same packaging
        // mechanism. A regression (the copy target dropped, the assembly name changed) fails here, in always-run unit CI,
        // BEFORE a deployment discovers a tool-less run. Skip the env override so we assert the DEFAULT resolution.
        var original = Environment.GetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, null);

            File.Exists(LocalProcessRunner.McpProxyBinaryPath()).ShouldBeTrue(
                customMessage: $"codespace-mcp must be packaged next to the assembly at '{LocalProcessRunner.McpProxyBinaryPath()}'. If this fails, the API csproj CopyMcpProxyToOutput/PublishMcpProxy target or the CodeSpace.Mcp ProjectReference regressed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, original);
        }
    }

    private static void WithMcpEnv(string? endpoint, string? proxyPath, Action body)
    {
        var prevEndpoint = Environment.GetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar);
        var prevProxy = Environment.GetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar, endpoint);
            Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, proxyPath);

            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.McpEndpointEnabledEnvVar, prevEndpoint);
            Environment.SetEnvironmentVariable(LocalProcessRunner.McpProxyPathEnvVar, prevProxy);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
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

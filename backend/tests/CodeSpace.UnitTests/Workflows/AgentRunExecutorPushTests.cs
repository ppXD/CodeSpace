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
[Collection("McpEndpointEnvMutation")]   // serialize with McpCatalogModeTests — both mutate CODESPACE_AGENT_MCP_ENDPOINT_ENABLED
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

    // ─── Branch-push gate (per-run opt-in OR ambient flag) ───────────────────

    [Theory]
    // ambient flag value × per-run opt-in → resolved gate. The gate is the OR of the two: a per-run opt-in turns
    // push ON without flipping the ambient flag, but cannot turn it OFF when the operator enabled it deployment-wide.
    [InlineData(null, null, false)]    // neither → off (byte-identical to today: an ordinary run pushes nothing)
    [InlineData(null, false, false)]   // explicit per-run false ≠ on (still defers to ambient)
    [InlineData(null, true, true)]     // per-run opt-in turns it on with the ambient flag off — the fan-out branch-agent case
    [InlineData("1", null, true)]      // ambient on → on regardless of the per-run signal
    [InlineData("1", false, true)]     // ambient wins (no per-run OFF override)
    [InlineData("1", true, true)]
    public void ShouldPushProducedBranch_is_the_or_of_the_ambient_flag_and_the_per_run_opt_in(string? envValue, bool? perRunOptIn, bool expected)
    {
        var original = Environment.GetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, envValue);

            var task = new AgentTask { Goal = "g", Harness = "codex-cli", PushProducedBranch = perRunOptIn };

            AgentRunExecutor.ShouldPushProducedBranch(task).ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, original);
        }
    }

    // ─── Branch-integration gate (per-run opt-in OR ambient flag) ────────────

    [Fact]
    public void IntegrateBranchEnabledEnvVar_name_is_pinned() =>
        // Renaming this silently turns on-disk integration OFF for any operator who enabled it via env (Rule 8).
        AgentRunExecutor.IntegrateBranchEnabledEnvVar.ShouldBe("CODESPACE_AGENT_INTEGRATE_BRANCH_ENABLED");

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
    public void IsIntegrateEnabled_is_true_only_for_explicit_enable_values(string? value, bool expected)
    {
        var original = Environment.GetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, value);

            AgentRunExecutor.IsIntegrateEnabled().ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, original);
        }
    }

    [Theory]
    // ambient flag value × per-run opt-in → resolved gate. The OR shape mirrors push exactly: a per-run opt-in turns
    // integration ON without flipping the ambient flag, but cannot turn it OFF when the operator enabled it.
    [InlineData(null, false, false)]   // neither → off (byte-identical to today: the merge produces only the side-by-side fold)
    [InlineData(null, true, true)]     // per-run opt-in turns it on with the ambient flag off
    [InlineData("1", false, true)]     // ambient on → on regardless of the per-run signal
    [InlineData("1", true, true)]
    public void ShouldIntegrate_is_the_or_of_the_ambient_flag_and_the_per_run_opt_in(string? envValue, bool perRunOptIn, bool expected)
    {
        var original = Environment.GetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, envValue);

            AgentRunExecutor.ShouldIntegrate(perRunOptIn).ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.IntegrateBranchEnabledEnvVar, original);
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
    public void LogMcpProxyReadiness_warns_when_the_proxy_is_missing_even_with_the_full_fabric_off()
    {
        // The endpoint now opens for EVERY run (read-only tools by default), so a missing proxy makes EVERY run tool-less
        // — including the read-only get_context. The boot diagnostic must warn even when the full-fabric flag is off.
        WithMcpEnv(endpoint: null, proxyPath: "/nonexistent/codespace-mcp", () =>
        {
            var logger = new CapturingLogger();

            AgentRunExecutor.LogMcpProxyReadiness(logger);

            var warning = logger.Entries.ShouldHaveSingleItem();
            warning.Level.ShouldBe(LogLevel.Warning, customMessage: "the read-only endpoint opens by default, so a missing proxy is a fail-closed Warning even with the full fabric off");
            warning.Message.ShouldContain("TOOL-LESS");
        });
    }

    [Fact]
    public void LogMcpProxyReadiness_confirms_at_information_with_the_proxy_present_and_the_full_fabric_off()
    {
        // The full-fabric flag off is the DEFAULT: read-only tools serve, the side-effecting fabric is opt-in. A present
        // proxy is a confirming Information line (not silent) since the read-only endpoint will open.
        var presentBinary = OperatingSystem.IsWindows() ? Environment.ProcessPath! : "/bin/sh";

        WithMcpEnv(endpoint: null, proxyPath: presentBinary, () =>
        {
            var logger = new CapturingLogger();

            AgentRunExecutor.LogMcpProxyReadiness(logger);

            var info = logger.Entries.ShouldHaveSingleItem();
            info.Level.ShouldBe(LogLevel.Information);
            info.Message.ShouldContain("opt-in", customMessage: "the line notes the full side-effecting fabric is opt-in per run when the ambient flag is off");
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

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None);

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
            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, failed, handle, ClaimedEpoch, CancellationToken.None);

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
            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, noChanges, handle, ClaimedEpoch, CancellationToken.None);

            result.ProducedBranch.ShouldBeNull();
            handle.PushCalled.ShouldBeFalse("nothing changed → nothing to push");
        });

    [Fact]
    public async Task A_non_push_capable_handle_returns_unchanged() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, SucceededWithChanges(), new ReadOnlyHandle(), ClaimedEpoch, CancellationToken.None);

            result.ProducedBranch.ShouldBeNull("a handle that doesn't implement IWorkspacePushHandle is skipped");
        });

    [Fact]
    public async Task A_null_workspace_returns_unchanged() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, SucceededWithChanges(), workspace: null, ClaimedEpoch, CancellationToken.None);

            result.ProducedBranch.ShouldBeNull();
        });

    [Fact]
    public async Task All_guards_pass_folds_the_pushed_branch_into_the_result() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new RecordingPushHandle();

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None);

            handle.PushCalled.ShouldBeTrue();
            handle.BranchPushed.ShouldBe(AgentRunExecutor.BuildBranchName(runId), "the deterministic run-derived branch name is pushed");
            result.ProducedBranch.ShouldBe(handle.BranchPushed, "the pushed branch is folded into the result so the node's branch output carries it");
        });

    [Fact]
    public async Task Per_run_opt_in_pushes_with_the_ambient_flag_off() =>
        // The one-agent-one-branch fan-out case: the env flag is OFF, but the task opted in per-run → the branch
        // is pushed for THIS run without flipping the global flag (every other run stays byte-identical).
        await WithFlagAsync(null, async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new RecordingPushHandle();

            var optedIn = new AgentTask { Goal = "g", Harness = "codex-cli", PushProducedBranch = true };
            var result = await executor.PushProducedBranchIfEnabledAsync(runId, optedIn, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None);

            handle.PushCalled.ShouldBeTrue("the per-run opt-in pushes even with the ambient flag off");
            result.ProducedBranch.ShouldBe(handle.BranchPushed, "the pushed branch is folded into the result");
        });

    [Fact]
    public async Task A_null_push_result_leaves_the_result_unchanged() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new RecordingPushHandle { ReturnBranch = null };   // e.g. no changes to commit / anonymous clone

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None);

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

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None);

            handle.PushCalled.ShouldBeFalse("a reclaimed run (epoch bumped) skips the side effect — its completion loses the CAS anyway");
            result.ProducedBranch.ShouldBeNull();
        });

    // ─── Multi-repo per-repo push (multi-repo PR3) ───────────────────────────

    [Fact]
    public void ChangeSetIdFor_is_run_id_derived() =>
        // Pinned: the change-set id is the stable run-id-derived handle a downstream integration references.
        AgentRunExecutor.ChangeSetIdFor(Guid.Parse("11111111-1111-1111-1111-111111111111"))
            .ShouldBe("cs-11111111111111111111111111111111");

    [Fact]
    public async Task A_multi_repo_run_pushes_each_writable_repo_and_folds_per_repo_branches() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new MultiRepoRecordingPushHandle();

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, MultiRepoSucceeded(runId), handle, ClaimedEpoch, CancellationToken.None);

            var expected = AgentRunExecutor.BuildBranchName(runId);
            handle.PushedByAlias.Keys.ShouldBe(new[] { "web", "api" }, ignoreOrder: true, "every writable repo is pushed, each to its own remote");
            handle.PushedByAlias["web"].ShouldBe(expected, "each repo pushes under the same run-derived branch name (distinct remotes)");
            handle.PushedByAlias["api"].ShouldBe(expected);

            result.RepositoryResults.Single(r => r.Alias == "web").ProducedBranch.ShouldBe(expected);
            result.RepositoryResults.Single(r => r.Alias == "api").ProducedBranch.ShouldBe(expected);
            result.ProducedBranch.ShouldBe(expected, "the top-level ProducedBranch mirrors the PRIMARY (web) repo's branch");
        });

    [Fact]
    public async Task A_multi_repo_run_records_null_for_a_secondary_repo_that_did_not_change() =>
        await WithFlagAsync("1", async () =>
        {
            // The agent touched only the primary; the secondary's push self-gates to null. The change set records
            // the per-repo truth (web a branch, api none) and the top-level still mirrors the primary.
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new MultiRepoRecordingPushHandle { NullForAliases = { "api" } };

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, MultiRepoSucceeded(runId), handle, ClaimedEpoch, CancellationToken.None);

            var expected = AgentRunExecutor.BuildBranchName(runId);
            result.RepositoryResults.Single(r => r.Alias == "web").ProducedBranch.ShouldBe(expected);
            result.RepositoryResults.Single(r => r.Alias == "api").ProducedBranch.ShouldBeNull("the unchanged secondary repo produced no branch");
            result.ProducedBranch.ShouldBe(expected, "the top-level still mirrors the primary's branch");
        });

    [Fact]
    public async Task A_multi_repo_run_pushes_even_when_the_primary_top_level_shows_no_change() =>
        await WithFlagAsync("1", async () =>
        {
            // Multi-repo skips the single-repo "top-level empty → return" gate: a secondary repo may carry changes
            // the primary's top-level fields don't reflect. Each per-repo push self-gates instead.
            var (runId, executor, _) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new MultiRepoRecordingPushHandle();

            // Top-level ChangedFiles + Patch are EMPTY (primary unchanged) but RepositoryResults still lists both repos.
            var input = MultiRepoSucceeded(runId) with { ChangedFiles = Array.Empty<string>(), Patch = "" };

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, input, handle, ClaimedEpoch, CancellationToken.None);

            handle.PushedByAlias.Count.ShouldBe(2, "the empty top-level gate does not short-circuit a multi-repo push");
            result.RepositoryResults.Single(r => r.Alias == "api").ProducedBranch.ShouldBe(AgentRunExecutor.BuildBranchName(runId));
        });

    [Fact]
    public async Task A_multi_repo_per_repo_push_failure_is_isolated_keeping_the_others() =>
        await WithFlagAsync("1", async () =>
        {
            // The core push-isolation fix: 'web' pushes successfully, then 'api' throws. 'api''s failure must NOT
            // discard 'web''s already-pushed branch (the orphaned-litter bug); 'api' gets a per-repo warning + null
            // branch; the run stays Succeeded.
            var (runId, executor, runs) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new MultiRepoRecordingPushHandle { ThrowForAliases = { "api" } };

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, MultiRepoSucceeded(runId), handle, ClaimedEpoch, CancellationToken.None);

            var expected = AgentRunExecutor.BuildBranchName(runId);
            result.Status.ShouldBe(AgentRunStatus.Succeeded, "one repo's push failure never fails the run");
            result.RepositoryResults.Single(r => r.Alias == "web").ProducedBranch.ShouldBe(expected, "the repo that pushed BEFORE the failure keeps its branch — never discarded");
            result.RepositoryResults.Single(r => r.Alias == "api").ProducedBranch.ShouldBeNull("the failed repo has no branch");
            result.ProducedBranch.ShouldBe(expected, "the top-level still mirrors the primary's branch");

            runs.AppendedEvents.Count.ShouldBe(1, "the operator gets a per-repo warning naming the failed repo");
            runs.AppendedEvents[0].Kind.ShouldBe(AgentEventKind.Warning);
            runs.AppendedEvents[0].Text.ShouldContain("[api]", customMessage: "the warning names the failed repo");
        });

    // ─── Best-effort failure handling ────────────────────────────────────────

    [Fact]
    public async Task A_thrown_workspace_exception_is_swallowed_and_recorded_as_a_warning() =>
        await WithFlagAsync("1", async () =>
        {
            var (runId, executor, runs) = NewExecutor(epoch: ClaimedEpoch);
            var handle = new RecordingPushHandle { ThrowOnPush = new WorkspaceException("git push failed: token *** rejected") };

            var result = await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None);

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
                await executor.PushProducedBranchIfEnabledAsync(runId, DefaultTask, SucceededWithChanges(), handle, ClaimedEpoch, CancellationToken.None));
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

    /// <summary>A succeeded MULTI-repo result as the Enrich step would build it: top-level = primary (web), plus a RepositoryResults entry per writable repo (web primary + api) and the run's change-set id.</summary>
    private static AgentRunResult MultiRepoSucceeded(Guid runId) => new()
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        ChangedFiles = new[] { "web.txt" },
        Patch = "diff --git a/web.txt b/web.txt",
        BaseSha = "base-web",
        ChangeSetId = AgentRunExecutor.ChangeSetIdFor(runId),
        RepositoryResults = new[]
        {
            new RepositoryRunResult { Alias = "web", ChangedFiles = new[] { "web.txt" }, BaseSha = "base-web" },
            new RepositoryRunResult { Alias = "api", ChangedFiles = new[] { "api.txt" }, BaseSha = "base-api" },
        },
    };

    /// <summary>A task with NO per-run push opt-in — so the push decision in these tests is driven purely by the env flag (the gate's OR is exercised separately by the per-run tests). Mirrors how an ordinary run looks.</summary>
    private static AgentTask DefaultTask => new() { Goal = "g", Harness = "codex-cli" };

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

        public string PrimaryAlias => "repo";

        // Empty (Count 0, not >1) keeps the executor on the single-repo push path; the alias overloads are never hit.
        public IReadOnlyList<WorkspaceRepositoryHandle> Repositories => Array.Empty<WorkspaceRepositoryHandle>();

        public Task<string?> PushChangesAsync(string branchName, CancellationToken cancellationToken)
        {
            PushCalled = true;
            BranchPushed = branchName;

            if (ThrowOnPush is not null) throw ThrowOnPush;

            // When the test wants the pushed name folded, return the branch the executor asked for.
            return Task.FromResult(ReturnBranch == "set-on-push" ? branchName : ReturnBranch);
        }

        public Task<string?> PushChangesAsync(string alias, string branchName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("single-repo push path only");

        public Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceChanges> CaptureChangesAsync(string alias, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A MULTI-repo push handle (web primary + api, both writable) recording each per-alias push. Drives the executor's multi-repo push fan-out; a configurable alias set returns null to model an unchanged repo.</summary>
    private sealed class MultiRepoRecordingPushHandle : IWorkspaceHandle, IWorkspacePushHandle
    {
        public Dictionary<string, string?> PushedByAlias { get; } = new();
        public HashSet<string> NullForAliases { get; } = new();
        public HashSet<string> ThrowForAliases { get; } = new();

        public string Directory => "/tmp/fake-multi";
        public string PrimaryAlias => "web";

        public IReadOnlyList<WorkspaceRepositoryHandle> Repositories => new[]
        {
            new WorkspaceRepositoryHandle { Alias = "web", Directory = "/tmp/fake-multi/web", Access = WorkspaceAccess.Write },
            new WorkspaceRepositoryHandle { Alias = "api", Directory = "/tmp/fake-multi/api", Access = WorkspaceAccess.Write },
        };

        public Task<string?> PushChangesAsync(string alias, string branchName, CancellationToken cancellationToken)
        {
            if (ThrowForAliases.Contains(alias)) throw new WorkspaceException($"git push failed for '{alias}': *** rejected");

            var pushed = NullForAliases.Contains(alias) ? null : branchName;
            PushedByAlias[alias] = pushed;
            return Task.FromResult(pushed);
        }

        public Task<string?> PushChangesAsync(string branchName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("multi-repo path uses the alias overload");

        public Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkspaceChanges> CaptureChangesAsync(string alias, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A read-only handle (NO IWorkspacePushHandle) — the feature-detect must skip it.</summary>
    private sealed class ReadOnlyHandle : IWorkspaceHandle
    {
        public string Directory => "/tmp/fake";
        public string PrimaryAlias => "repo";
        public IReadOnlyList<WorkspaceRepositoryHandle> Repositories => Array.Empty<WorkspaceRepositoryHandle>();
        public Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkspaceChanges> CaptureChangesAsync(string alias, CancellationToken cancellationToken) => throw new NotSupportedException();
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

        public Task AppendEventsAsync(Guid runId, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken)
        {
            AppendedEvents.AddRange(events);
            return Task.CompletedTask;
        }

        public Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, string iterationKey = "", CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

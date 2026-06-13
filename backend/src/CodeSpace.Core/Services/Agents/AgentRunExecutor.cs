using System.Text.Json;
using System.Net.Sockets;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Runs one already-created (Queued) agent run to a terminal state: claims it, runs the harness in its
/// sandbox while streaming normalized events to the durable log, and lands the result. This is the
/// execution core a worker (the agent.code node's Hangfire job) invokes — substrate-neutral, driving
/// everything through the harness + runner contracts so any harness/runner combination behaves the same.
///
/// <para><b>Exactly-once:</b> the claim is a CAS (<see cref="IAgentRunService.MarkRunningAsync"/>); if the
/// run is already Running or terminal (a re-claimed Hangfire job after a crash, a duplicate dispatch),
/// the executor returns WITHOUT spawning the harness — so an agent never runs twice and tokens aren't
/// re-spent. A worker torn down mid-run (pod shutdown) leaves the run Running for the reconciler / a
/// re-claim; any other failure lands a clean Failed instead of a stuck Running.</para>
/// </summary>
public interface IAgentRunExecutor
{
    Task ExecuteAsync(Guid agentRunId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-attach to an already-<see cref="AgentRunStatus.Running"/> durable run whose original worker vanished
    /// (a backend restart) but whose detached supervisor is still alive — dispatched by the reconciler after it
    /// re-claimed the run (bumped the fence epoch + re-leased). Unlike <see cref="ExecuteAsync"/> it does NOT
    /// claim or launch: it resumes tailing the persisted spool from the handle's checkpoint offset (no duplicate
    /// events), folds the result from the streamed events + exit code (NO git diff — the workspace clone didn't
    /// survive the restart), and completes under the run's current (reclaim-bumped) epoch. A no-op if the run is
    /// already terminal or carries no durable handle.
    /// </summary>
    Task ReattachAsync(Guid agentRunId, CancellationToken cancellationToken);
}

public sealed class AgentRunExecutor : IAgentRunExecutor, IScopedDependency
{
    /// <summary>Runner used when the task doesn't pin one. v0 = the in-process local runner.</summary>
    private const string DefaultRunnerKind = "local";

    /// <summary>Cap on the captured diff inlined into the persisted result row (~1 MB). A larger diff is truncated with a marker; the full diff belongs in the artifact layer (a later slice).</summary>
    private const int MaxPatchChars = 1_000_000;

    /// <summary>
    /// Operators opt INTO pushing a successful run's diff to a remote branch (a side-effecting write to the
    /// user's remote) by setting this to "1"/"true". Fail-closed default-OFF (absent/""/"0"/"false"/anything
    /// else → no push), so every existing run is byte-identical until an operator flips it. Pinned by a test
    /// (Rule 8) — renaming it silently turns the feature off for an operator who enabled it.
    /// </summary>
    public const string PushEnabledEnvVar = "CODESPACE_AGENT_PUSH_BRANCH_ENABLED";

    /// <summary>
    /// Operators opt INTO the in-process MCP endpoint (the run-scoped tool-fabric server a CLI harness can later
    /// reach) by setting this to "1"/"true". Fail-closed default-OFF (absent/""/"0"/"false"/anything else → no
    /// endpoint is minted, so the run is byte-identical to today). Pinned by a test (Rule 8) — renaming it silently
    /// turns the feature off for an operator who enabled it.
    /// </summary>
    public const string McpEndpointEnabledEnvVar = "CODESPACE_AGENT_MCP_ENDPOINT_ENABLED";

    private static readonly IReadOnlyDictionary<string, string> EmptySecretEnv = new Dictionary<string, string>();

    private readonly IAgentRunService _runs;
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IAgentWorkspaceResolver _workspaceResolver;
    private readonly IModelCredentialResolver _modelCredentials;
    private readonly IWorkspaceProviderRegistry _workspaces;
    private readonly IAgentRunCompletionNotifier _notifier;
    // Mints a fresh DI scope (→ its own DbContext) for the heartbeat loop, which runs concurrently with the event stream.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentRunExecutor> _logger;

    public AgentRunExecutor(IAgentRunService runs, IAgentHarnessRegistry harnesses, ISandboxRunnerRegistry runners, IAgentWorkspaceResolver workspaceResolver, IModelCredentialResolver modelCredentials, IWorkspaceProviderRegistry workspaces, IAgentRunCompletionNotifier notifier, IServiceScopeFactory scopeFactory, ILogger<AgentRunExecutor> logger)
    {
        _runs = runs;
        _harnesses = harnesses;
        _runners = runners;
        _workspaceResolver = workspaceResolver;
        _modelCredentials = modelCredentials;
        _workspaces = workspaces;
        _notifier = notifier;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(Guid agentRunId, CancellationToken cancellationToken)
    {
        var run = await _runs.GetAsync(agentRunId, cancellationToken).ConfigureAwait(false);

        if (await TryClaimAsync(agentRunId, cancellationToken).ConfigureAwait(false) is not { } claimedEpoch) return;

        // One heartbeat spans the ENTIRE execution — streaming AND the post-CLI tail (git-diff capture +
        // completion). The tail used to run un-heartbeated, so a slow capture on a large repo could outlast the
        // reconciler's liveness window and falsely abandon a run that was actually finishing (which then races
        // the real completion and resumes the parent node with a non-terminal status). Pinging on a DEDICATED DI
        // scope — its own DbContext — because it runs concurrently with the event-append path (not thread-safe
        // to share). Cancelled + awaited in the finally, the moment work ends (or the worker is torn down).
        using var heartbeatScope = _scopeFactory.CreateScope();
        var heartbeatRuns = heartbeatScope.ServiceProvider.GetRequiredService<IAgentRunService>();
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatLoop.RunAsync(
            ct => heartbeatRuns.HeartbeatAsync(agentRunId, ct),
            AgentRunLiveness.HeartbeatInterval,
            ex => _logger.LogWarning(ex, "Heartbeat ping failed for agent run {RunId}; will retry next interval", agentRunId),
            heartbeatCts.Token);

        // Holds the run's resolved secret(s) once the credential is resolved (below), so the catch-all can scrub
        // them from a failure message too. None until then — a pre-resolve failure has no secret to leak.
        var redactor = SecretRedactor.None;

        // The workspace clone is disposed in the finally on a TERMINAL exit (success / failure), but DELIBERATELY
        // left in place when the worker is torn down (OperationCanceledException): the setsid-detached agent is
        // still running with its cwd inside this clone, so deleting it would pull the directory out from under the
        // live process and corrupt the run. The re-attach reuses the surviving clone, and the workspace janitor
        // reaps it by age if no re-attach ever claims it.
        IWorkspaceHandle? workspace = null;
        var leaveWorkspaceForReattach = false;

        try
        {
            var task = JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)
                       ?? throw new InvalidOperationException($"AgentRun {agentRunId} has an empty task envelope.");

            var harness = _harnesses.Resolve(task.Harness);
            var runnerKind = string.IsNullOrWhiteSpace(task.RunnerKind) ? DefaultRunnerKind : task.RunnerKind;
            var runner = _runners.Resolve(runnerKind);

            // Materialise the workspace (clone the bound repo) before the harness runs. Null = no workspace
            // for this run. The handle's lifetime is the run's — DisposeAsync removes the clone afterwards.
            var workspaceRequest = await _workspaceResolver.ResolveAsync(task, run.TeamId, cancellationToken).ConfigureAwait(false);
            workspace = workspaceRequest is null ? null : await _workspaces.Resolve(runnerKind).PrepareAsync(workspaceRequest, cancellationToken).ConfigureAwait(false);

            // Resolve + decrypt the model credential JUST-IN-TIME (team from the run row, never the envelope) and
            // project it onto the harness's env vars. The secret lives only in this in-memory effectiveTask →
            // SandboxSpec.Environment; it is NEVER re-persisted (CompleteAsync writes only the result). The
            // redactor (keyed on the decrypted key) strips it from any echoed event / error before it persists.
            var (secretEnv, secretRedactor) = await ResolveModelCredentialEnvAsync(task, run.TeamId, harness, cancellationToken).ConfigureAwait(false);
            redactor = secretRedactor;

            var effectiveTask = (workspace is null ? task : task with { WorkspaceDirectory = workspace.Directory }) with { Environment = MergeEnvironment(task.Environment, secretEnv) };

            // Open the per-run MCP endpoint (flag-OFF → null → no-op). It lives ONLY for the harness span: the harness
            // runs synchronously here (RunHarnessAsync → AttachAsync blocks until exit), and `await using` inside the
            // try tears it down on EVERY exit (success / cancel / generic catch) — NOT gated on leaveWorkspaceForReattach.
            await using var mcp = await OpenMcpEndpointIfEnabledAsync(agentRunId, effectiveTask, run.TeamId, cancellationToken).ConfigureAwait(false);

            var result = await RunHarnessAsync(agentRunId, harness, runner, harness.BuildInvocation(effectiveTask), redactor, cancellationToken).ConfigureAwait(false);

            result = await EnrichWithWorkspaceChangesAsync(agentRunId, result, workspace, cancellationToken).ConfigureAwait(false);

            result = await PushProducedBranchIfEnabledAsync(agentRunId, result, workspace, claimedEpoch, cancellationToken).ConfigureAwait(false);

            await CompleteAndNotifyAsync(agentRunId, result, claimedEpoch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Worker torn down (pod shutdown): leave the run Running for the reconciler / a re-claim — do NOT
            // complete, and do NOT delete the workspace (the detached agent is still running inside it; see above).
            leaveWorkspaceForReattach = true;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent run {RunId} failed during execution", agentRunId);
            await CompleteAndNotifyAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "executor-error", Error = redactor.Redact(ex.Message) }, claimedEpoch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat.ConfigureAwait(false);

            // Terminal exit (success / failure) owns the clone's cleanup; a worker tear-down leaves it for re-attach.
            if (workspace is not null && !leaveWorkspaceForReattach)
                await workspace.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task ReattachAsync(Guid agentRunId, CancellationToken cancellationToken)
    {
        var run = await _runs.GetAsync(agentRunId, cancellationToken).ConfigureAwait(false);

        if (run.Status != AgentRunStatus.Running) return;   // already landed terminal (completed/recovered) — nothing to re-attach

        if (DeserializeHandle(run.RunnerHandleJson) is not { } handle) return;   // no durable handle — the reconciler marker-recovers it instead

        var task = JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)
                   ?? throw new InvalidOperationException($"AgentRun {agentRunId} has an empty task envelope.");

        var harness = _harnesses.Resolve(task.Harness);

        if (_runners.All.FirstOrDefault(r => r.Kind == handle.Kind) is not ISandboxDurableRunner durable) return;

        // Heartbeat spans the whole re-tail (its own DI scope, like ExecuteAsync) so the lease stays fresh and the
        // reconciler doesn't reclaim the run out from under this re-attach.
        using var heartbeatScope = _scopeFactory.CreateScope();
        var heartbeatRuns = heartbeatScope.ServiceProvider.GetRequiredService<IAgentRunService>();
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatLoop.RunAsync(
            ct => heartbeatRuns.HeartbeatAsync(agentRunId, ct),
            AgentRunLiveness.HeartbeatInterval,
            ex => _logger.LogWarning(ex, "Heartbeat ping failed for re-attached agent run {RunId}; will retry next interval", agentRunId),
            heartbeatCts.Token);

        // Complete under the run's CURRENT epoch — the reconciler's reclaim just bumped it, and its fresh lease
        // blocks another reclaim for the lease window, so this is stably our epoch. A revived original observer
        // (stale epoch) loses the completion CAS.
        var expectedEpoch = run.FenceEpoch;

        try
        {
            // NOTE: deliberately NO branch push on the re-attach path — the workspace clone didn't survive the
            // backend restart (re-attach folds the result from the spool + exit code, never a git diff), so there
            // is nothing to commit or push. A run that needed its branch on a remote must produce it on the
            // original ExecuteAsync path.
            var result = await ReattachAndFoldAsync(agentRunId, durable, handle, task, run.TeamId, harness, cancellationToken).ConfigureAwait(false);

            if (result is null) return;   // couldn't safely observe (no redactor, still running) — leave Running for a later sweep

            await CompleteAndNotifyAsync(agentRunId, result, expectedEpoch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;   // worker torn down again — leave Running for the next re-attach
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent run {RunId} failed during re-attach", agentRunId);
            await CompleteAndNotifyAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "reattach-error", Error = "The agent run could not be re-attached after a restart and was failed." }, expectedEpoch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-tail the durable spool from the handle's checkpoint offset, redacting + appending each parsed event,
    /// and fold the harness result (events + exit code) — NO git diff (the workspace clone didn't survive the
    /// restart). The redactor is rebuilt by RE-RESOLVING the credential PURELY for redaction (not injected — the
    /// CLI already ran): the tail may echo a secret and the append-only log can't be edited, so redaction-on-write
    /// is the only safe point. The rebuilt redactor's fingerprint MUST match the one stamped on the handle at
    /// launch — only then have we provably reconstructed the same key that masked the original output. If the
    /// credential threw, re-resolved to nothing, or rotated (fingerprint mismatch), we complete from the exit
    /// marker only (NEVER re-tail with an un/mis-keyed redactor) so an echoed secret is never frozen into the log.
    /// </summary>
    private async Task<AgentRunResult?> ReattachAndFoldAsync(Guid runId, ISandboxDurableRunner durable, SandboxHandle handle, AgentTask task, Guid teamId, IAgentHarness harness, CancellationToken cancellationToken)
    {
        SecretRedactor redactor;
        try
        {
            redactor = (await ResolveModelCredentialEnvAsync(task, teamId, harness, cancellationToken).ConfigureAwait(false)).Redactor;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not re-resolve the credential to redact the re-attached tail; completing from the exit marker only to avoid leaking an echoed secret", runId);
            return await CompleteFromMarkerOnlyAsync(durable, handle, cancellationToken).ConfigureAwait(false);
        }

        // Re-tail ONLY when the rebuilt redactor provably matches the one that masked the original output — its
        // fingerprint must equal the one stamped at launch. A mismatch (credential deleted/rotated, team-default
        // changed; both-null = a run with no injected secret → safe) means we can no longer mask a key the spool
        // may echo, so complete from the marker only rather than freeze an unmaskable secret into the log.
        if (redactor.Fingerprint != handle.InjectedKeyFingerprint)
        {
            _logger.LogWarning("Agent run {RunId}: the re-resolved credential no longer matches the one injected at launch (deleted/rotated); completing from the exit marker only to avoid leaking an echoed secret", runId);
            return await CompleteFromMarkerOnlyAsync(durable, handle, cancellationToken).ConfigureAwait(false);
        }

        var events = new List<AgentEvent>();

        async Task PersistLineAsync(string line)
        {
            var normalized = harness.ParseEvent(line);
            if (normalized is null) return;

            var redacted = Redact(normalized, redactor);

            await _runs.AppendEventAsync(runId, redacted, cancellationToken).ConfigureAwait(false);
            events.Add(redacted);
        }

        var sandbox = await durable.AttachAsync(handle, (line, _) => PersistLineAsync(line), cancellationToken, CheckpointHandleOffset(runId, handle)).ConfigureAwait(false);

        return sandbox.Status == SandboxStatus.TimedOut
            ? new AgentRunResult { Status = AgentRunStatus.TimedOut, ExitReason = "timed-out", Error = "The agent run exceeded its time budget and was terminated." }
            : harness.BuildResult(events, sandbox.ExitCode);
    }

    /// <summary>Fallback when the credential can't be re-resolved to redact a re-attached tail: complete from the exit marker WITHOUT re-tailing (so no unredacted line reaches the log) — Succeeded/Failed by the code if it's present, Failed if the process is gone, or null (leave Running for a later sweep) if it's still alive and we can't safely observe it.</summary>
    private static async Task<AgentRunResult?> CompleteFromMarkerOnlyAsync(ISandboxDurableRunner durable, SandboxHandle handle, CancellationToken cancellationToken)
    {
        var probe = await durable.ProbeAsync(handle, cancellationToken).ConfigureAwait(false);

        return probe.State switch
        {
            SandboxRunState.Exited => new AgentRunResult { Status = (probe.ExitCode ?? -1) == 0 ? AgentRunStatus.Succeeded : AgentRunStatus.Failed, ExitReason = "reattach-marker-only", Error = (probe.ExitCode ?? -1) == 0 ? null : $"Re-attached run completed from its exit marker only (exit {probe.ExitCode}); its output was not re-folded because the credential was unavailable to redact it." },
            SandboxRunState.Gone => new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "reattach-marker-only", Error = "Re-attached run's process was gone with no exit marker and the credential was unavailable to redact its output." },
            _ => null,
        };
    }

    private static SandboxHandle? DeserializeHandle(string? handleJson)
    {
        if (string.IsNullOrWhiteSpace(handleJson)) return null;

        try { return JsonSerializer.Deserialize<SandboxHandle>(handleJson, AgentJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Land the terminal result (fenced on the claim epoch, so a reclaimed-then-revived worker loses), then fire the completion notifier (which resumes the agent.code node parked on this run). The notifier is best-effort + swallows its own failures, so completion is never masked by a resume error.</summary>
    private async Task CompleteAndNotifyAsync(Guid runId, AgentRunResult result, long expectedEpoch, CancellationToken cancellationToken)
    {
        try
        {
            await _runs.CompleteAsync(runId, result, expectedEpoch, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentRunTransitionException ex)
        {
            // The run is already terminal — the reconciler (or another worker) landed it first while this
            // executor was mid-flight. Don't re-complete or throw; still notify below so the parent
            // workflow resumes off whatever terminal state stuck.
            _logger.LogWarning(ex, "Agent run {RunId} was already terminal at completion (likely reconciled); skipping re-complete, still notifying", runId);
        }

        await _notifier.NotifyCompletedAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fold the workspace's git-diff ground truth into the result — the agent's actual changed files +
    /// unified patch, overriding the harness's event-parsed file list (git is authoritative, not the
    /// agent's self-report). No-op when the run had no workspace. Best-effort: a capture failure is logged
    /// and the result kept as-is, never flipping an otherwise-successful run to Failed over a git hiccup.
    /// </summary>
    private async Task<AgentRunResult> EnrichWithWorkspaceChangesAsync(Guid runId, AgentRunResult result, IWorkspaceHandle? workspace, CancellationToken cancellationToken)
    {
        if (workspace is null) return result;

        try
        {
            var changes = await workspace.CaptureChangesAsync(cancellationToken).ConfigureAwait(false);
            return result with { ChangedFiles = changes.ChangedFiles, Patch = TruncatePatch(changes.Patch, MaxPatchChars) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort + defence-in-depth: ANY capture failure (a wrapped WorkspaceException, or a raw
            // infra exception that slipped the provider) is logged and the result kept — a git hiccup must
            // never flip an otherwise-successful run to Failed. Cancellation still propagates (worker torn down).
            _logger.LogWarning(ex, "Agent run {RunId}: failed to capture workspace changes; keeping the harness-reported file list", runId);
            return result;
        }
    }

    /// <summary>
    /// Cap the inlined diff so a runaway / binary diff can't bloat the persisted run row (read on every
    /// resume). The full diff moving to the artifact/observability layer is a later slice. Internal + static
    /// so it's unit-pinned.
    /// </summary>
    internal static string TruncatePatch(string patch, int maxChars)
    {
        if (string.IsNullOrEmpty(patch) || patch.Length <= maxChars) return patch;

        return patch[..maxChars] + $"\n... diff truncated ({patch.Length} chars; capped at {maxChars}) ...\n";
    }

    /// <summary>
    /// When enabled, push a SUCCESSFUL run's non-empty diff to a deterministically-named remote branch and fold
    /// the pushed name into the result so the agent.code node's <c>branch</c> output carries it — the handoff a
    /// downstream git.open_pr needs (that node requires the branch to pre-exist on the remote). A SIDE-EFFECTING
    /// write to the user's remote, so it is gated hard: the flag must be on, the run must have Succeeded with a
    /// non-empty diff, and the handle must be push-capable; ANY guard failing returns the result UNCHANGED.
    ///
    /// <para>Idempotence / no-replay: re-read the run's epoch and skip if it no longer matches the one this
    /// executor claimed — the run was reclaimed, so this side effect would be wasted (the completion CAS loses
    /// anyway) and we must not fire it. The branch name is run-id-derived, so a workflow RETRY of agent.code is a
    /// new run id → a NEW branch (acceptable v1 branch-litter), and a re-push of the SAME run is a plain --force
    /// overwrite (no divergent branch).</para>
    ///
    /// <para>Best-effort like <see cref="EnrichWithWorkspaceChangesAsync"/>: a <see cref="WorkspaceException"/> is
    /// SWALLOWED (a push hiccup — e.g. a read-only credential 403 — never flips a Succeeded run to Failed) but is
    /// surfaced as a Warning event on the timeline (token already redacted in the message) so the operator sees
    /// WHY no branch appeared. Cancellation still propagates (worker torn down).</para>
    /// </summary>
    internal async Task<AgentRunResult> PushProducedBranchIfEnabledAsync(Guid runId, AgentRunResult result, IWorkspaceHandle? workspace, long claimedEpoch, CancellationToken cancellationToken)
    {
        if (!IsPushEnabled()) return result;
        if (result.Status != AgentRunStatus.Succeeded) return result;
        if (result.ChangedFiles.Count == 0 && string.IsNullOrEmpty(result.Patch)) return result;
        if (workspace is not IWorkspacePushHandle pushHandle) return result;

        // No-replay: a reclaimed run (epoch bumped) would lose the completion CAS anyway — don't fire the side
        // effect. Read FRESH + untracked (GetAsync is AsNoTracking) so we see the reclaimer's bumped epoch.
        var current = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);

        if (current.FenceEpoch != claimedEpoch)
        {
            _logger.LogWarning("Agent run {RunId}: skipping branch push — the run was reclaimed (epoch {Current} != claimed {Claimed}); its completion would lose the CAS", runId, current.FenceEpoch, claimedEpoch);
            return result;
        }

        try
        {
            var branch = await pushHandle.PushChangesAsync(BuildBranchName(runId), cancellationToken).ConfigureAwait(false);

            return branch is null ? result : result with { ProducedBranch = branch };
        }
        catch (WorkspaceException ex)
        {
            // Best-effort: a push failure must never flip a Succeeded run to Failed. The exception message has the
            // token already redacted (the handle redacts it), so it's safe to persist onto the timeline.
            _logger.LogWarning(ex, "Agent run {RunId}: failed to push the produced branch; the run stays Succeeded with no branch output", runId);
            await AppendPushFailureWarningAsync(runId, ex.Message, cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>Append a Warning event so the operator sees on the timeline WHY no branch appeared — not only in an ILogger line. Best-effort: a failure to record the warning never masks the run's success.</summary>
    private async Task AppendPushFailureWarningAsync(Guid runId, string redactedMessage, CancellationToken cancellationToken)
    {
        try
        {
            await _runs.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.Warning, Text = $"Could not push the agent's changes to a branch: {redactedMessage}" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not record the branch-push failure warning event", runId);
        }
    }

    /// <summary>Deterministic, run-unique remote branch name for a produced diff. Pure + private so it's unit-pinned. Run-id-derived, so a workflow retry (new run id) → a new branch; a re-push of the same run → the same branch (plain --force overwrite).</summary>
    internal static string BuildBranchName(Guid runId) => $"codespace/agent/{runId:N}";

    /// <summary>True ONLY for "1"/"true"/"TRUE" (trimmed); fail-closed default-OFF for null / "" / "0" / "false" / anything else. Mirrors the env-flag pattern (Rule 8). Internal so it's unit-pinned; production reads it through this single gate.</summary>
    internal static bool IsPushEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(PushEnabledEnvVar)?.Trim();

        return raw is "1" or "true" or "TRUE";
    }

    /// <summary>True ONLY for "1"/"true"/"TRUE" (trimmed); fail-closed default-OFF otherwise. Mirrors <see cref="IsPushEnabled"/> exactly (Rule 8). Internal so it's unit-pinned; production reads it through this single gate.</summary>
    internal static bool IsMcpEndpointEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(McpEndpointEnabledEnvVar)?.Trim();

        return raw is "1" or "true" or "TRUE";
    }

    /// <summary>
    /// Open the run's per-run UDS MCP endpoint when the flag is ON — null otherwise, so the flag-OFF path is
    /// byte-identical. Mints a DEDICATED DI scope (its own DbContext) because the framing loop runs CONCURRENTLY
    /// with the harness + the event-append path, so it must not share the heartbeat / streaming scope — the same
    /// thread-safety reason the heartbeat mints its own. The scope is held for the endpoint's life and disposed in
    /// the endpoint's <see cref="AgentMcpEndpoint.DisposeAsync"/> (never resolve the registry from a disposed scope).
    /// The connect registry is a DI singleton, so resolving it from this scope hands a consumer the same map. The
    /// socket path is computed from the SAME <see cref="LocalProcessRunner.McpSocketPathFor"/> the runner uses, so the
    /// listener and a later runner-side bind agree by construction. Fail-soft (A10): a host that can't bind a UDS —
    /// though the flag is on — disposes the scope, logs a Warning, and returns null; the endpoint is optional infra,
    /// not the run, so the run still proceeds without it.
    /// </summary>
    private Task<AgentMcpEndpoint?> OpenMcpEndpointIfEnabledAsync(Guid runId, AgentTask effectiveTask, Guid teamId, CancellationToken ct)
    {
        if (!IsMcpEndpointEnabled()) return Task.FromResult<AgentMcpEndpoint?>(null);

        var socketPath = LocalProcessRunner.McpSocketPathFor(runId.ToString("N"));
        var token = McpRunToken.Mint();

        var scope = _scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAgentToolRegistry>();
        var connects = scope.ServiceProvider.GetRequiredService<IAgentMcpConnectRegistry>();

        try
        {
            var endpoint = new AgentMcpEndpoint(runId, registry, effectiveTask.Autonomy, teamId, socketPath, token, connects, scope, ct, _logger);

            return Task.FromResult<AgentMcpEndpoint?>(endpoint);
        }
        // An over-length socket path throws ArgumentOutOfRangeException (UDS endpoint ctor); CreateDirectory can throw
        // IOException / UnauthorizedAccessException. The endpoint is optional infra, not the run, so any of these is a
        // null + Warning, never a failed run. NOT OperationCanceledException — cancellation must propagate.
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            scope.Dispose();
            _logger.LogWarning(ex, "Agent run {RunId}: could not bind the MCP endpoint socket; proceeding without the tool fabric", runId);

            return Task.FromResult<AgentMcpEndpoint?>(null);
        }
    }

    /// <summary>
    /// Resolve + decrypt the run's model credential (if any) just-in-time and project it onto the harness's env
    /// vars. Empty when the harness can't authenticate (implements no projector) or no credential applies — the
    /// run then relies on whatever env the runner already provides. A PINNED-but-unresolvable credential throws
    /// (the executor's catch lands a clean Failed), never silently using a different key.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string> Env, SecretRedactor Redactor)> ResolveModelCredentialEnvAsync(AgentTask task, Guid teamId, IAgentHarness harness, CancellationToken cancellationToken)
    {
        var projector = harness as IModelCredentialProjector;

        var credential = await _modelCredentials.ResolveAsync(task, teamId, projector, cancellationToken).ConfigureAwait(false);

        var env = projector is not null && credential is not null ? projector.ProjectToEnv(credential) : EmptySecretEnv;
        // Redact only the actual SECRET (the api key / gateway token) — never the non-secret base URL.
        var redactor = credential?.ApiKey is { Length: > 0 } key ? new SecretRedactor(new[] { key }) : SecretRedactor.None;

        return (env, redactor);
    }

    /// <summary>Layer the resolved credential's env onto the task's own non-secret env — the injected value wins for a shared key. In-memory only; the result is never re-persisted (an empty secret env returns the task env unchanged).</summary>
    internal static IReadOnlyDictionary<string, string> MergeEnvironment(IReadOnlyDictionary<string, string> taskEnv, IReadOnlyDictionary<string, string> secretEnv)
    {
        if (secretEnv.Count == 0) return taskEnv;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in taskEnv) merged[key] = value;
        foreach (var (key, value) in secretEnv) merged[key] = value;
        return merged;
    }

    /// <summary>Claim the run (Queued → Running). Returns false when it's already Running/terminal — the exactly-once guard that stops a re-claim from re-spawning the harness.</summary>
    /// <summary>Claim the run (Queued → Running) and return the fencing epoch to complete under, or null when it's already claimed/terminal (the exactly-once guard).</summary>
    private async Task<long?> TryClaimAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            return await _runs.MarkRunningAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentRunTransitionException)
        {
            _logger.LogInformation("Agent run {RunId} already claimed or terminal; skipping duplicate execution", runId);
            return null;
        }
    }

    private async Task<AgentRunResult> RunHarnessAsync(Guid runId, IAgentHarness harness, ISandboxRunner runner, SandboxSpec spec, SecretRedactor redactor, CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();

        async Task PersistLineAsync(string line)
        {
            var normalized = harness.ParseEvent(line);
            if (normalized is null) return;

            // Strip any secret the CLI echoed BEFORE the append-only log freezes it (the log can't be edited later).
            var redacted = Redact(normalized, redactor);

            await _runs.AppendEventAsync(runId, redacted, cancellationToken).ConfigureAwait(false);
            events.Add(redacted);
        }

        // The heartbeat is owned by ExecuteAsync (it spans the whole run, including the completion tail), so
        // streaming here just emits events — a quiet step's liveness is kept fresh by that outer heartbeat. The
        // redactor's fingerprint is stamped onto the durable handle so a re-attach can prove it rebuilt the SAME
        // key before re-tailing the spool (a rotated/deleted key → marker-only, never an unmaskable leak).
        var sandbox = await RunSandboxAsync(runId, runner, spec, PersistLineAsync, redactor.Fingerprint, cancellationToken).ConfigureAwait(false);

        // Events are already redacted, so a result the harness folds from them (summary / error) is redacted too.
        return sandbox.Status == SandboxStatus.TimedOut
            ? new AgentRunResult { Status = AgentRunStatus.TimedOut, ExitReason = "timed-out", Error = "The agent run exceeded its time budget and was terminated." }
            : harness.BuildResult(events, sandbox.ExitCode);
    }

    /// <summary>Redact any echoed secret out of a normalized event — its text AND its structured payload — before it reaches the append-only log. No-op when the run has no secret.</summary>
    private static AgentEvent Redact(AgentEvent normalized, SecretRedactor redactor)
    {
        if (redactor.IsEmpty) return normalized;

        return normalized with { Text = redactor.Redact(normalized.Text), Data = RedactData(normalized.Data, redactor) };
    }

    /// <summary>Mask a structured payload via its raw JSON text, then re-parse. If masking somehow broke the JSON, drop the payload rather than persist an unredacted blob.</summary>
    private static JsonElement? RedactData(JsonElement? data, SecretRedactor redactor)
    {
        if (data is null) return null;

        var raw = data.Value.GetRawText();
        var redacted = redactor.Redact(raw);

        if (redacted == raw) return data;

        try { using var doc = JsonDocument.Parse(redacted); return doc.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Pick the execution mode for the resolved runner: the DURABLE path (launch to a spool + persist a
    /// handle + tail) whenever the runner supports it — so a backend restart can recover/re-attach the run;
    /// otherwise the live-stream / batch path. Feature-detected via <c>runner is ISandboxDurableRunner</c>, so
    /// a runner that can't be durable transparently falls back to streaming.
    /// </summary>
    private async Task<SandboxResult> RunSandboxAsync(Guid runId, ISandboxRunner runner, SandboxSpec spec, Func<string, Task> persistLine, string? keyFingerprint, CancellationToken cancellationToken)
    {
        if (runner is ISandboxDurableRunner durable)
            return await RunDurableAsync(runId, durable, spec, persistLine, keyFingerprint, cancellationToken).ConfigureAwait(false);

        return await RunAndStreamAsync(runner, spec, persistLine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Launch the run to its durable spool, persist the returned handle (keyed by the run id) BEFORE
    /// observing, then attach + tail. Persisting first is what lets the reconciler recover this run if this
    /// observer dies mid-tail. On a host-shutdown cancel the attach stops observing WITHOUT killing the
    /// process (leaving the run Running for re-attach/recovery); only the spec timeout terminates it.
    /// </summary>
    private async Task<SandboxResult> RunDurableAsync(Guid runId, ISandboxDurableRunner durable, SandboxSpec spec, Func<string, Task> persistLine, string? keyFingerprint, CancellationToken cancellationToken)
    {
        // Stamp the injected-key fingerprint onto the handle at launch so a re-attach can verify it rebuilt the
        // same redactor before re-tailing (a rotated/deleted credential → fingerprint mismatch → marker-only).
        var handle = (await durable.LaunchAsync(spec, runId.ToString("N"), cancellationToken).ConfigureAwait(false)) with { InjectedKeyFingerprint = keyFingerprint };

        await _runs.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), cancellationToken).ConfigureAwait(false);

        // Checkpoint the advancing spool offset onto the handle as we tail, so a backend restart mid-run can
        // re-attach (ReattachAsync) and resume from here instead of re-emitting the whole spool.
        return await durable.AttachAsync(handle, (line, _) => persistLine(line), cancellationToken, CheckpointHandleOffset(runId, handle)).ConfigureAwait(false);
    }

    /// <summary>The onCheckpoint callback for <see cref="ISandboxDurableRunner.AttachAsync"/>: persist the advanced spool offset onto the run's handle (re-serialising the same handle with the new offset) so a re-attach resumes there. A pure UPDATE via <see cref="IAgentRunService.SetRunnerHandleAsync"/>, never blocking completion.</summary>
    private Func<long, CancellationToken, Task> CheckpointHandleOffset(Guid runId, SandboxHandle handle) =>
        (offset, ct) => _runs.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle with { StdoutOffset = offset }, AgentJson.Options), ct);

    /// <summary>Stream the harness live when the runner supports it (events land as emitted); otherwise run batch and replay captured stdout through the same per-line path.</summary>
    private static async Task<SandboxResult> RunAndStreamAsync(ISandboxRunner runner, SandboxSpec spec, Func<string, Task> persistLine, CancellationToken cancellationToken)
    {
        if (runner is ISandboxStreamRunner streamer)
            return await streamer.RunStreamingAsync(spec, (line, _) => persistLine(line), cancellationToken).ConfigureAwait(false);

        var result = await runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);

        foreach (var line in result.Stdout.Split('\n')) await persistLine(line).ConfigureAwait(false);

        return result;
    }
}

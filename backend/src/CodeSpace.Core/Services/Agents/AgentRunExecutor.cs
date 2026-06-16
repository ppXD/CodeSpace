using System.Text.Json;
using System.Net.Sockets;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Operator-facing reason stamped on a branch agent run the executor cancels at the claim point because its parent workflow run flipped terminal between the reconciler's dispatch and this claim — the no-sandbox-under-terminal-parent guard. Mirrors the reconciler's <c>OrphanedParentTerminalError</c> intent (which catches the still-Queued window; this closes the post-claim TOCTOU one).</summary>
    public const string ParentTerminalAtClaimError =
        "Agent run cancelled by the executor — its parent workflow run reached a terminal state (cancelled, " +
        "failed, or succeeded) before this run's sandbox was launched, so no work was started for an already-finished workflow.";

    private readonly IAgentRunService _runs;
    private readonly IAgentHarnessRegistry _harnesses;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IAgentWorkspaceResolver _workspaceResolver;
    private readonly IModelCredentialResolver _modelCredentials;
    private readonly IWorkspaceProviderRegistry _workspaces;
    private readonly IAgentRunCompletionNotifier _notifier;
    // Mints a fresh DI scope (→ its own DbContext) for the heartbeat loop, which runs concurrently with the event stream.
    private readonly IServiceScopeFactory _scopeFactory;
    // Reads the parent WorkflowRun's status at the claim point — the authoritative no-sandbox-under-terminal-parent guard.
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<AgentRunExecutor> _logger;

    public AgentRunExecutor(IAgentRunService runs, IAgentHarnessRegistry harnesses, ISandboxRunnerRegistry runners, IAgentWorkspaceResolver workspaceResolver, IModelCredentialResolver modelCredentials, IWorkspaceProviderRegistry workspaces, IAgentRunCompletionNotifier notifier, IServiceScopeFactory scopeFactory, CodeSpaceDbContext db, ILogger<AgentRunExecutor> logger)
    {
        _runs = runs;
        _harnesses = harnesses;
        _runners = runners;
        _workspaceResolver = workspaceResolver;
        _modelCredentials = modelCredentials;
        _workspaces = workspaces;
        _notifier = notifier;
        _scopeFactory = scopeFactory;
        _db = db;
        _logger = logger;
    }

    public async Task ExecuteAsync(Guid agentRunId, CancellationToken cancellationToken)
    {
        var run = await _runs.GetAsync(agentRunId, cancellationToken).ConfigureAwait(false);

        if (await TryClaimAsync(agentRunId, cancellationToken).ConfigureAwait(false) is not { } claimedEpoch) return;

        // Re-check the parent workflow run's status the instant after the Queued→Running claim wins, closing the TOCTOU
        // the reconciler's guard leaves open: the reconciler reads the parent then re-dispatches, but the parent can flip
        // terminal in the window before this claim, so without this re-check the executor would launch a sandbox under an
        // already-dead workflow. A standalone run (no WorkflowRunId) or a live parent (Suspended/Pending/Running) proceeds
        // EXACTLY as before — only a terminal parent aborts the launch (the run, now Running, is cancelled instead).
        if (await AbortIfParentTerminalAsync(agentRunId, run.WorkflowRunId, claimedEpoch, cancellationToken).ConfigureAwait(false)) return;

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

            // Mint the per-run socket + token ONCE so the endpoint listener and the harness's declaration agree by
            // construction (and so the token can be stamped on the durable handle for a re-attach to re-bind the same
            // one). Both null on the flag-OFF path → no endpoint, no wiring → byte-identical to today.
            var (socketPath, token) = MintMcpConnect(agentRunId);

            // Open the per-run MCP endpoint (flag-OFF → null → no-op). It lives ONLY for the harness span: the harness
            // runs synchronously here (RunHarnessAsync → AttachAsync blocks until exit), and `await using` inside the
            // try tears it down on EVERY exit (success / cancel / generic catch) — NOT gated on leaveWorkspaceForReattach.
            await using var mcp = OpenMcpEndpointIfEnabled(effectiveTask, agentRunId, effectiveTask.Autonomy, run.TeamId, redactor, socketPath, token, claimedEpoch, effectiveTask.ApprovalConversationId, cancellationToken);

            // Wire the live CLI to the fabric ONLY when the endpoint actually opened AND the harness declares an
            // MCP-server shape — a non-null endpoint already encodes "the flag is on AND the bind succeeded", so no
            // second flag. Otherwise the wiring is null and the run is unchanged. The token rides the handle so a
            // re-attach re-binds the same one the agent's declaration file already holds.
            var mcpWiring = BuildMcpWiring(agentRunId, mcp, harness, socketPath, token);

            // When the declaration WAS written (the CLI will load the codespace server), merge the run's tier-permitted
            // mcp__codespace__* tool names into the harness allow-list — so a run that set a RESTRICTED task.Tools still
            // receives the governed tools the endpoint serves (today the harness projects ONLY task.Tools, so a restricted
            // run couldn't call them). Additive + tier-filtered; a no-op when the author named no tools (the CLI default
            // already reaches a declared MCP server's tools). Drives BuildInvocation off the augmented task.
            var spec = harness.BuildInvocation(AugmentToolsForMcp(effectiveTask, mcp, mcpWiring)) with { Mcp = mcpWiring };

            // The MCP token rides the durable handle whenever the ENDPOINT opened (not only when a declaration was
            // written) so a re-attach re-binds the SAME socket+token — the detached agent's declaration file still
            // points at it. Null when no endpoint → nothing to re-open.
            var mcpToken = mcp is null ? null : token;

            var result = await RunHarnessAsync(agentRunId, harness, runner, spec, mcpToken, redactor, cancellationToken).ConfigureAwait(false);

            result = await EnrichWithWorkspaceChangesAsync(agentRunId, result, workspace, cancellationToken).ConfigureAwait(false);

            result = await PushProducedBranchIfEnabledAsync(agentRunId, effectiveTask, result, workspace, claimedEpoch, cancellationToken).ConfigureAwait(false);

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

        // Resolve a redactor for the re-opened endpoint's tool-result text — fresh from the run's credential, in its
        // own try so a deleted/rotated credential degrades to the no-op redactor rather than blocking the reattach.
        // Independent of ReattachAndFoldAsync's own resolution (which still owns the fingerprint-gated re-tail).
        SecretRedactor reopenRedactor;
        try { reopenRedactor = (await ResolveModelCredentialEnvAsync(task, run.TeamId, harness, cancellationToken).ConfigureAwait(false)).Redactor; }
        catch { reopenRedactor = SecretRedactor.None; }

        // Re-open the run's MCP endpoint on the SAME socket+token the handle recorded at launch (the in-process listener
        // died with the original worker, but the detached agent keeps running with its declaration file pointing here).
        // Null when the run had no fabric / the flag is off → no-op. Bounded to the re-tail span like ExecuteAsync's.
        await using var mcp = ReopenMcpEndpointForReattach(task, agentRunId, task.Autonomy, run.TeamId, reopenRedactor, handle, expectedEpoch, task.ApprovalConversationId, cancellationToken);

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
        var writer = new BufferedEventWriter(_runs, runId);   // same batched-append + flush-at-checkpoint path as the live tail

        async Task PersistLineAsync(string line)
        {
            var normalized = harness.ParseEvent(line);
            if (normalized is null) return;

            var redacted = Redact(normalized, redactor);

            await writer.BufferAsync(redacted, cancellationToken).ConfigureAwait(false);
            events.Add(redacted);
        }

        var sandbox = await durable.AttachAsync(handle, (line, _) => PersistLineAsync(line), cancellationToken, CheckpointHandleOffset(runId, handle, writer)).ConfigureAwait(false);

        // Final flush for the terminal-drain lines (no trailing checkpoint), as in the live path.
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

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
    internal async Task<AgentRunResult> PushProducedBranchIfEnabledAsync(Guid runId, AgentTask task, AgentRunResult result, IWorkspaceHandle? workspace, long claimedEpoch, CancellationToken cancellationToken)
    {
        if (!ShouldPushProducedBranch(task)) return result;
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

    /// <summary>
    /// The single per-run gate deciding whether THIS run pushes a produced branch: the deployment-wide env flag
    /// (<see cref="IsPushEnabled"/>) OR the task's explicit per-run opt-in (<see cref="AgentTask.PushProducedBranch"/>).
    /// Fail-open toward the operator (a per-run opt-in turns push ON for one run without flipping the ambient flag, but
    /// cannot turn it OFF when the operator enabled it deployment-wide) — the SAME shape as
    /// <see cref="ShouldOpenMcpEndpoint"/>. This is the gate the one-agent-one-branch fan-out trips per branch agent.
    /// Pure + internal so it's unit-pinned and production reads it through this single gate.
    /// </summary>
    internal static bool ShouldPushProducedBranch(AgentTask task) => IsPushEnabled() || task.PushProducedBranch == true;

    /// <summary>True ONLY for "1"/"true"/"TRUE" (trimmed); fail-closed default-OFF otherwise. Mirrors <see cref="IsPushEnabled"/> exactly (Rule 8). Internal so it's unit-pinned; production reads it through this single gate.</summary>
    internal static bool IsMcpEndpointEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(McpEndpointEnabledEnvVar)?.Trim();

        return raw is "1" or "true" or "TRUE";
    }

    /// <summary>
    /// The single per-run gate deciding whether the MCP endpoint opens for THIS run: the deployment-wide env flag
    /// (<see cref="IsMcpEndpointEnabled"/>) OR the task's explicit per-run opt-in (<see cref="AgentTask.EnableMcpEndpoint"/>).
    /// Fail-open toward the operator: a per-run opt-in can turn the fabric ON for one run without flipping the ambient
    /// flag, but cannot turn it OFF when the operator enabled it deployment-wide. Pure + internal so it's unit-pinned and
    /// the benchmark runner can read the SAME gate to record what the executor actually did (no mislabeled rows).
    /// </summary>
    internal static bool ShouldOpenMcpEndpoint(AgentTask task) => IsMcpEndpointEnabled() || task.EnableMcpEndpoint == true;

    /// <summary>
    /// A BOOT diagnostic the worker host calls once at startup so a mis-configured tool fabric is VISIBLE at deploy time,
    /// not silently discovered as a tool-less run hours later: when the endpoint is enabled deployment-wide
    /// (<see cref="IsMcpEndpointEnabled"/>) but the <c>codespace-mcp</c> proxy binary can't be resolved at
    /// <see cref="LocalProcessRunner.McpProxyBinaryPath"/>, log a clear Warning naming the resolved path + the override
    /// env var; otherwise (endpoint enabled AND the binary present) log a confirming Information line. A no-op when the
    /// endpoint is OFF (nothing to warn about). Pure logging — never throws, never fails boot (the fabric is optional
    /// infra). The per-run <see cref="BuildMcpWiring"/> ALSO fail-closes + logs per run; this is the proactive deploy-time
    /// half of the same fail-closed signal. Internal + static so it's unit-pinnable without a host.
    /// </summary>
    public static void LogMcpProxyReadiness(ILogger logger)
    {
        if (!IsMcpEndpointEnabled()) return;

        var proxyPath = LocalProcessRunner.McpProxyBinaryPath();

        if (File.Exists(proxyPath))
        {
            logger.LogInformation("MCP tool fabric enabled ({EnvVar}=on); codespace-mcp proxy resolved at '{ProxyPath}'", McpEndpointEnabledEnvVar, proxyPath);
            return;
        }

        logger.LogWarning("MCP tool fabric is enabled ({EnvVar}=on) but the codespace-mcp proxy binary was NOT found at '{ProxyPath}'. Agent runs will fail closed to a TOOL-LESS run (no MCP wiring written). Publish the proxy alongside the worker or set {OverrideEnvVar} to its absolute path.", McpEndpointEnabledEnvVar, proxyPath, LocalProcessRunner.McpProxyPathEnvVar);
    }

    /// <summary>
    /// The run's per-run UDS socket path + a freshly-minted capability token, computed once so the endpoint listener,
    /// the harness's declaration file, and the durable handle (for a re-attach) all agree on the same pair. The socket
    /// path uses the SAME <see cref="LocalProcessRunner.McpSocketPathFor"/> the runner binds, so they match by
    /// construction. On a re-attach the token is NOT re-minted — see <see cref="ReopenMcpEndpointForReattach"/>.
    /// </summary>
    private static (string SocketPath, string Token) MintMcpConnect(Guid runId) =>
        (LocalProcessRunner.McpSocketPathFor(runId.ToString("N")), McpRunToken.Mint());

    /// <summary>
    /// Open the run's per-run UDS MCP endpoint on the given socket + token when the run's MCP gate is ON
    /// (<see cref="ShouldOpenMcpEndpoint"/> — the ambient env flag OR the task's per-run opt-in) — null otherwise, so the
    /// gate-OFF path is byte-identical. Mints a DEDICATED DI scope (its own DbContext) because the framing loop
    /// runs CONCURRENTLY with the harness + the event-append path, so it must not share the heartbeat / streaming scope.
    /// The scope is held for the endpoint's life and disposed in the endpoint's <see cref="AgentMcpEndpoint.DisposeAsync"/>.
    /// The connect registry is a DI singleton, so resolving it from this scope hands a consumer the same map. Fail-soft
    /// (A10): a host that can't bind a UDS — though the gate is on — disposes the scope, logs a Warning, and returns
    /// null; the endpoint is optional infra, not the run, so the run still proceeds without it.
    /// </summary>
    private AgentMcpEndpoint? OpenMcpEndpointIfEnabled(AgentTask task, Guid runId, AgentAutonomyLevel autonomy, Guid teamId, SecretRedactor redactor, string socketPath, string token, long fenceEpoch, Guid? approvalConversationId, CancellationToken ct)
    {
        if (!ShouldOpenMcpEndpoint(task)) return null;

        var scope = _scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAgentToolRegistry>();
        var connects = scope.ServiceProvider.GetRequiredService<IAgentMcpConnectRegistry>();

        // The governance flag is read ONCE here (Rule 8 single gate); the endpoint threads it + the run's fence epoch
        // into each connection's handler so a side-effecting tool call is ledger-tracked. Flag-OFF → no ledger rows.
        var governanceEnabled = McpRequestHandler.IsGovernanceEnabled();

        try
        {
            return new AgentMcpEndpoint(runId, registry, autonomy, teamId, redactor, socketPath, token, connects, scope, ct, _logger, fenceEpoch, governanceEnabled, approvalConversationId);
        }
        // An over-length socket path throws ArgumentOutOfRangeException (UDS endpoint ctor); CreateDirectory can throw
        // IOException / UnauthorizedAccessException. The endpoint is optional infra, not the run, so any of these is a
        // null + Warning, never a failed run. NOT OperationCanceledException — cancellation must propagate.
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            scope.Dispose();
            _logger.LogWarning(ex, "Agent run {RunId}: could not bind the MCP endpoint socket; proceeding without the tool fabric", runId);

            return null;
        }
    }

    /// <summary>
    /// Re-open the run's MCP endpoint after a re-attach using the SAME socket + token the launch recorded on the handle.
    /// The in-process listener died with the original worker, but the setsid-detached agent keeps running with its 0600
    /// declaration file pointing at THIS socket+token — a fresh token would lock it out. The socket path is reconstructed
    /// from the run id (the single-source-of-truth <see cref="LocalProcessRunner.McpSocketPathFor"/>, the SAME the
    /// launch bound). Null — no re-open — when the run had no fabric (handle carries no token) or the run's MCP gate is
    /// off (<see cref="ShouldOpenMcpEndpoint"/> — ambient flag OR per-run opt-in, the SAME gate the launch used, so a
    /// run opened via the per-run opt-in re-opens here too); the wiring flag is NOT re-checked here (the agent's
    /// declaration already exists, so the endpoint must serve it regardless). Fail-soft via <see cref="OpenMcpEndpointIfEnabled"/>.
    /// </summary>
    private AgentMcpEndpoint? ReopenMcpEndpointForReattach(AgentTask task, Guid runId, AgentAutonomyLevel autonomy, Guid teamId, SecretRedactor redactor, SandboxHandle handle, long fenceEpoch, Guid? approvalConversationId, CancellationToken ct)
    {
        if (handle.McpRunToken is not { Length: > 0 } token) return null;

        var socketPath = LocalProcessRunner.McpSocketPathFor(runId.ToString("N"));

        // The reopened endpoint redacts tool-result text with a redactor the caller resolved fresh from the run's
        // credential — kept INDEPENDENT of the fold's own resolution (a second decrypt is harmless + idempotent) so the
        // delicate fingerprint-gated marker-only re-tail in ReattachAndFoldAsync is left untouched. The caller degrades
        // it to SecretRedactor.None on a resolution failure, so a deleted/rotated credential never blocks the reattach.
        return OpenMcpEndpointIfEnabled(task, runId, autonomy, teamId, redactor, socketPath, token, fenceEpoch, approvalConversationId, ct);
    }

    /// <summary>
    /// Build the MCP wiring the runner uses to point the live CLI at the fabric — or null (no wiring) unless BOTH hold:
    /// the endpoint ACTUALLY opened (a non-null endpoint already encodes "the flag is on AND the bind succeeded" — no
    /// second flag), and the chosen harness declares an MCP-server shape (<see cref="IMcpHarnessDeclaration"/>).
    ///
    /// <para>Fail-CLOSED (A10): the proxy binary the declaration points at must EXIST host-side; if it doesn't (a
    /// mis-configured deployment, a missing publish artifact), write NO declaration + log a Warning — handing the agent
    /// a config pointing at a missing binary would surface as a confusingly-broken MCP init, so a tool-less run is the
    /// honest degradation. The harness owns its format: it renders the file Content from the run-scoped context (socket +
    /// token + the absolute proxy command), so the declaration the agent reads matches the listener by construction.</para>
    /// </summary>
    private McpServerWiring? BuildMcpWiring(Guid runId, AgentMcpEndpoint? endpoint, IAgentHarness harness, string socketPath, string token)
    {
        if (endpoint is null || harness is not IMcpHarnessDeclaration declarer) return null;

        var proxyPath = LocalProcessRunner.McpProxyBinaryPath();

        if (!File.Exists(proxyPath))
        {
            _logger.LogWarning("Agent run {RunId}: the codespace-mcp proxy binary was not found at '{ProxyPath}'; proceeding WITHOUT the tool fabric (set {EnvVar} to its absolute path)", runId, proxyPath, LocalProcessRunner.McpProxyPathEnvVar);
            return null;
        }

        var context = new McpDeclarationContext { ProxyCommand = proxyPath, SocketPath = socketPath, Token = token, ServerName = McpRequestHandler.ServerName };

        var declaration = declarer.BuildMcpDeclaration(context);

        return new McpServerWiring { RelativeFileName = declaration.RelativeFileName, Content = declaration.Content, SocketPath = socketPath };
    }

    /// <summary>
    /// Merge the run's tier-permitted <c>mcp__codespace__*</c> tool names into the task's harness allow-list — but ONLY
    /// when the endpoint opened AND a declaration was actually written (a non-null <paramref name="wiring"/>: the CLI will
    /// load the codespace server, so the names resolve). The endpoint computes the tier-filtered set from the SAME
    /// registry + autonomy + server name it serves with, so the allow-list and the endpoint gate agree by construction.
    /// Additive (the author's tools win order); tier-filtered (a Denied tool is never offered); a no-op when the author
    /// named no tools (<see cref="McpAllowedTools.Augment"/> leaves a null/empty list untouched so the CLI default still
    /// reaches the MCP tools). Returns the task UNCHANGED whenever the fabric isn't actually serving — byte-identical.
    /// </summary>
    private static AgentTask AugmentToolsForMcp(AgentTask task, AgentMcpEndpoint? endpoint, McpServerWiring? wiring)
    {
        if (endpoint is null || wiring is null) return task;

        return task with { Tools = McpAllowedTools.Augment(task.Tools, endpoint.AllowedToolNames()) };
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

    /// <summary>
    /// The authoritative no-sandbox-under-terminal-parent guard, run the instant after the Queued→Running claim wins.
    /// A standalone run (no <paramref name="workflowRunId"/>) is unaffected — returns false (proceed) without touching
    /// the DB. For a workflow-staged branch run, read the parent WorkflowRun's status: a LIVE parent
    /// (Suspended/Pending/Running, or absent) returns false (proceed exactly as before); a TERMINAL parent
    /// (Cancelled/Failure/Success) cancels this now-Running run — via the same epoch-fenced completion path the executor
    /// uses for any outcome, which also notifies the parent — and returns true (abort the launch). This closes the TOCTOU
    /// the reconciler's still-Queued guard can't: the parent may flip terminal between that guard's read and this claim.
    /// </summary>
    private async Task<bool> AbortIfParentTerminalAsync(Guid runId, Guid? workflowRunId, long claimedEpoch, CancellationToken cancellationToken)
    {
        if (workflowRunId is not { } parentId) return false;   // standalone run — no parent to gate on, proceed unchanged

        var parentStatus = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == parentId)
            .Select(r => (WorkflowRunStatus?)r.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (parentStatus is not (WorkflowRunStatus.Cancelled or WorkflowRunStatus.Failure or WorkflowRunStatus.Success)) return false;   // live parent (or absent) — proceed unchanged

        _logger.LogInformation("Agent run {RunId}: parent workflow run {ParentId} is terminal ({Status}) at the claim point; cancelling instead of launching a sandbox", runId, parentId, parentStatus);

        await CompleteAndNotifyAsync(runId, new AgentRunResult { Status = AgentRunStatus.Cancelled, ExitReason = "parent-terminal", Error = ParentTerminalAtClaimError }, claimedEpoch, cancellationToken).ConfigureAwait(false);

        return true;
    }

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

    private async Task<AgentRunResult> RunHarnessAsync(Guid runId, IAgentHarness harness, ISandboxRunner runner, SandboxSpec spec, string? mcpToken, SecretRedactor redactor, CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();
        var writer = new BufferedEventWriter(_runs, runId);   // batches the DB inserts; flushed at each spool checkpoint + once at the end

        async Task PersistLineAsync(string line)
        {
            var normalized = harness.ParseEvent(line);
            if (normalized is null) return;

            // Strip any secret the CLI echoed BEFORE the append-only log freezes it (the log can't be edited later).
            var redacted = Redact(normalized, redactor);

            await writer.BufferAsync(redacted, cancellationToken).ConfigureAwait(false);   // buffered — one batched INSERT per spool checkpoint, not one per line
            events.Add(redacted);   // in-memory, for the harness's result fold
        }

        // The heartbeat is owned by ExecuteAsync (it spans the whole run, including the completion tail), so
        // streaming here just emits events — a quiet step's liveness is kept fresh by that outer heartbeat. The
        // redactor's fingerprint is stamped onto the durable handle so a re-attach can prove it rebuilt the SAME
        // key before re-tailing the spool (a rotated/deleted key → marker-only, never an unmaskable leak). The MCP
        // token rides the handle too so a re-attach re-binds the SAME socket+token the agent's declaration carries.
        var sandbox = await RunSandboxAsync(runId, runner, spec, PersistLineAsync, writer, redactor.Fingerprint, mcpToken, cancellationToken).ConfigureAwait(false);

        // Final flush: the durable runner's terminal-drain paths (CompleteFromSpool/Timeout/Vanished) deliver the last
        // lines WITHOUT a trailing checkpoint, so anything buffered after the last checkpoint must be flushed here
        // before the result is folded + the run completes. (A no-op when the buffer is already empty.)
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

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
    private async Task<SandboxResult> RunSandboxAsync(Guid runId, ISandboxRunner runner, SandboxSpec spec, Func<string, Task> persistLine, BufferedEventWriter writer, string? keyFingerprint, string? mcpToken, CancellationToken cancellationToken)
    {
        if (runner is ISandboxDurableRunner durable)
            return await RunDurableAsync(runId, durable, spec, persistLine, writer, keyFingerprint, mcpToken, cancellationToken).ConfigureAwait(false);

        // Non-durable fallback (no spool/checkpoint): the writer's size cap + the caller's final flush drain it.
        return await RunAndStreamAsync(runner, spec, persistLine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Launch the run to its durable spool, persist the returned handle (keyed by the run id) BEFORE
    /// observing, then attach + tail. Persisting first is what lets the reconciler recover this run if this
    /// observer dies mid-tail. On a host-shutdown cancel the attach stops observing WITHOUT killing the
    /// process (leaving the run Running for re-attach/recovery); only the spec timeout terminates it.
    /// </summary>
    private async Task<SandboxResult> RunDurableAsync(Guid runId, ISandboxDurableRunner durable, SandboxSpec spec, Func<string, Task> persistLine, BufferedEventWriter writer, string? keyFingerprint, string? mcpToken, CancellationToken cancellationToken)
    {
        // Stamp the injected-key fingerprint + the MCP run token onto the handle at launch. The fingerprint lets a
        // re-attach verify it rebuilt the same redactor before re-tailing (rotated/deleted credential → marker-only);
        // the token lets it RE-OPEN the endpoint with the SAME socket+token the agent's declaration file already holds.
        var handle = (await durable.LaunchAsync(spec, runId.ToString("N"), cancellationToken).ConfigureAwait(false)) with { InjectedKeyFingerprint = keyFingerprint, McpRunToken = mcpToken };

        await _runs.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), cancellationToken).ConfigureAwait(false);

        // Checkpoint the advancing spool offset onto the handle as we tail, so a backend restart mid-run can
        // re-attach (ReattachAsync) and resume from here instead of re-emitting the whole spool.
        return await durable.AttachAsync(handle, (line, _) => persistLine(line), cancellationToken, CheckpointHandleOffset(runId, handle, writer)).ConfigureAwait(false);
    }

    /// <summary>
    /// The onCheckpoint callback for <see cref="ISandboxDurableRunner.AttachAsync"/>: FLUSH the buffered events for the
    /// poll's lines, THEN persist the advanced spool offset onto the handle. The flush-before-offset ordering is the
    /// durability invariant — the persisted offset must never run ahead of flushed events, so a re-attach at worst
    /// re-emits the last batch (never loses a line). A pure jsonb UPDATE for the offset; never blocks completion.
    /// </summary>
    private Func<long, CancellationToken, Task> CheckpointHandleOffset(Guid runId, SandboxHandle handle, BufferedEventWriter writer) =>
        async (offset, ct) =>
        {
            await writer.FlushAsync(ct).ConfigureAwait(false);
            await _runs.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle with { StdoutOffset = offset }, AgentJson.Options), ct).ConfigureAwait(false);
        };

    /// <summary>
    /// Buffers redacted agent events and flushes them as ONE batched insert (instead of one INSERT per stdout line —
    /// the hot-path write-cost fix that also scales to faithful multi-block reasoning capture). Flushed by the spool
    /// <see cref="CheckpointHandleOffset"/> callback BEFORE the offset advances (so the durable offset never runs ahead
    /// of flushed events) and once more after the sandbox returns (the terminal drain has no trailing checkpoint). The
    /// size cap bounds memory and gives the non-durable / checkpoint-less path a periodic flush. Single-threaded by
    /// construction: the durable tail loop awaits each <c>onLine</c> then <c>onCheckpoint</c> sequentially, and the
    /// final flush runs after the attach returns — so no buffer lock is needed.
    /// </summary>
    private sealed class BufferedEventWriter
    {
        private const int MaxBuffered = 256;   // memory cap; the per-poll checkpoint is the normal flush trigger

        private readonly IAgentRunService _runs;
        private readonly Guid _runId;
        private readonly List<AgentEvent> _pending = new();

        public BufferedEventWriter(IAgentRunService runs, Guid runId)
        {
            _runs = runs;
            _runId = runId;
        }

        public async Task BufferAsync(AgentEvent @event, CancellationToken cancellationToken)
        {
            _pending.Add(@event);

            if (_pending.Count >= MaxBuffered) await FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_pending.Count == 0) return;

            var batch = _pending.ToList();
            _pending.Clear();

            await _runs.AppendEventsAsync(_runId, batch, cancellationToken).ConfigureAwait(false);
        }
    }

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

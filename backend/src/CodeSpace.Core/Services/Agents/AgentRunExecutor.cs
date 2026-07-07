using System.Text;
using System.Text.Json;
using System.Net.Sockets;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
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
    /// Air-gapped/large-context operator override (Rule 8) for the max session-transcript file size the P3 capture will
    /// read into memory. The session <c>.jsonl</c> is read whole into a string and then offloaded, so the transient
    /// per-capture peak is ~3× the file size (a ~2× UTF-8→UTF-16 string co-resident with the ~1× <c>byte[]</c> the
    /// artifact offloader encodes) — and it is NOT bounded across concurrency, so the worst-case envelope is roughly
    /// <c>runningParallelism × 3× cap</c> when many runs complete at once (raise the cap only on a worker sized for it;
    /// LOWER it on a constrained one). Beyond the cap the capture SKIPS (a continue cold-starts) rather than risk an OOM.
    /// Full-fidelity capture of an over-cap session would need a streaming artifact put — a separate slice; skipping is
    /// the safe floor (a cold-start is strictly better than an OOM).
    /// </summary>
    public const string MaxSessionTranscriptBytesEnvVar = "CODESPACE_AGENT_MAX_SESSION_TRANSCRIPT_BYTES";

    /// <summary>Default session-transcript capture cap — 32 MiB comfortably covers realistic multi-hour conversations; a larger file is treated as pathological and skipped. Env-overridable via <see cref="MaxSessionTranscriptBytesEnvVar"/>.</summary>
    internal const long DefaultMaxSessionTranscriptBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Operators opt INTO pushing a successful run's diff to a remote branch (a side-effecting write to the
    /// user's remote) by setting this to "1"/"true". Fail-closed default-OFF (absent/""/"0"/"false"/anything
    /// else → no push), so every existing run is byte-identical until an operator flips it. Pinned by a test
    /// (Rule 8) — renaming it silently turns the feature off for an operator who enabled it.
    /// </summary>
    public const string PushEnabledEnvVar = "CODESPACE_AGENT_PUSH_BRANCH_ENABLED";

    /// <summary>
    /// Operators opt INTO on-disk integration of K parallel agent contributions into ONE branch (a side-effecting
    /// write to the user's remote — SOTA #3) by setting this to "1"/"true". Fail-closed default-OFF
    /// (absent/""/"0"/"false"/anything else → no clone, no integration, no LLM synthesis call, byte-identical to
    /// today). Pinned by a test (Rule 8) — renaming it silently turns the feature off for an operator who enabled it.
    /// </summary>
    public const string IntegrateBranchEnabledEnvVar = "CODESPACE_AGENT_INTEGRATE_BRANCH_ENABLED";

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
    private readonly IHarnessModelReconciler _harnessReconciler;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IAgentWorkspaceResolver _workspaceResolver;
    private readonly IModelCredentialResolver _modelCredentials;
    private readonly IWorkspaceProviderRegistry _workspaces;
    private readonly IAgentRunCompletionNotifier _notifier;
    // Mints a fresh DI scope (→ its own DbContext) for the heartbeat loop, which runs concurrently with the event stream.
    private readonly IServiceScopeFactory _scopeFactory;
    // Reads the parent WorkflowRun's status at the claim point — the authoritative no-sandbox-under-terminal-parent guard.
    private readonly CodeSpaceDbContext _db;
    // The generic adversarial-review critic — runs over the produced change at completion when output-review is opted in.
    private readonly IStructuredCritic _critic;
    // Resolves a REFERENCED (offloaded) restored transcript back to bytes just before invocation (P3 continue).
    private readonly IArtifactOffloader _offloader;
    private readonly ILogger<AgentRunExecutor> _logger;

    public AgentRunExecutor(IAgentRunService runs, IAgentHarnessRegistry harnesses, IHarnessModelReconciler harnessReconciler, ISandboxRunnerRegistry runners, IAgentWorkspaceResolver workspaceResolver, IModelCredentialResolver modelCredentials, IWorkspaceProviderRegistry workspaces, IAgentRunCompletionNotifier notifier, IServiceScopeFactory scopeFactory, CodeSpaceDbContext db, IStructuredCritic critic, IArtifactOffloader offloader, ILogger<AgentRunExecutor> logger)
    {
        _runs = runs;
        _harnesses = harnesses;
        _harnessReconciler = harnessReconciler;
        _runners = runners;
        _workspaceResolver = workspaceResolver;
        _modelCredentials = modelCredentials;
        _workspaces = workspaces;
        _notifier = notifier;
        _scopeFactory = scopeFactory;
        _db = db;
        _critic = critic;
        _offloader = offloader;
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
            // Re-check the parent workflow run's status the instant after the Queued→Running claim wins, closing the TOCTOU
            // the reconciler's guard leaves open: the reconciler reads the parent then re-dispatches, but the parent can flip
            // terminal in the window before this claim, so without this re-check the executor would launch a sandbox under an
            // already-dead workflow. A standalone run (no WorkflowRunId) or a live parent (Suspended/Pending/Running) proceeds
            // EXACTLY as before — only a terminal parent aborts the launch (the run, now Running, is cancelled instead). INSIDE
            // the try so a fault READING the parent status lands a clean terminal Failed with the real (redacted) error, instead
            // of escaping uncaught to leave the run Running for the reconciler to later abandon with a generic reason.
            if (await AbortIfParentTerminalAsync(agentRunId, run.WorkflowRunId, claimedEpoch, cancellationToken).ConfigureAwait(false)) return;

            var task = JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)
                       ?? throw new InvalidOperationException($"AgentRun {agentRunId} has an empty task envelope.");

            // Reconcile the authored harness with the model's provider (from the pinned credential, or — for the
            // planner's loose model name — the pool row backing it) — if the pairing is impossible (e.g. an
            // Anthropic-provider model under a codex-cli default), repair to a harness that CAN drive it so the agent
            // still runs, instead of failing every agent at credential resolution.
            var reconciliation = await _harnessReconciler.ReconcileAsync(task, run.TeamId, cancellationToken).ConfigureAwait(false);
            var harness = _harnesses.Resolve(reconciliation.HarnessKind);

            if (reconciliation.Repaired)
            {
                _logger.LogWarning("AgentRun {RunId}: {Note}", agentRunId, reconciliation.Note);
                await _runs.AppendEventAsync(agentRunId, new AgentEvent { Kind = AgentEventKind.Warning, Text = reconciliation.Note! }, cancellationToken).ConfigureAwait(false);

                // Correct the stored harness so observability (the runs index, the eval scorecard's group-by) reflects
                // the harness that ACTUALLY ran, not the impossible authored one.
                await _db.AgentRun.Where(r => r.Id == agentRunId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Harness, reconciliation.HarnessKind), cancellationToken).ConfigureAwait(false);
            }

            var runnerKind = string.IsNullOrWhiteSpace(task.RunnerKind) ? DefaultRunnerKind : task.RunnerKind;
            var runner = _runners.Resolve(runnerKind);

            // Materialise the workspace (clone the bound repo) before the harness runs. Null = no workspace
            // for this run. The handle's lifetime is the run's — DisposeAsync removes the clone afterwards.
            var workspaceProvision = await _workspaceResolver.ResolveAsync(task, run.TeamId, cancellationToken).ConfigureAwait(false);
            workspace = workspaceProvision is null ? null : await _workspaces.Resolve(runnerKind).PrepareAsync(workspaceProvision, cancellationToken).ConfigureAwait(false);

            // Resolve + decrypt the model credential JUST-IN-TIME (team from the run row, never the envelope) and
            // project it onto the harness's env vars. The secret lives only in this in-memory effectiveTask →
            // SandboxSpec.Environment; it is NEVER re-persisted (CompleteAsync writes only the result). The
            // redactor (keyed on the decrypted key) strips it from any echoed event / error before it persists.
            var (secretEnv, secretRedactor, modelBaseUrl, modelProvider, defaultModel) = await ResolveModelCredentialEnvAsync(task, run.TeamId, harness, cancellationToken).ConfigureAwait(false);
            redactor = secretRedactor;

            // An "auto" run (no pinned model) falls back to the resolved credential's own default model, so a custom
            // gateway runs on ITS family instead of the CLI's built-in default (e.g. codex gpt-5.5) it can't serve.
            var effectiveModel = string.IsNullOrWhiteSpace(task.Model) ? defaultModel : task.Model;

            var effectiveTask = (workspace is null ? task : task with { WorkspaceDirectory = workspace.Directory }) with { Environment = MergeEnvironment(task.Environment, secretEnv), Model = effectiveModel };

            // P3 (3.2c): resolve a REFERENCED (offloaded) restored transcript to bytes NOW — the producer kept only the
            // ref in task_jsonb to bound its size; the harness needs the bytes to lay down the resume file. Bounded: the
            // stored transcript was captured under the capture cap, so this never fetches an unbounded blob.
            effectiveTask = await ResolveRestoredTranscriptAsync(effectiveTask, run.TeamId, cancellationToken).ConfigureAwait(false);

            // Mint the per-run socket + token ONCE so the endpoint listener and the harness's declaration agree by
            // construction (and so the token can be stamped on the durable handle for a re-attach to re-bind the same
            // one).
            var (socketPath, token) = MintMcpConnect(agentRunId);

            // Open the per-run MCP endpoint — it opens for EVERY run now, serving the read-only tools by default and the
            // full fabric only on opt-in (ResolveMcpCatalogMode). It lives ONLY for the harness span: the harness runs
            // synchronously here (RunHarnessAsync → AttachAsync blocks until exit), and `await using` inside the try
            // tears it down on EVERY exit (success / cancel / generic catch) — NOT gated on leaveWorkspaceForReattach.
            await using var mcp = OpenMcpEndpoint(effectiveTask, agentRunId, effectiveTask.Autonomy, run.TeamId, redactor, socketPath, token, claimedEpoch, effectiveTask.ApprovalConversationId, cancellationToken);

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
            var spec = ApplyEgressPolicy(
                harness.BuildInvocation(AugmentToolsForMcp(effectiveTask, mcp, mcpWiring)) with { Mcp = mcpWiring },
                effectiveTask.Permissions, modelBaseUrl, modelProvider, workspaceProvision);

            // The MCP token rides the durable handle whenever the ENDPOINT opened (not only when a declaration was
            // written) so a re-attach re-binds the SAME socket+token — the detached agent's declaration file still
            // points at it. Null when no endpoint → nothing to re-open.
            var mcpToken = mcp is null ? null : token;

            var result = await RunHarnessAsync(agentRunId, harness, runner, spec, mcpToken, redactor, ReviseSpoolKey(agentRunId, round: 0), cancellationToken).ConfigureAwait(false);

            result = await VerifyProducedWorkAsync(agentRunId, run, harness, effectiveTask, result, workspace, claimedEpoch, cancellationToken).ConfigureAwait(false);

            // S6: the bounded REVISE loop — when the objective oracle failed on something the agent can fix, or the
            // Improve-mode critic flagged the output, feed the failure detail back to the SAME agent (same workspace;
            // the same conversation when the round captured a resumable session) and re-verify through the FULL chain.
            // Each round re-pushes the same run-derived branch (a designed force overwrite) and re-grades against it,
            // so a pass can never be a stale verdict. A blocking decision (A1) defers grade+review, so no revise reason
            // surfaces and the completion choke point keeps precedence. A worker tear-down mid-round leaves the run for
            // re-attach, whose own terminal path honours the acceptance contract fail-closed — never a phantom pass.
            var reviseBudget = EffectiveReviseRounds(effectiveTask);
            string? priorReason = null;

            for (var round = 1; round <= reviseBudget; round++)
            {
                if (ReviseReasonFor(effectiveTask, result) is not { } reason) break;   // nothing left to revise — approved / passed

                // Convergence (P1b-2): a CRITIC that re-raises the identical feedback means the prior revision moved
                // nothing it cares about, and another pass will only re-produce it — stop EARLY rather than re-billing
                // the same stall, and record it so the flagged result stands for a human with an honest "stalled" note.
                // SCOPED TO THE CRITIC PATH: an ORACLE's failing-check detail is identical every round REGARDLESS of
                // what the agent tried (the check output doesn't change until it passes), so an identical oracle reason
                // is NOT a stall signal — a later round may still land the fix, so the budget runs for oracle failures.
                if (priorReason is not null && result.ExitReason == "output-flagged" && CriticConvergence.SameSignal(priorReason, reason))
                {
                    await AppendReviseStalledEventAsync(agentRunId, reason, round - 1, cancellationToken).ConfigureAwait(false);
                    break;
                }

                await AppendReviseEventAsync(agentRunId, reason, round, reviseBudget, cancellationToken).ConfigureAwait(false);

                var reviseTask = BuildReviseTask(effectiveTask, result, reason);
                var reviseSpec = ApplyEgressPolicy(harness.BuildInvocation(AugmentToolsForMcp(reviseTask, mcp, mcpWiring)) with { Mcp = mcpWiring }, reviseTask.Permissions, modelBaseUrl, modelProvider, workspaceProvision);

                var priorTranscript = result.Transcript;
                var priorUsage = result.TokenUsage;
                result = await RunHarnessAsync(agentRunId, harness, runner, reviseSpec, mcpToken, redactor, ReviseSpoolKey(agentRunId, round), cancellationToken).ConfigureAwait(false);
                result = result with { Transcript = JoinTranscripts(priorTranscript, result.Transcript), TokenUsage = SumTokenUsage(priorUsage, result.TokenUsage), ReviseRounds = round };

                // Verify under the ORIGINAL goal: the composed REVISE goal is for the harness invocation only — the
                // output critic must judge goal-alignment against what the task actually asked for, not the feedback
                // wrapper (which quotes the failure and could bias or blind the reviewer).
                result = await VerifyProducedWorkAsync(agentRunId, run, harness, reviseTask with { Goal = effectiveTask.Goal }, result, workspace, claimedEpoch, cancellationToken).ConfigureAwait(false);

                priorReason = reason;
            }

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

        // Re-attach must redact + fold against the SAME harness the original run reconciled to (so the right model
        // env is redacted); reconcile silently here — the repair event was already emitted on the first attach.
        var harness = _harnesses.Resolve((await _harnessReconciler.ReconcileAsync(task, run.TeamId, cancellationToken).ConfigureAwait(false)).HarnessKind);

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

            // S5: the acceptance invariant holds on THIS terminal path too — a contract-bearing run that completed
            // across a worker restart has no published branch to grade (see the no-push note above), so it fails
            // CLOSED rather than landing Succeeded ungraded because a crash happened at the right moment.
            result = await GradeAcceptanceIfPresentAsync(run, task, result, cancellationToken).ConfigureAwait(false);

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
        var transcript = new System.Text.StringBuilder();   // D3: the faithful raw stream of the RESUMED tail (the pre-crash prefix lived in the dead observer's run)
        var writer = new BufferedEventWriter(_runs, runId);   // same batched-append + flush-at-checkpoint path as the live tail

        async Task PersistLineAsync(string line)
        {
            transcript.AppendLine(redactor.Redact(line));

            foreach (var normalized in harness.ParseEvents(line))
            {
                var redacted = Redact(normalized, redactor);

                await writer.BufferAsync(redacted, cancellationToken).ConfigureAwait(false);
                events.Add(redacted);
            }
        }

        var sandbox = await durable.AttachAsync(handle, (line, _) => PersistLineAsync(line), cancellationToken, CheckpointHandleOffset(runId, handle, writer)).ConfigureAwait(false);

        // Final flush for the terminal-drain lines (no trailing checkpoint), as in the live path.
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        var result = MapSandboxResult(sandbox, harness, events) with { Transcript = transcript.ToString() };

        // Capture the resumable session transcript here too — a run that completes via durable re-attach (worker restart
        // mid-run) is exactly the durability case continuity serves; the config home still lives under the handle's spool.
        return await CaptureSessionTranscriptAsync(runId, task, result, harness, handle, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Map a terminal <see cref="SandboxResult"/> onto the agent-run result. A budget overrun is <see cref="AgentRunStatus.TimedOut"/>;
    /// a C3 STALL (no output for the idle window — likely a nested interactive prompt the agent can't answer) is surfaced
    /// for a human as <see cref="AgentRunStatus.NeedsReview"/> / <see cref="CompletionDisposition.Blocked"/>; any other
    /// terminal is folded by the harness from its events. Shared by the live + reattach paths so they can't drift.
    /// </summary>
    internal static AgentRunResult MapSandboxResult(SandboxResult sandbox, IAgentHarness harness, IReadOnlyList<AgentEvent> events) => sandbox.Status switch
    {
        // A timed-out / stalled agent still BURNED tokens before we killed it — capture the usage from its events
        // (the harness's own fold does this for a clean/non-zero exit; these forced-terminal paths must too) so the
        // spend shows on the run regardless of outcome.
        SandboxStatus.TimedOut => new AgentRunResult { Status = AgentRunStatus.TimedOut, ExitReason = "timed-out", Error = "The agent run exceeded its time budget and was terminated.", TokenUsage = AgentTokenUsageReader.TryRead(events) },
        SandboxStatus.Stalled => new AgentRunResult { Status = AgentRunStatus.NeedsReview, CompletionDisposition = CompletionDisposition.Blocked, ExitReason = "stalled", Error = "The agent produced no output for the configured idle window and was terminated as stalled — it is likely blocked at an interactive prompt it cannot answer unattended; a human must take over.", TokenUsage = AgentTokenUsageReader.TryRead(events) },
        _ => harness.BuildResult(events, sandbox.ExitCode),
    };

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
    /// <summary>
    /// P3: capture the harness's RESUMABLE session transcript (Claude's <c>projects/&lt;cwd&gt;/&lt;id&gt;.jsonl</c>) from the
    /// per-run config home into the result, BEFORE the spool (and its config home) is reaped — so a later CONTINUE can
    /// restore the conversation. No-op unless the harness declares a session-transcript location
    /// (<see cref="IAgentSessionTranscript"/>), the run captured a session id, and a durable handle's on-disk config
    /// home holds the file. Best-effort: any read failure logs + keeps the result unchanged (a continue then cold-starts;
    /// it NEVER flips an otherwise-successful run to Failed). The live path passes a null <paramref name="handle"/> and
    /// re-reads the one recorded at launch; the durable RE-ATTACH path passes its in-scope handle (the config home under
    /// its spool is exactly what re-attach is tailing) so a run that completes after a worker restart stays resumable too.
    /// </summary>
    private async Task<AgentRunResult> CaptureSessionTranscriptAsync(Guid runId, AgentTask task, AgentRunResult result, IAgentHarness harness, SandboxHandle? handle, CancellationToken cancellationToken)
    {
        if (harness is not IAgentSessionTranscript resumable) return result;

        if (string.IsNullOrEmpty(result.SessionId)) return result;   // no captured session → nothing to resume (both harness shapes need it)

        try
        {
            handle ??= DeserializeHandle((await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false)).RunnerHandleJson);

            if (handle is null) return result;   // a non-durable runner has no on-disk config home to read

            var configHome = LocalProcessRunner.ConfigHomePath(handle.SpoolDirectory);

            // Locate the transcript WITHIN the config home — a computable path (Claude) or a glob (Codex, whose rollout
            // name carries a timestamp unknown ahead of time). Null → this run can't address one → cold-start on continue.
            if (resumable.SessionTranscriptRelativePath(configHome, task.WorkspaceDirectory, result.SessionId) is not { } relativePath) return result;

            if (ResolveSessionTranscriptPath(configHome, relativePath) is not { } path)
            {
                _logger.LogWarning("Agent run {RunId}: the session-transcript path escaped the config home (hostile session id?); skipping capture", runId);
                return result;
            }

            if (!File.Exists(path)) return result;   // the CLI wrote its session elsewhere (cwd mismatch) or not at all — cold-start on continue

            var length = new FileInfo(path).Length;
            var cap = MaxSessionTranscriptBytes();

            if (length > cap)   // a pathological session file — skip rather than read it whole into memory (cold-start >> OOM)
            {
                _logger.LogWarning("Agent run {RunId}: session transcript is {Bytes} bytes (> {Cap} cap); skipping capture — a continue will cold-start", runId, length, cap);
                return result;
            }

            return result with { SessionTranscript = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not capture the session transcript for resume; a continue will cold-start", runId);
            return result;
        }
    }

    /// <summary>
    /// P3 (3.2c): resolve a REFERENCED restored transcript (the producer stamped <c>RestoredTranscriptArtifactId</c> to
    /// keep the bytes out of task_jsonb) to its bytes on <see cref="AgentTask.RestoredTranscript"/>, clearing the ref, so
    /// the harness's <c>BuildConfigHomeFiles</c> stays a pure bytes consumer. A task with no ref (inline bytes, or no
    /// resume at all) is returned unchanged. The offloader resolves inline-or-artifact transparently.
    /// </summary>
    private async Task<AgentTask> ResolveRestoredTranscriptAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken)
    {
        if (task.RestoredTranscriptArtifactId is not { } artifactId) return task;

        var transcript = await _offloader.ResolveAsync(teamId, task.RestoredTranscript, artifactId, cancellationToken).ConfigureAwait(false);

        return task with { RestoredTranscript = transcript, RestoredTranscriptArtifactId = null };
    }

    /// <summary>
    /// P3 (security): resolve a config-home-relative session-transcript path to an absolute path ONLY when it stays
    /// within <paramref name="configHome"/>. The session id naming the file is captured from the agent's UNTRUSTED
    /// stream unescaped, AND the agent has WRITE access to its config home (it is <c>--bind</c>-mounted), so two escapes
    /// must be blocked: a hostile id (<c>../../etc/passwd</c>) that spells out of bounds — caught lexically — and a
    /// planted SYMLINK that spells in bounds but points out. The symlink can be the LEAF (<c>ln -s /etc/passwd
    /// projects/&lt;cwd&gt;/&lt;id&gt;.jsonl</c>) OR an INTERMEDIATE DIRECTORY (<c>ln -s / sessions/leak</c>, then a real
    /// <c>rollout-&lt;id&gt;.jsonl</c> under the linked target — which a search-based locate like Codex's glob surfaces and
    /// a leaf-only resolve misses). So the check walks EVERY component from just below the config home to the leaf and
    /// fail-closes on ANY symlink: the CLIs only ever write real files/dirs here, so a symlink component in this subtree
    /// is inherently hostile. Capture runs AFTER the agent process exits, so there is no live check-then-read race.
    /// Returns null when the path escapes (the caller logs + skips); a non-existent in-bounds path is returned as-is
    /// (the caller's existence check then treats it as a cold-start).
    ///
    /// <para>RESIDUAL (documented, not closed here): a HARDLINK carries no link target, so a per-component symlink walk
    /// cannot see it. This is NOT a symlink-style escalation under the default hardening — Linux <c>protected_hardlinks=1</c>
    /// (the modern default) only permits hardlinking a file the caller can already READ, so a confined agent gains
    /// nothing it couldn't get by copying, and under bubblewrap its namespace exposes only the read-only-bound roots, not
    /// operator secrets. It is exploitable only with <c>protected_hardlinks=0</c> AND a worker-readable-but-agent-unreadable
    /// secret on the config-home filesystem — a deployment misconfiguration. No cheap managed check closes it (a realpath
    /// re-clamp does not: a hardlink's PATH is genuinely in-bounds); the airtight fix is to capture from WITHIN the
    /// sandbox namespace instead of host-side post-exit, tracked as a follow-up.</para>
    /// </summary>
    internal static string? ResolveSessionTranscriptPath(string configHome, string relativePath)
    {
        var boundary = Path.GetFullPath(configHome) + Path.DirectorySeparatorChar;
        var lexical = Path.GetFullPath(Path.Combine(configHome, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!lexical.StartsWith(boundary, StringComparison.Ordinal)) return null;   // .. escape / absolute / the home itself

        // Reject ANY symlink component below the config home — an intermediate directory symlink defeats a leaf-only
        // check and lets a search/computed path escape the tree (LinkTarget is non-null IFF the component is a symlink).
        for (var current = lexical; current.Length > boundary.Length && current.StartsWith(boundary, StringComparison.Ordinal); current = Path.GetDirectoryName(current) ?? "")
            if ((new FileInfo(current).LinkTarget ?? new DirectoryInfo(current).LinkTarget) is not null) return null;

        return lexical;
    }

    /// <summary>The session-transcript capture cap in bytes — the env override (<see cref="MaxSessionTranscriptBytesEnvVar"/>) when it parses to a positive long, else <see cref="DefaultMaxSessionTranscriptBytes"/>.</summary>
    private static long MaxSessionTranscriptBytes() =>
        ParseMaxSessionTranscriptBytes(Environment.GetEnvironmentVariable(MaxSessionTranscriptBytesEnvVar), DefaultMaxSessionTranscriptBytes);

    /// <summary>Parse the cap override — a positive long wins; anything else (null / non-numeric / non-positive) falls back to <paramref name="fallback"/>. Pure, so the parse + fallback is unit-pinned without touching the process env.</summary>
    internal static long ParseMaxSessionTranscriptBytes(string? raw, long fallback) =>
        long.TryParse(raw, out var value) && value > 0 ? value : fallback;

    private async Task<AgentRunResult> EnrichWithWorkspaceChangesAsync(Guid runId, AgentTask task, AgentRunResult result, IWorkspaceHandle? workspace, CancellationToken cancellationToken)
    {
        if (workspace is null) return result;

        try
        {
            // The PRIMARY repo's diff is git ground truth for the top-level fields — byte-identical to a single-repo run.
            var changes = await workspace.CaptureChangesAsync(cancellationToken).ConfigureAwait(false);
            result = result with { ChangedFiles = changes.ChangedFiles, FileStats = changes.FileStats, Patch = TruncatePatch(changes.Patch, MaxPatchChars), BaseSha = changes.BaseSha };

            // Multi-repo: ALSO surface every writable repo's outcome as a Change Set. A single-repo workspace skips
            // this branch entirely, so its result is unchanged (RepositoryResults empty, ChangeSetId null).
            if (workspace.Repositories.Count > 1)
                result = await CaptureRepositoryResultsAsync(runId, task, result, workspace, changes, cancellationToken).ConfigureAwait(false);

            return result;
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
    /// Multi-repo: capture EVERY writable repo's diff into <see cref="AgentRunResult.RepositoryResults"/> + stamp the
    /// run's <see cref="AgentRunResult.ChangeSetId"/>. The primary's already-captured changes are reused (no second git
    /// call), so the top-level fields and the primary's per-repo entry agree; each entry carries its <see cref="RepositoryRunResult.RepositoryId"/>
    /// resolved from the run's authoring spec. The push step fills in each entry's produced branch.
    ///
    /// <para>Per-repo ISOLATED + best-effort: a SECONDARY repo's capture failure is logged and that repo is dropped from
    /// the set — it must never abort the whole capture (which would discard the repos that captured fine and silently
    /// degrade a multi-repo run to look single-repo). The primary's capture already succeeded (it's the top-level diff),
    /// so the change set always carries at least the primary.</para>
    /// </summary>
    private async Task<AgentRunResult> CaptureRepositoryResultsAsync(Guid runId, AgentTask task, AgentRunResult result, IWorkspaceHandle workspace, WorkspaceChanges primaryChanges, CancellationToken cancellationToken)
    {
        var repoIds = task.Workspace?.Repositories.ToDictionary(r => r.Alias, r => r.RepositoryId);
        var perRepo = new List<RepositoryRunResult>();

        foreach (var repo in workspace.Repositories.Where(r => r.Access == WorkspaceAccess.Write))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changes = await CaptureOneRepoOrNullAsync(runId, repo, workspace, primaryChanges, cancellationToken).ConfigureAwait(false);

            if (changes is null) continue;   // a secondary repo's capture failed (already logged) — drop it, keep the rest

            perRepo.Add(new RepositoryRunResult
            {
                Alias = repo.Alias,
                RepositoryId = repoIds is not null && repoIds.TryGetValue(repo.Alias, out var id) ? id : null,
                ChangedFiles = changes.ChangedFiles,
                FileStats = changes.FileStats,
                // Capture this repo's diff (capped inline like the top-level patch) — the durable, base-anchored input
                // the supervisor's per-repo on-disk integration consumes; a large one is offloaded at completion.
                Patch = TruncatePatch(changes.Patch, MaxPatchChars),
                BaseSha = changes.BaseSha,
                BaseBranch = repo.BaseBranch,
                Access = WorkspaceAccess.Write,
            });
        }

        return result with { RepositoryResults = perRepo, ChangeSetId = ChangeSetIdFor(runId) };
    }

    /// <summary>Capture one writable repo's changes (the primary's are already in hand, so reuse them). Returns null when a SECONDARY repo's capture fails — logged, isolated, never aborting the whole change set. Cancellation still propagates.</summary>
    private async Task<WorkspaceChanges?> CaptureOneRepoOrNullAsync(Guid runId, WorkspaceRepositoryHandle repo, IWorkspaceHandle workspace, WorkspaceChanges primaryChanges, CancellationToken cancellationToken)
    {
        if (repo.Alias == workspace.PrimaryAlias) return primaryChanges;

        try
        {
            return await workspace.CaptureChangesAsync(repo.Alias, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: failed to capture changes for repo '{Alias}'; dropping it from the change set, keeping the others", runId, repo.Alias);
            return null;
        }
    }

    /// <summary>The stable id for the SET of branches a multi-repo run produces — run-id-derived, so a re-push of the SAME run reuses it (idempotent) and its non-null-ness distinguishes a multi-repo run from a single-repo one. A workflow RETRY of agent.code is a new run id → a new change set (like the produced branch names). Internal + static so it's unit-pinned.</summary>
    internal static string ChangeSetIdFor(Guid runId) => $"cs-{runId:N}";

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
        if (workspace is not IWorkspacePushHandle pushHandle) return result;

        var multiRepo = workspace.Repositories.Count > 1;

        // Single-repo: skip the push when nothing changed (byte-identical gate). Multi-repo skips this global gate —
        // a secondary repo may have changes the primary's top-level fields don't reflect; each per-repo push self-gates.
        if (!multiRepo && result.ChangedFiles.Count == 0 && string.IsNullOrEmpty(result.Patch)) return result;

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
            if (multiRepo) return await PushRepositoryResultsAsync(runId, result, workspace, pushHandle, cancellationToken).ConfigureAwait(false);

            var branch = await PushWithRetryAsync(ct => pushHandle.PushChangesAsync(BuildBranchName(runId), ct), cancellationToken).ConfigureAwait(false);

            return branch is null ? result : result with { ProducedBranch = branch };
        }
        catch (WorkspaceException ex)
        {
            // Best-effort: a push failure must never flip a Succeeded run to Failed. The exception message has the
            // token already redacted (the handle redacts it), so it's safe to persist onto the timeline.
            _logger.LogWarning(ex, "Agent run {RunId}: failed to push the produced branch after {Attempts} attempt(s); the run stays Succeeded with no branch output", runId, PushMaxAttempts);
            await AppendPushFailureWarningAsync(runId, ex.Message, cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>Bounded attempts for one branch push before giving up — a contract-bearing task forces the push opt-in (F4), so a single transient git failure (network blip, remote hiccup) would otherwise convert a fully correct run into <c>AcceptanceFailed("no-branch-or-repo")</c> with zero chance to recover. <see cref="WorkspaceException"/> carries no transient/deterministic classification (a flat clone/push wrapper), so this retries EVERY push failure a fixed number of times rather than sniffing error text — the same "retry blind, bounded" posture the P0.3 agent-respawn fix already took for a transient-vs-deterministic distinction git itself doesn't expose. Pinned (Rule 8).</summary>
    internal const int PushMaxAttempts = 3;

    /// <summary>Fixed backoff between push attempts — short because this runs inside the agent's own bounded wall clock; a network blip clears in well under a second, and a deterministic failure (auth, permission) just burns 3 short waits (~1s total) before falling through to the existing best-effort no-branch-output path unchanged. Pinned (Rule 8).</summary>
    internal static readonly TimeSpan PushRetryBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>Retry a single push call up to <see cref="PushMaxAttempts"/> times on <see cref="WorkspaceException"/>, with <see cref="PushRetryBackoff"/> between attempts. The FINAL attempt's exception propagates unchanged — callers keep their existing single-catch best-effort fallback (per-repo isolation for multi-repo, run-stays-Succeeded for single-repo). Internal so both push paths share one retry posture and a test can pin the attempt count directly.</summary>
    internal async Task<string?> PushWithRetryAsync(Func<CancellationToken, Task<string?>> push, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await push(cancellationToken).ConfigureAwait(false);
            }
            catch (WorkspaceException) when (attempt < PushMaxAttempts)
            {
                await Task.Delay(PushRetryBackoff, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Multi-repo: push EACH writable repo (from <see cref="AgentRunResult.RepositoryResults"/>) to its own origin under
    /// the SAME run-id-derived branch name (distinct remotes, so a shared name is coherent), folding each pushed branch
    /// back into its per-repo entry. The top-level <see cref="AgentRunResult.ProducedBranch"/> mirrors the PRIMARY repo's
    /// branch so an existing single-branch consumer keeps working. Each push self-gates (returns null for an unchanged repo).
    ///
    /// <para>Per-repo ISOLATED + best-effort: each repo's push is wrapped independently, so ONE repo's failure (a 403, a
    /// network blip) never discards the branches that already pushed — those are folded + persisted, and the failed repo
    /// gets a redacted Warning on the timeline naming it. This is why it does NOT propagate to the caller's catch: that
    /// catch returns the UNMODIFIED result, which would orphan already-pushed remote branches (live on the remote but
    /// recorded as no-branch). Cancellation still propagates (worker torn down).</para>
    /// </summary>
    private async Task<AgentRunResult> PushRepositoryResultsAsync(Guid runId, AgentRunResult result, IWorkspaceHandle workspace, IWorkspacePushHandle pushHandle, CancellationToken cancellationToken)
    {
        var branchName = BuildBranchName(runId);
        var updated = new List<RepositoryRunResult>(result.RepositoryResults.Count);
        string? primaryBranch = null;

        foreach (var repo in result.RepositoryResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pushed = await PushOneRepoOrNullAsync(runId, repo.Alias, branchName, pushHandle, cancellationToken).ConfigureAwait(false);

            updated.Add(repo with { ProducedBranch = pushed });

            if (repo.Alias == workspace.PrimaryAlias) primaryBranch = pushed;
        }

        return result with { RepositoryResults = updated, ProducedBranch = primaryBranch };
    }

    /// <summary>Push one repo by alias, ISOLATING its failure: a <see cref="WorkspaceException"/> — after <see cref="PushMaxAttempts"/> retries — is logged + surfaced as a per-repo Warning on the timeline (token already redacted) and returns null, so a sibling repo's already-pushed branch is never discarded. Cancellation propagates.</summary>
    private async Task<string?> PushOneRepoOrNullAsync(Guid runId, string alias, string branchName, IWorkspacePushHandle pushHandle, CancellationToken cancellationToken)
    {
        try
        {
            return await PushWithRetryAsync(ct => pushHandle.PushChangesAsync(alias, branchName, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: failed to push repo '{Alias}' after {Attempts} attempt(s); keeping the other repos' branches in the change set", runId, alias, PushMaxAttempts);
            await AppendPushFailureWarningAsync(runId, $"[{alias}] {ex.Message}", cancellationToken).ConfigureAwait(false);
            return null;
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

    /// <summary>The post-harness verification chain — capture the session for resume, capture the diff, publish the branch, then the OBJECTIVE oracle and the SUBJECTIVE critic. One named unit because the S6 revise loop re-runs it after every round: a revision is only ever judged by the same full chain that judged the first attempt.</summary>
    private async Task<AgentRunResult> VerifyProducedWorkAsync(Guid runId, AgentRun run, IAgentHarness harness, AgentTask task, AgentRunResult result, IWorkspaceHandle? workspace, long claimedEpoch, CancellationToken cancellationToken)
    {
        result = await CaptureSessionTranscriptAsync(runId, task, result, harness, handle: null, cancellationToken).ConfigureAwait(false);

        result = await EnrichWithWorkspaceChangesAsync(runId, task, result, workspace, cancellationToken).ConfigureAwait(false);

        result = await PushProducedBranchIfEnabledAsync(runId, task, result, workspace, claimedEpoch, cancellationToken).ConfigureAwait(false);

        result = await GradeAcceptanceIfPresentAsync(run, task, result, cancellationToken).ConfigureAwait(false);

        return await ReviewOutputIfEnabledAsync(task, result, run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Hard cap on S6 revise rounds — a runaway budget is clamped here, so a task can never buy more than this many billed re-runs inside one agent run.</summary>
    internal const int MaxReviseRoundsCap = 3;

    /// <summary>The composed revise instruction's fixed prefix — a pinned, operator-visible marker so a revise round is recognisable in any transcript regardless of harness (and a stable hook for deterministic test CLIs).</summary>
    internal const string ReviseInstructionPrefix = "REVISE:";

    /// <summary>The task's clamped revise budget: an explicit non-negative <see cref="AgentTask.MaxReviseRounds"/> wins (clamped to <see cref="MaxReviseRoundsCap"/>); null defaults to 1 under <see cref="ReviewMode.Improve"/> (Improve MEANS improve) and 0 otherwise — S5's hard-gate semantics unchanged.</summary>
    internal static int EffectiveReviseRounds(AgentTask task) =>
        task.MaxReviseRounds is { } explicitRounds ? Math.Clamp(explicitRounds, 0, MaxReviseRoundsCap)
        : task.OutputReviewMode == ReviewMode.Improve ? 1 : 0;

    /// <summary>
    /// WHY this result deserves a revise round, or null when it doesn't: an oracle failure with an agent-fixable detail
    /// (a <c>grade-error:</c> is infra — another round can't fix the grader), or an Improve-mode critic flag carrying its
    /// feedback. A deferred gate (blocking decision / multi-repo grade) sets neither signal, so this stays null and the
    /// A1 completion choke point keeps precedence; a Gate-mode flag stays a flag — only Improve buys a re-run.
    /// </summary>
    internal static string? ReviseReasonFor(AgentTask task, AgentRunResult result)
    {
        if (result is { Status: AgentRunStatus.Failed, ExitReason: "acceptance-failed", AcceptancePassed: false }
            && result.AcceptanceDetail is { } detail && IsAgentFixableOracleFailure(result, detail))
            return $"The objective acceptance check failed: {detail}";

        if (task.OutputReviewMode == ReviewMode.Improve && result is { Status: AgentRunStatus.NeedsReview, ExitReason: "output-flagged", ReviewFeedback: { Length: > 0 } feedback })
            return $"An independent reviewer flagged the change: {feedback}";

        return null;
    }

    /// <summary>An oracle failure the agent can plausibly fix with another pass — the negation of the SHARED infra classification (<see cref="AgentAcceptanceContract.IsInfraFailure"/>): grader failures, half-authored specs (<c>no-rubric</c>/<c>no-schema</c> — an agent cannot author the missing half), and publish failures with work present never buy a revise round.</summary>
    private static bool IsAgentFixableOracleFailure(AgentRunResult result, string detail) =>
        !AgentAcceptanceContract.IsInfraFailure(detail, workPresent: result.ChangedFiles.Count > 0 || !string.IsNullOrEmpty(result.Patch));

    /// <summary>Sum the rounds' token usage — the final result must bill the WHOLE run (the cost plane prices <c>ResultJson.TokenUsage</c>), not just the last round. Null when neither round reported usage.</summary>
    internal static AgentTokenUsage? SumTokenUsage(AgentTokenUsage? prior, AgentTokenUsage? current) =>
        prior is null ? current
        : current is null ? prior
        : new AgentTokenUsage { InputTokens = prior.InputTokens + current.InputTokens, OutputTokens = prior.OutputTokens + current.OutputTokens };

    /// <summary>
    /// The task for one revise round: the SAME contract with the failure fed back as the goal. WARM when the finished
    /// round captured a resumable session (id + transcript): the harness continues that conversation in a fresh config
    /// home, so the instruction is just the delta. COLD otherwise: a fresh conversation in the same workspace, so the
    /// instruction restates the original goal too. Any ancestor continue-resume riding the task is superseded by THIS
    /// run's own session; a stale offloaded-transcript ref is dropped with it.
    /// </summary>
    internal static AgentTask BuildReviseTask(AgentTask task, AgentRunResult result, string reason)
    {
        var warm = result is { SessionId.Length: > 0, SessionTranscript.Length: > 0 };

        return task with
        {
            Goal = ComposeReviseGoal(task.Goal, reason, warm),
            ResumeFromSessionId = warm ? result.SessionId : null,
            RestoredTranscript = warm ? result.SessionTranscript : null,
            RestoredTranscriptArtifactId = null,
        };
    }

    /// <summary>Compose the revise instruction. Warm (conversation continued): the failure + "fix it" — the session already holds the goal and the work. Cold (fresh conversation, same workspace): restate the original goal so the new session carries the full contract.</summary>
    internal static string ComposeReviseGoal(string originalGoal, string reason, bool warmResume) =>
        warmResume
            ? $"{ReviseInstructionPrefix} Your previous attempt did not pass verification.\n\n{reason}\n\nRevise your work in this workspace so the verification passes. Do not start over, and do not change what the task is."
            : $"{ReviseInstructionPrefix} A previous attempt at the goal below did not pass verification.\n\n{reason}\n\nOriginal goal:\n{originalGoal}\n\nThe previous attempt's work is already in this workspace. Revise it so the verification passes.";

    /// <summary>Round-scoped durable spool key: round 0 (the first attempt) keeps the bare run key — byte-identical spool paths for every non-revised run — and each revise round gets its own suffixed directory, because a spool is single-use (its exit marker means THIS launch finished; reusing round 1's would complete round 2 instantly with a stale code). Re-attach is unaffected: it reads the ACTUAL spool path off the persisted handle, never recomputes it.</summary>
    internal static string ReviseSpoolKey(Guid runId, int round) => round == 0 ? runId.ToString("N") : $"{runId:N}-r{round}";

    /// <summary>Concatenate the rounds' faithful raw streams with a visible seam, so the final offloaded transcript holds the WHOLE run — every round — not just the last one.</summary>
    internal static string? JoinTranscripts(string? prior, string? current) =>
        string.IsNullOrEmpty(prior) ? current
        : string.IsNullOrEmpty(current) ? prior
        : $"{prior}\n--- revise round ---\n{current}";

    /// <summary>The revise-round announcement's pinned prefix — the journal describer matches it to classify the Warning as a REVISE beat, so the copy and the classification can't drift apart.</summary>
    internal const string ReviseAnnouncementPrefix = "Verification failed — revising";

    /// <summary>Announce a revise round on the timeline — the operator sees WHY the run is taking another pass and which round of the budget this is. Best-effort like the other completion-tail events.</summary>
    private async Task AppendReviseEventAsync(Guid runId, string reason, int round, int budget, CancellationToken cancellationToken)
    {
        try
        {
            await _runs.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.Warning, Text = $"{ReviseAnnouncementPrefix} (round {round} of {budget}). {reason}" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not record the revise-round event", runId);
        }
    }

    /// <summary>The stalled-revision announcement's pinned prefix — the convergence early-stop's operator-visible marker (a stable hook for tests + the journal).</summary>
    internal const string ReviseStalledPrefix = "Revision stalled — the same issue persisted";

    /// <summary>Announce that the revise loop stopped EARLY because the same problem re-surfaced unchanged — the operator sees the loop gave up on an unmovable issue rather than silently exhausting the budget. Best-effort.</summary>
    private async Task AppendReviseStalledEventAsync(Guid runId, string reason, int roundsRun, CancellationToken cancellationToken)
    {
        try
        {
            await _runs.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.Warning, Text = $"{ReviseStalledPrefix} after {roundsRun} round(s); stopping early. {reason}" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not record the revise-stalled event", runId);
        }
    }

    /// <summary>
    /// The OBJECTIVE oracle gate (triad S5): grade <c>AgentTask.Acceptance</c> against the produced branch at
    /// completion — the single-agent twin of the supervisor's per-unit fold gate, on the SAME grader (a server-run
    /// check on an agent-independent clone, never a model self-report). FAIL-CLOSED: a failing check — or a
    /// contract with no branch/repo to grade — re-grades the would-be Succeeded run to Failed
    /// ("acceptance-failed"); the captured work (branch, diff, transcript) is preserved for diagnosis. Runs BEFORE
    /// the subjective output critic, so a failed oracle never bills a review. Deferred (verdict null, run intact):
    /// no contract, a non-success result, or a multi-repo result (per-repo grading is a follow-on, mirroring the
    /// supervisor fold's same deferral). Grader errors record not-accepted rather than crashing the completion.
    /// </summary>
    internal async Task<AgentRunResult> GradeAcceptanceIfPresentAsync(AgentRun run, AgentTask task, AgentRunResult result, CancellationToken cancellationToken)
    {
        if (!AgentAcceptanceContract.RequiresGrade(task)) return result;
        if (result.Status != AgentRunStatus.Succeeded) return result;
        if (result.RepositoryResults.Count > 0) return result;   // multi-repo → deferred, exactly like the supervisor's unit gate

        // A1 always takes precedence (the same defer the output critic honours): a run that left a decision.request
        // unanswered re-grades to NeedsReview(NeedsDecision) WITH the decision linkage at the completion choke point
        // — flunking it here first would strand the decision unlinked and skip the retry-resume loop. The answered,
        // resumed attempt gets graded at ITS completion.
        using (var ledgerScope = _scopeFactory.CreateScope())
        {
            var ledger = ledgerScope.ServiceProvider.GetRequiredService<IToolCallLedgerService>();
            if (await ledger.FindBlockingDecisionIdAsync(run.Id, cancellationToken).ConfigureAwait(false) is not null) return result;
        }

        var spec = task.Acceptance!;
        var command = spec.Command.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

        if (string.IsNullOrEmpty(result.ProducedBranch) || task.RepositoryId is not { } repositoryId)
        {
            _logger.LogWarning("Agent run {RunId}: an acceptance contract is present but there is no produced branch/repo to grade — failing closed", run.Id);

            return AcceptanceFailed(result, "no-branch-or-repo");
        }

        BenchmarkGrade grade;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            grade = await scope.ServiceProvider.GetRequiredService<ISupervisorAcceptanceGrader>()
                .GradeAsync(repositoryId, run.TeamId, result.ProducedBranch, spec with { Command = command }, SupervisorLane.AcceptanceGradeTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: the acceptance grade failed unexpectedly; recording not-accepted", run.Id);

            grade = new BenchmarkGrade { Passed = false, Detail = $"grade-error: {ex.Message}" };
        }

        if (grade.Passed)
        {
            _logger.LogInformation("Agent run {RunId}: the acceptance check passed ({Detail})", run.Id, grade.Detail);

            return result with { AcceptancePassed = true, AcceptanceDetail = grade.Detail };
        }

        _logger.LogWarning("Agent run {RunId}: the acceptance check FAILED ({Detail}) — re-grading the run to Failed", run.Id, grade.Detail);

        return AcceptanceFailed(result, grade.Detail);
    }

    private static AgentRunResult AcceptanceFailed(AgentRunResult result, string? detail) => AgentAcceptanceContract.FailClosed(result, detail);

    /// <summary>
    /// Review the agent's produced change with an INDEPENDENT critic at completion. Doubly-off ⇒ byte-identical
    /// (no per-run <c>OutputReviewMode</c> baked, OR the shared <see cref="CriticToggle"/> kill-switch is off). Self-skips
    /// when there's nothing to gate — a non-success, or a no-op / re-attach run with no captured diff. A DISAPPROVED change
    /// re-grades the would-be <see cref="AgentRunStatus.Succeeded"/> run to <see cref="AgentRunStatus.NeedsReview"/>
    /// (<see cref="CompletionDisposition.NeedsReview"/>) so a human looks before the downstream PR-open (Succeeded-gated)
    /// consumes it; the captured work is preserved, and the critique rides <see cref="AgentRunResult.ReviewFeedback"/>.
    /// FAILS OPEN — a failed review keeps the original result. Under <see cref="ReviewMode.Improve"/> the S6 revise loop
    /// reads the flag + feedback and buys the agent a bounded re-run before the flag stands (Gate never re-runs).
    /// </summary>
    internal async Task<AgentRunResult> ReviewOutputIfEnabledAsync(AgentTask task, AgentRunResult result, AgentRun run, CancellationToken cancellationToken)
    {
        if (task.OutputReviewMode == ReviewMode.None || !CriticToggle.Enabled) return result;
        if (result.Status != AgentRunStatus.Succeeded) return result;
        if (result.ChangedFiles.Count == 0 && string.IsNullOrEmpty(result.Patch)) return result;

        var runId = run.Id;

        // Defer to the A1 completion gate: a run that left a decision.request unanswered will be re-graded to
        // NeedsReview(NeedsDecision) at the completion choke point WITH the specific decision linkage (the stronger
        // signal). Don't pre-empt it by flipping to output-flagged here — A1 always takes precedence (the same ordering
        // FinalOutputReview/A2 respects). Resolve the ledger from a fresh scope (the heartbeat-loop pattern), not a ctor dep.
        using (var ledgerScope = _scopeFactory.CreateScope())
        {
            var ledger = ledgerScope.ServiceProvider.GetRequiredService<IToolCallLedgerService>();
            if (await ledger.FindBlockingDecisionIdAsync(runId, cancellationToken).ConfigureAwait(false) is not null) return result;
        }

        // S8 reviewer ladder: an opted-in AGENT reviewer first (a real read-only run cloning the produced branch on a
        // distinct-first harness — it inspects the repository, not a diff string), laddering DOWN to the in-process
        // model critic when the agent can't produce a verdict (no branch, staging/parse failure) — an agent review is
        // never worse than a model review, and a model review is never worse than none.
        var verdict = task.ReviewerAgent
            ? await ReviewWithAgentAsync(task, result, run, cancellationToken).ConfigureAwait(false)
            : CriticVerdict.ReviewFailed(ReviewMode.Gate, "agent-reviewer: not requested");

        var agentReviewed = !verdict.Failed;

        if (verdict.Failed)
            verdict = await ReviewRecordedAsync(
                new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent change", Artifact = RenderChange(result), Goal = task.Goal },
                run, task.ReviewerModelId, cancellationToken).ConfigureAwait(false);

        // D② approve co-sign: an AGENT reviewer's APPROVAL gets a cheap independent MODEL co-check before it counts.
        // The reviewer agent READS the produced tree — hostile committed content could try to instruct it to approve
        // (the injection prize) — so approval requires CONSENSUS across the two independent channels: a model
        // disagreement fails toward the human (NeedsReview carrying both sides), never a silent pass. A FAILED
        // co-check keeps the agent's approval (fail-open — a broken co-check must not manufacture a flag), and a
        // DISAPPROVING agent verdict needs no co-sign (the worst case of a wrong block is one wasted revise round).
        if (agentReviewed && verdict.Approved)
        {
            var coSign = await ReviewRecordedAsync(
                new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent change", Artifact = RenderChange(result), Goal = task.Goal },
                run, task.ReviewerModelId, cancellationToken).ConfigureAwait(false);

            if (!coSign.Failed && !coSign.Approved)
                verdict = coSign with { Rationale = $"The reviewer agent approved, but the independent model co-check disagreed: {coSign.Rationale}" };
        }

        if (verdict.Failed || verdict.Approved) return result;   // fail-open, or a clean pass ⇒ byte-identical

        await AppendOutputFlaggedWarningAsync(runId, verdict, cancellationToken).ConfigureAwait(false);

        return result with { Status = AgentRunStatus.NeedsReview, CompletionDisposition = CompletionDisposition.NeedsReview, ExitReason = "output-flagged", ReviewFeedback = RenderReviewFeedback(verdict) };
    }

    /// <summary>
    /// Run the output-review critic — the executor's one IN-PROCESS model call — WITH recording. The executor runs in a
    /// Hangfire job OUTSIDE the engine's per-node <see cref="LlmCallContext"/> scope (which the engine pushes around every
    /// node), so this call would otherwise record NOTHING. Mirror the engine: push the run's
    /// <c>(WorkflowRunId, NodeId, IterationKey)</c> cell + a FRESH-scope ledger writer/offloader (the long-running-job
    /// pattern above, not a ctor dep) around the call, so the recording decorator lands its <c>interaction.*</c> onto the
    /// SAME <c>workflow_run_record</c> ledger as the rest of the run, keyed to the spawning agent.code node. A standalone
    /// run (no <see cref="AgentRun.WorkflowRunId"/>) has no workflow ledger ⇒ no scope pushed ⇒ records nothing
    /// (fail-open), and the critic runs byte-identically.
    /// </summary>
    private async Task<CriticVerdict> ReviewRecordedAsync(CriticRequest request, AgentRun run, Guid? reviewerModelId, CancellationToken cancellationToken)
    {
        if (run.WorkflowRunId is not { } workflowRunId)
            return await _critic.ReviewAsync(request, run.TeamId, reviewerModelId, cancellationToken).ConfigureAwait(false);

        using var recordingScope = _scopeFactory.CreateScope();
        var recordLogger = recordingScope.ServiceProvider.GetRequiredService<IRunRecordLogger>();
        var offloader = recordingScope.ServiceProvider.GetRequiredService<IArtifactOffloader>();

        using (LlmCallContext.Push(new LlmCallScope(workflowRunId, run.TeamId, run.NodeId, run.IterationKey, "agent.critic", recordLogger, offloader)))
            return await _critic.ReviewAsync(request, run.TeamId, reviewerModelId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Run the S8 AGENT reviewer from a fresh scope (it stages + executes a first-class run — the heartbeat-loop scope pattern). Never throws (the reviewer is itself fail-closed to a failed verdict).</summary>
    private async Task<CriticVerdict> ReviewWithAgentAsync(AgentTask task, AgentRunResult result, AgentRun run, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<Review.IAgentOutputReviewer>()
            .ReviewAsync(task, result, run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The critic's verdict as one feedback string — persisted on the result (WHY the run was flagged) and fed back verbatim by an Improve revise round.</summary>
    internal static string RenderReviewFeedback(CriticVerdict verdict) =>
        verdict.Issues.Count > 0 ? $"{verdict.Rationale} Issues: {string.Join("; ", verdict.Issues)}" : verdict.Rationale;

    /// <summary>Render the produced change for the critic — the git unified diff (already capped), with the agent's summary + the changed-file list as context.</summary>
    private static string RenderChange(AgentRunResult result)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(result.Summary)) builder.AppendLine($"Agent summary: {result.Summary}").AppendLine();

        builder.AppendLine($"Changed files ({result.ChangedFiles.Count}): {string.Join(", ", result.ChangedFiles)}").AppendLine();
        builder.AppendLine("Diff:").AppendLine(string.IsNullOrEmpty(result.Patch) ? "(no unified diff captured)" : result.Patch);

        return builder.ToString();
    }

    /// <summary>Append a Warning event so the operator sees on the timeline WHY the run was flagged for review. Best-effort: a failure to record it never masks the run's terminal write.</summary>
    private async Task AppendOutputFlaggedWarningAsync(Guid runId, CriticVerdict verdict, CancellationToken cancellationToken)
    {
        var issues = verdict.Issues.Count > 0 ? $" Issues: {string.Join("; ", verdict.Issues)}." : "";

        try
        {
            await _runs.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.Warning, Text = $"Output flagged by the reviewer — a human should look before this is consumed: {verdict.Rationale}{issues}" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not record the output-flagged warning event", runId);
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

    /// <summary>True ONLY for "1"/"true"/"TRUE" (trimmed); fail-closed default-OFF for null / "" / "0" / "false" / anything else. Mirrors <see cref="IsPushEnabled"/> exactly (Rule 8). Internal so it's unit-pinned; production reads it through this single gate.</summary>
    internal static bool IsIntegrateEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(IntegrateBranchEnabledEnvVar)?.Trim();

        return raw is "1" or "true" or "TRUE";
    }

    /// <summary>
    /// The single gate deciding whether a run INTEGRATES its parallel agent contributions on disk: the
    /// deployment-wide env flag (<see cref="IsIntegrateEnabled"/>) OR an explicit per-run/profile opt-in. Fail-open
    /// toward the operator (a per-run opt-in turns integration ON for one run without flipping the ambient flag) —
    /// the SAME shape as <see cref="ShouldPushProducedBranch"/>. Pure + internal so it's unit-pinned and production
    /// reads it through this single gate.
    /// </summary>
    internal static bool ShouldIntegrate(bool perRunOptIn) => IsIntegrateEnabled() || perRunOptIn;

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
    /// Which slice of the tool catalog this run's endpoint serves. The endpoint now opens for EVERY run; this is the
    /// ONLY thing the opt-in changes. <see cref="ShouldOpenMcpEndpoint"/> ON (the ambient flag OR the per-run opt-in)
    /// selects <see cref="McpCatalogMode.Full"/> — the whole registry incl. the side-effecting fabric, byte-identical to
    /// before. OFF (the default) selects <see cref="McpCatalogMode.ReadOnly"/> — only read-only tools (e.g.
    /// <c>get_context</c> + the git reads) are served, so a default run still reaches the safe read tools without
    /// exposing any side effect. Pure + internal so it's unit-pinned.
    /// </summary>
    internal static McpCatalogMode ResolveMcpCatalogMode(AgentTask task) => ShouldOpenMcpEndpoint(task) ? McpCatalogMode.Full : McpCatalogMode.ReadOnly;

    /// <summary>
    /// A BOOT diagnostic the worker host calls once at startup so a mis-configured tool fabric is VISIBLE at deploy time,
    /// not silently discovered as a tool-less run hours later. The MCP endpoint now opens for EVERY run (serving the
    /// read-only tools by default, the full fabric on opt-in), so the <c>codespace-mcp</c> proxy is needed by every run:
    /// when it can't be resolved at <see cref="LocalProcessRunner.McpProxyBinaryPath"/>, log a clear Warning naming the
    /// resolved path + the override env var (every run will degrade to TOOL-LESS); otherwise log a confirming
    /// Information line that also notes whether the full side-effecting fabric is enabled deployment-wide
    /// (<see cref="IsMcpEndpointEnabled"/>). Pure logging — never throws, never fails boot (the fabric is optional infra).
    /// The per-run <see cref="BuildMcpWiring"/> ALSO fail-closes + logs per run; this is the proactive deploy-time half of
    /// the same fail-closed signal. Internal + static so it's unit-pinnable without a host.
    /// </summary>
    public static void LogMcpProxyReadiness(ILogger logger)
    {
        var proxyPath = LocalProcessRunner.McpProxyBinaryPath();

        if (File.Exists(proxyPath))
        {
            logger.LogInformation("MCP tool fabric ready; codespace-mcp proxy resolved at '{ProxyPath}'. Read-only tools (get_context + git reads) serve by default; the full side-effecting fabric is {FabricState}.", proxyPath, IsMcpEndpointEnabled() ? "ENABLED deployment-wide" : "opt-in per run");
            return;
        }

        logger.LogWarning("The codespace-mcp proxy binary was NOT found at '{ProxyPath}'. Agent runs will fail closed to a TOOL-LESS run (no MCP wiring written) — including the read-only tools served by default. Publish the proxy alongside the worker or set {OverrideEnvVar} to its absolute path.", proxyPath, LocalProcessRunner.McpProxyPathEnvVar);
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
    /// Open the run's per-run UDS MCP endpoint on the given socket + token. The endpoint opens for EVERY run; what it
    /// SERVES is the <see cref="ResolveMcpCatalogMode"/> mode — ReadOnly by default (only read-only tools, e.g.
    /// <c>get_context</c> + git reads), Full when the run opted into the side-effecting fabric. Mints a DEDICATED DI
    /// scope (its own DbContext) because the framing loop runs CONCURRENTLY with the harness + the event-append path, so
    /// it must not share the heartbeat / streaming scope. The scope is held for the endpoint's life and disposed in the
    /// endpoint's <see cref="AgentMcpEndpoint.DisposeAsync"/>. The connect registry is a DI singleton, so resolving it
    /// from this scope hands a consumer the same map. Fail-soft (A10): a host that can't bind a UDS disposes the scope,
    /// logs a Warning, and returns null; the endpoint is optional infra, not the run, so the run still proceeds without
    /// it (and a proxy-less deployment still degrades to a tool-less run via the wiring's own fail-close).
    /// </summary>
    private AgentMcpEndpoint? OpenMcpEndpoint(AgentTask task, Guid runId, AgentAutonomyLevel autonomy, Guid teamId, SecretRedactor redactor, string socketPath, string token, long fenceEpoch, Guid? approvalConversationId, CancellationToken ct)
    {
        var scope = _scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAgentToolRegistry>();
        var connects = scope.ServiceProvider.GetRequiredService<IAgentMcpConnectRegistry>();

        // The governance flag is read ONCE here (Rule 8 single gate); the endpoint threads it + the run's fence epoch
        // into each connection's handler so a side-effecting tool call is ledger-tracked. Flag-OFF → no ledger rows.
        var governanceEnabled = McpRequestHandler.IsGovernanceEnabled();

        // The catalog mode is the ONLY thing the opt-in changes now: the endpoint ALWAYS opens, serving the read-only
        // tools by default and the whole fabric only when the run opted in.
        var catalogMode = ResolveMcpCatalogMode(task);

        try
        {
            return new AgentMcpEndpoint(runId, registry, autonomy, teamId, redactor, socketPath, token, connects, scope, ct, _logger, fenceEpoch, governanceEnabled, approvalConversationId, catalogMode);
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
    /// launch bound). Null — no re-open — only when the run had no fabric (the handle carries no token, e.g. a pre-fabric
    /// run); the wiring flag is NOT re-checked here (the agent's declaration already exists, so the endpoint must serve
    /// it regardless). The catalog mode is re-resolved from the SAME task, so the re-opened endpoint serves the SAME
    /// slice the launch did. Fail-soft via <see cref="OpenMcpEndpoint"/>.
    /// </summary>
    private AgentMcpEndpoint? ReopenMcpEndpointForReattach(AgentTask task, Guid runId, AgentAutonomyLevel autonomy, Guid teamId, SecretRedactor redactor, SandboxHandle handle, long fenceEpoch, Guid? approvalConversationId, CancellationToken ct)
    {
        if (handle.McpRunToken is not { Length: > 0 } token) return null;

        var socketPath = LocalProcessRunner.McpSocketPathFor(runId.ToString("N"));

        // The reopened endpoint redacts tool-result text with a redactor the caller resolved fresh from the run's
        // credential — kept INDEPENDENT of the fold's own resolution (a second decrypt is harmless + idempotent) so the
        // delicate fingerprint-gated marker-only re-tail in ReattachAndFoldAsync is left untouched. The caller degrades
        // it to SecretRedactor.None on a resolution failure, so a deleted/rotated credential never blocks the reattach.
        return OpenMcpEndpoint(task, runId, autonomy, teamId, redactor, socketPath, token, fenceEpoch, approvalConversationId, ct);
    }

    /// <summary>
    /// Build the MCP wiring the runner uses to point the live CLI at the fabric — or null (no wiring) unless BOTH hold:
    /// the endpoint ACTUALLY opened (a non-null endpoint encodes "the bind succeeded"; the endpoint opens for every run
    /// now, serving the read-only tools by default), and the chosen harness declares an MCP-server shape
    /// (<see cref="IMcpHarnessDeclaration"/>).
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
    private async Task<(IReadOnlyDictionary<string, string> Env, SecretRedactor Redactor, string? ModelBaseUrl, string? ModelProvider, string? DefaultModel)> ResolveModelCredentialEnvAsync(AgentTask task, Guid teamId, IAgentHarness harness, CancellationToken cancellationToken)
    {
        var projector = harness as IModelCredentialProjector;

        var credential = await _modelCredentials.ResolveAsync(task, teamId, projector, cancellationToken).ConfigureAwait(false);

        var env = projector is not null && credential is not null ? projector.ProjectToEnv(credential) : EmptySecretEnv;
        // Redact only the actual SECRET (the api key / gateway token) — never the non-secret base URL.
        var redactor = credential?.ApiKey is { Length: > 0 } key ? new SecretRedactor(new[] { key }) : SecretRedactor.None;

        // The non-secret base URL + provider tag flow out so a restricted (Allowlist) run can pin its model-API host
        // in the egress allowlist (B3.3b). DefaultModel flows out so a model-less ("auto") run falls back to one of the
        // credential's own models instead of the CLI default. All null when no credential resolved.
        return (env, redactor, credential?.BaseUrl, credential?.Provider, credential?.DefaultModel);
    }

    /// <summary>
    /// When the run opted into <see cref="AgentEgressPolicy.Allowlist"/> egress (B3.3b), set the sandbox's egress
    /// allowlist to the run's model-API host + each repo's git host + the operator's extra hosts. FAIL-CLOSED: an
    /// Allowlist run whose host set comes out EMPTY is SEVERED (AllowNetwork=false), never left to fall through to
    /// Full egress (<see cref="Sandbox.Isolation.SandboxEgressPolicy"/> reads an empty allowlist as "no allowlist →
    /// Full"). Full egress (the default) returns the spec UNCHANGED — byte-identical to today.
    /// </summary>
    internal static SandboxSpec ApplyEgressPolicy(SandboxSpec spec, AgentPermissions permissions, string? modelBaseUrl, string? modelProvider, WorkspaceProvisionRequest? workspace)
    {
        if (permissions.Egress != AgentEgressPolicy.Allowlist) return spec;

        var hosts = EgressAllowlistBuilder.Build(modelBaseUrl, modelProvider, CloneUrlsOf(workspace), permissions.EgressAllowHosts);

        if (hosts.Count == 0) return spec with { AllowNetwork = false, EgressAllowlist = null };   // fail-closed: no derivable host ⇒ sever, NEVER Full

        return spec with { EgressAllowlist = hosts };
    }

    /// <summary>The git clone URLs of every repo in the run's workspace provision (empty for a no-repo run) — the source of the allowlist's git hosts.</summary>
    private static IReadOnlyList<string> CloneUrlsOf(WorkspaceProvisionRequest? workspace) =>
        workspace is null ? Array.Empty<string>() : workspace.Repositories.Select(r => r.CloneRequest.RepositoryUrl).ToList();

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

    private async Task<AgentRunResult> RunHarnessAsync(Guid runId, IAgentHarness harness, ISandboxRunner runner, SandboxSpec spec, string? mcpToken, SecretRedactor redactor, string spoolKey, CancellationToken cancellationToken)
    {
        var events = new List<AgentEvent>();
        var transcript = new System.Text.StringBuilder();   // D3: the FAITHFUL raw stream — every redacted line, incl. ones ParseEvent drops
        var writer = new BufferedEventWriter(_runs, runId);   // batches the DB inserts; flushed at each spool checkpoint + once at the end

        async Task PersistLineAsync(string line)
        {
            // Capture the faithful transcript FIRST — redact the raw line, then keep it whether or not ParseEvents
            // surfaces any event. ParseEvents drops blank/unrecognized lines; the transcript keeps them so a replay
            // is exact. Redacted before it's held, so no secret reaches the offloaded artifact.
            transcript.AppendLine(redactor.Redact(line));

            // ONE native line can carry several content blocks (reasoning + tool_use + text) → several events, in
            // stream order. Each is redacted BEFORE the append-only log freezes it (the log can't be edited later).
            foreach (var normalized in harness.ParseEvents(line))
            {
                var redacted = Redact(normalized, redactor);

                await writer.BufferAsync(redacted, cancellationToken).ConfigureAwait(false);   // buffered — one batched INSERT per spool checkpoint, not one per line
                events.Add(redacted);   // in-memory, for the harness's result fold
            }
        }

        // The heartbeat is owned by ExecuteAsync (it spans the whole run, including the completion tail), so
        // streaming here just emits events — a quiet step's liveness is kept fresh by that outer heartbeat. The
        // redactor's fingerprint is stamped onto the durable handle so a re-attach can prove it rebuilt the SAME
        // key before re-tailing the spool (a rotated/deleted key → marker-only, never an unmaskable leak). The MCP
        // token rides the handle too so a re-attach re-binds the SAME socket+token the agent's declaration carries.
        var sandbox = await RunSandboxAsync(runId, runner, spec, PersistLineAsync, writer, redactor.Fingerprint, mcpToken, spoolKey, cancellationToken).ConfigureAwait(false);

        // Final flush: the durable runner's terminal-drain paths (CompleteFromSpool/Timeout/Vanished) deliver the last
        // lines WITHOUT a trailing checkpoint, so anything buffered after the last checkpoint must be flushed here
        // before the result is folded + the run completes. (A no-op when the buffer is already empty.)
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Events are already redacted, so a result the harness folds from them (summary / error) is redacted too.
        var result = MapSandboxResult(sandbox, harness, events);

        // D3: attach the faithful raw transcript (offloaded to an artifact at completion if large — the common case).
        return result with { Transcript = transcript.ToString() };
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
    private async Task<SandboxResult> RunSandboxAsync(Guid runId, ISandboxRunner runner, SandboxSpec spec, Func<string, Task> persistLine, BufferedEventWriter writer, string? keyFingerprint, string? mcpToken, string spoolKey, CancellationToken cancellationToken)
    {
        if (runner is ISandboxDurableRunner durable)
            return await RunDurableAsync(runId, durable, spec, persistLine, writer, keyFingerprint, mcpToken, spoolKey, cancellationToken).ConfigureAwait(false);

        // Non-durable fallback (no spool/checkpoint): the writer's size cap + the caller's final flush drain it.
        return await RunAndStreamAsync(runner, spec, persistLine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Launch the run to its durable spool, persist the returned handle (keyed by the run id) BEFORE
    /// observing, then attach + tail. Persisting first is what lets the reconciler recover this run if this
    /// observer dies mid-tail. On a host-shutdown cancel the attach stops observing WITHOUT killing the
    /// process (leaving the run Running for re-attach/recovery); only the spec timeout terminates it.
    /// </summary>
    private async Task<SandboxResult> RunDurableAsync(Guid runId, ISandboxDurableRunner durable, SandboxSpec spec, Func<string, Task> persistLine, BufferedEventWriter writer, string? keyFingerprint, string? mcpToken, string spoolKey, CancellationToken cancellationToken)
    {
        // Stamp the injected-key fingerprint + the MCP run token onto the handle at launch. The fingerprint lets a
        // re-attach verify it rebuilt the same redactor before re-tailing (rotated/deleted credential → marker-only);
        // the token lets it RE-OPEN the endpoint with the SAME socket+token the agent's declaration file already holds.
        // The spool key is round-scoped (ReviseSpoolKey) — a revise round must never inherit a finished spool's exit marker.
        var handle = (await durable.LaunchAsync(spec, spoolKey, cancellationToken).ConfigureAwait(false)) with { InjectedKeyFingerprint = keyFingerprint, McpRunToken = mcpToken };

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

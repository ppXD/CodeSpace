using System.Text;
using System.Text.Json;
using System.Net.Sockets;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Settings;
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
/// execution core a worker (the agent.run node's Hangfire job) invokes — substrate-neutral, driving
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
    /// <summary>Cap on the captured diff inlined into the persisted result row (~1 MB). A larger diff is truncated with a marker; the full diff belongs in the artifact layer (a later slice).</summary>
    private const int MaxPatchChars = 1_000_000;

    /// <summary>Redacted durable reason for a writable repository whose git facts could not be captured.</summary>
    internal const string RepositoryCaptureUnavailableCode = "capture-unavailable";

    /// <summary>
    /// Air-gapped/large-context operator override (Rule 8) for the max session-transcript file size the P3 capture will
    /// read into memory. The session <c>.jsonl</c> is read whole into a string and then offloaded, so the transient
    /// per-capture peak is ~3× the file size (a ~2× UTF-8→UTF-16 string co-resident with the ~1× <c>byte[]</c> the
    /// artifact offloader encodes) — and it is NOT bounded across concurrency, so the worst-case envelope is roughly
    /// <c>runningParallelism × 3× cap</c> when many runs complete at once (raise the cap only on a worker sized for it;
    /// LOWER it on a constrained one). Beyond the cap the capture SKIPS (a continue cold-starts) rather than risk an OOM.
    ///
    /// <para>The skip is a DECIDED limit, not a missing slice — measured, so the next author does not re-derive it. A
    /// streaming segmented put removes the capture-side peak but NOT the limit, for two reasons the write side cannot
    /// see. (1) Restore does not shrink: the harness consumes the transcript as a <c>string</c>
    /// (<c>ConfigHomeFile.Content</c>), so a resume must still materialize the whole session — the unbounded read moves
    /// from capture to restore rather than disappearing. (2) The storage is permanent and mostly orphaned: this capture
    /// runs once per S6 revise round (up to <c>1 + MaxReviseRoundsCap</c> times per run) and each round's result
    /// OVERWRITES the last, whereas the inline carrier offloads once at completion — so every superseded round's bytes
    /// stay in <c>workflow_artifact</c>, which has no reaper anywhere in the codebase.</para>
    ///
    /// <para>So removing the cliff costs an artifact RETENTION path first; until one exists, RAISING this cap is the
    /// supported lever, and its cost is BOTH halves: worker memory (the envelope above) AND a proportional durable
    /// one — the captured transcript is offloaded once at completion, so a raised cap raises, linearly, the size of
    /// one permanent <c>workflow_artifact</c> object per run, in the same reaper-less table this paragraph names.
    /// It is one object rather than one per MiB per revise round, which is why it is the supported lever and the
    /// segmented carrier was not; it is not free. Pinned by
    /// <c>An_over_cap_session_is_skipped_and_leaves_the_result_untouched</c>.</para>
    /// </summary>
    public const string MaxSessionTranscriptBytesEnvVar = "CODESPACE_AGENT_MAX_SESSION_TRANSCRIPT_BYTES";

    /// <summary>Default session-transcript capture cap — 32 MiB comfortably covers realistic multi-hour conversations; a larger file is treated as pathological and skipped. Env-overridable via <see cref="MaxSessionTranscriptBytesEnvVar"/>.</summary>
    internal const long DefaultMaxSessionTranscriptBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Whether a run with no explicit opt-in INTEGRATES its K parallel agent contributions into ONE branch. Committed
    /// and ON: K parallel agents that each publish their own branch and leave the human to merge them is the
    /// hand-back this arc exists to remove, and the step is bounded — it clones, integrates, and synthesises once.
    /// Changing it is a one-line reviewed edit.
    /// </summary>
    internal const bool IntegrateBranchByDefault = true;

    /// <summary>
    /// Whether a run whose task expresses no preference gets the FULL tool catalog (the side-effecting fabric) rather
    /// than the read-only slice. Committed here rather than read from the environment: this is the posture the only
    /// deployment that exists already ran, and a security posture that changes with an unreviewed environment variable
    /// is exactly what this codebase does not keep. Changing it is a one-line edit in a reviewed PR.
    /// </summary>
    internal const bool FullToolCatalogByDefault = true;
    private static readonly TimeSpan ShadowLogTerminalizationBudget = TimeSpan.FromSeconds(30);

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
    private readonly Workflows.Artifacts.IArtifactStore _artifacts;
    // The publish-or-park ledger: upserted at the end of every verification pass, regardless of the run's status.
    private readonly IPublishManifestStore _manifests;
    private readonly IArtifactManifestStore _artifactManifests;
    private readonly Capture.ICaptureIntentService _captureIntents;
    private readonly IAgentRunLogCaptureBridge? _logCapture;
    // G1: the lossless native-record plane, dual-written beside the normalized event log. Optional for the same reason
    // _logCapture is — a hand-built test double must not have to know about a shadow plane, and a run must never depend
    // on one. Null (or a plane that will not open) leaves the streaming path byte-for-byte what it was; a plane that
    // DOES open only adds rows of its own, on its own unit of work, and re-raises a harness parser's throw unchanged.
    private readonly INativeRecordPlane? _nativeRecords;
    // The publish guard chain (Order ascending) — see EvaluatePublishGuardsAsync. Sorted once at construction so
    // production reads a stable sequence regardless of DI registration order.
    private readonly IReadOnlyList<IPublishGuard> _publishGuards;
    // The runner kind a task that pins none executes on: the deployment default (AgentDefaultRunnerSetting), read
    // once at construction. SandboxKinds.Local when no setting was supplied — a hand-built test double is not
    // required to know about a configuration class, and that is the value this line hard-coded before the key existed.
    private readonly string _defaultRunnerKind;
    private readonly Services.RunData.IRunDataCompletenessWriter? _completeness;
    private readonly ILogger<AgentRunExecutor> _logger;

    public AgentRunExecutor(IAgentRunService runs, IAgentHarnessRegistry harnesses, IHarnessModelReconciler harnessReconciler, ISandboxRunnerRegistry runners, IAgentWorkspaceResolver workspaceResolver, IModelCredentialResolver modelCredentials, IWorkspaceProviderRegistry workspaces, IAgentRunCompletionNotifier notifier, IServiceScopeFactory scopeFactory, CodeSpaceDbContext db, IStructuredCritic critic, IArtifactOffloader offloader, Workflows.Artifacts.IArtifactStore artifacts, IPublishManifestStore manifests, IArtifactManifestStore artifactManifests, Capture.ICaptureIntentService captureIntents, IEnumerable<IPublishGuard> publishGuards, ILogger<AgentRunExecutor> logger, IAgentRunLogCaptureBridge? logCapture = null, INativeRecordPlane? nativeRecords = null, AgentDefaultRunnerSetting? defaultRunner = null, Services.RunData.IRunDataCompletenessWriter? completeness = null)
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
        _artifacts = artifacts;
        _manifests = manifests;
        _artifactManifests = artifactManifests;
        _captureIntents = captureIntents;
        _logCapture = logCapture;
        _nativeRecords = nativeRecords;
        _defaultRunnerKind = defaultRunner?.Value ?? SandboxKinds.Local;
        _completeness = completeness;
        // Tolerate a null enumerable (a hand-built test double that never exercises the push path) — zero guards
        // registered is a legitimate state (every push clears), not a constructor-time crash.
        _publishGuards = (publishGuards ?? Enumerable.Empty<IPublishGuard>()).OrderBy(g => g.Order).ToList();
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
            if (await AbortIfParentTerminalAsync(agentRunId, run.TeamId, run.WorkflowRunId, claimedEpoch, cancellationToken).ConfigureAwait(false)) return;

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

            var runnerKind = string.IsNullOrWhiteSpace(task.RunnerKind) ? _defaultRunnerKind : task.RunnerKind;
            var runner = _runners.Resolve(runnerKind);

            // Materialise the workspace (clone the bound repo) before the harness runs. Null = no workspace
            // for this run. The handle's lifetime is the run's — DisposeAsync removes the clone afterwards.
            var workspaceProvision = await _workspaceResolver.ResolveAsync(task, run.TeamId, cancellationToken).ConfigureAwait(false);
            workspace = workspaceProvision is null ? null : await _workspaces.Resolve(runnerKind).PrepareAsync(workspaceProvision, cancellationToken).ConfigureAwait(false);

            // DC-4 slice 2 / C2: a repo-less run with NO world of its own needs one — a scratch working directory
            // the harness runs in, the capture reads from, and the oracle grades against. Without it the harness ran
            // with a NULL working directory and anything it wrote (a report.md it was never asked to declare) died
            // with the process, unreachable and unaccountable. The declared-deliverable condition that used to gate
            // this is gone: a run only knows what it declared, and the interesting loss was always the file nobody
            // declared.
            //
            // A task that NAMES its own WorkspaceDirectory already HAS a world and never had the defect — minting a
            // scratch for it would REPLACE the caller's directory on the effective task below, moving the harness
            // out of the tree the operator pointed it at and re-keying the session-transcript capture (which reads
            // Task.WorkspaceDirectory) onto an empty temp dir. That is a silent hijack, not a repair.
            workspace ??= string.IsNullOrWhiteSpace(task.WorkspaceDirectory) ? Workspace.ScratchWorkspaceHandle.Create(agentRunId) : null;

            // The primary repo's directory + cloned base SHA, stamped onto the durable handle at launch so a
            // re-attach can capture the diff even after the live workspace handle object dies with this worker.
            var primaryRepo = workspace?.Repositories.FirstOrDefault(r => r.Alias == workspace.PrimaryAlias);
            var workspaceDirectory = primaryRepo?.Directory;
            var workspaceBaseSha = primaryRepo?.BaseSha;

            // Resolve + decrypt the model credential JUST-IN-TIME (team from the run row, never the envelope) and
            // project it onto the harness's env vars. The secret lives only in this in-memory effectiveTask →
            // SandboxSpec.Environment; it is NEVER re-persisted (CompleteAsync writes only the result). The
            // redactor (keyed on the decrypted key) strips it from any echoed event / error before it persists.
            var (secretEnv, secretRedactor, modelBaseUrl, modelProvider, defaultModel, modelCredentialId) = await ResolveModelCredentialEnvAsync(task, run.TeamId, harness, cancellationToken).ConfigureAwait(false);
            redactor = secretRedactor;

            // An "auto" run (no pinned model) falls back to the resolved credential's own default model, so a custom
            // gateway runs on ITS family instead of the CLI's built-in default (e.g. codex gpt-5.5) it can't serve.
            var effectiveModel = string.IsNullOrWhiteSpace(task.Model) ? defaultModel : task.Model;

            // Surface the RESOLVED model on the run NOW — so the agent's identity strip shows what it's running the moment
            // it starts, from the DISPATCH data, not only after the rollout backfills it at completion. Only when the
            // operator left it blank (a pin is already displayed); the resolved model is not a secret, so re-persisting the
            // stored task (the ORIGINAL, no injected env) with just its model filled is safe.
            if (string.IsNullOrWhiteSpace(task.Model) && !string.IsNullOrWhiteSpace(effectiveModel))
                await PersistResolvedModelAsync(agentRunId, task with { Model = effectiveModel }, cancellationToken).ConfigureAwait(false);

            var effectiveTask = (workspace is null ? task : task with { WorkspaceDirectory = workspace.Directory }) with { Environment = MergeEnvironment(task.Environment, secretEnv), Model = effectiveModel };

            // D3: an escalation the DISPATCHER already decided this attempt owes — the agent.run node's respawn after
            // an attempt whose own evidence said the MODEL was the limit. It arrives as a request (why + the prior
            // model, the tier floor) because only the executor reads the credentialed pool. Resolved HERE, after the
            // credential resolve, so the pick is bounded to the very credential ROW whose key is now in this
            // sandbox's environment — which is what keeps that key, the egress base URL and the reconciled harness
            // valid for the escalated model without re-resolving any of them.
            var escalation = task.Escalation is { } requested
                ? await ResolveEscalationAsync(requested.Reason, run.TeamId, modelCredentialId, modelProvider, requested.From ?? effectiveModel, cancellationToken).ConfigureAwait(false)
                : null;

            // A no-op escalation is announced ONCE per run: the fact that this team has nothing stronger does not
            // change between rounds, and repeating it every round would bury the round's real reason under noise.
            var noStrongerModelNoted = false;

            if (escalation is not null)
            {
                effectiveTask = ApplyEscalation(effectiveTask, escalation);
                noStrongerModelNoted = escalation.To is null;
                await AppendEscalationEventAsync(agentRunId, escalation, cancellationToken).ConfigureAwait(false);

                // Surface the escalated model on the run the same way the resolved model is surfaced above — the
                // identity strip must show what this attempt is ACTUALLY running, not the model it was authored with.
                if (escalation.To is { Length: > 0 } escalated)
                    await PersistResolvedModelAsync(agentRunId, task with { Model = escalated }, cancellationToken).ConfigureAwait(false);
            }

            // The escalation event's sibling, for the OTHER thing a dispatcher can decide a respawn owes: this
            // attempt IS the gateway-format-fault repair. Announced here, at the moment the repair actually runs,
            // so the note can never outlive the respawn it describes — and once, for BOTH retry lanes, because
            // both write the same degrade into the same envelope (AgentRetryCauses.ApplyFormatFaultMitigation).
            if (Supervisor.AgentRetryCauses.IsFormatFaultMitigated(effectiveTask))
                await AppendMitigationEventAsync(agentRunId, cancellationToken).ConfigureAwait(false);

            // The model in force for the NEXT harness invocation, carried across revise rounds: an escalation won in
            // round 1 must not evaporate in round 2 just because round 2's own result asked for nothing further.
            var dispatchedModel = effectiveTask.Model;

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
            var spec = HardenSpec(
                harness.BuildInvocation(AugmentToolsForMcp(effectiveTask, mcp, mcpWiring)) with { Mcp = mcpWiring },
                effectiveTask, modelBaseUrl, modelProvider, workspaceProvision);

            // The MCP token rides the durable handle whenever the ENDPOINT opened (not only when a declaration was
            // written) so a re-attach re-binds the SAME socket+token — the detached agent's declaration file still
            // points at it. Null when no endpoint → nothing to re-open.
            var mcpToken = mcp is null ? null : token;

            // The run token is the fourth secret this launch injects: it rides the MCP declaration's server env
            // (McpDeclarationWriter.TokenEnvVar) into the agent's config home, so a CLI that echoes its loaded MCP
            // config — an init banner, a "failed to start server" dump — puts a live capability straight into
            // AgentRun.Error and the append-only log. It only EXISTS after the mint, which is why it joins the
            // redactor here rather than in BuildRunRedactor. Folded exactly when it is stamped on the handle, so the
            // re-attach that rebuilds from handle.McpRunToken reproduces this fingerprint precisely — a token folded
            // in when none was stamped would fail the re-attach's equality gate for a secret that never left the
            // worker. The endpoint above keeps the pre-fold redactor: it is the token's own issuer and validator, and
            // when there is no token to fold there is no endpoint either.
            redactor = WithMcpRunToken(redactor, mcpToken);

            // D3/G0: the run's faithful raw stream, accumulated across EVERY revise round without being retained —
            // bounded at the artifact offloader's own inline threshold, so what the durable record carries is
            // unchanged while the heap stops growing with stdout. Disposed with the run (its spill file with it), and
            // anything past the budget spills into the run's OWN spool directory — so if this worker dies before that
            // dispose, the spool reaper reclaims the spill with the rest of the run's spool instead of leaking it.
            await using var transcript = new AgentTranscriptSpool(TranscriptSpillDirectory(agentRunId));

            var runContext = new HarnessRunContext
            {
                RunId = agentRunId, TeamId = run.TeamId, ActorId = run.CreatedBy, WorkerFenceEpoch = claimedEpoch,
                Harness = harness, Runner = runner, Spec = spec, McpToken = mcpToken, Redactor = redactor,
                SpoolKey = ReviseSpoolKey(agentRunId, round: 0), Transcript = transcript,
                WorkspaceDirectory = workspaceDirectory, WorkspaceBaseSha = workspaceBaseSha,
            };
            var result = await RunHarnessAsync(runContext, cancellationToken).ConfigureAwait(false);

            // P2 (capture-intent saga): the harness exited — the capture window opens HERE, before any of its
            // individually best-effort side effects (diff, offload, push, manifest). A crash inside the window
            // leaves this promise Intended; recovery marks it INDETERMINATE — visible, never a silent Succeeded.
            await _captureIntents.OpenAsync(agentRunId, run.TeamId, run.WorkflowRunId, claimedEpoch, CaptureExpectationsOf(effectiveTask), cancellationToken).ConfigureAwait(false);

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

                var reviseTask = BuildReviseTask(effectiveTask, result, reason) with { Model = dispatchedModel };

                // D3: the round that just failed IS the evidence. When it says the model was the limit — an
                // over-claim, or a check that failed on real work, never a grader / environment / gateway fault —
                // this round reaches for a stronger credentialed model instead of re-running the same one and
                // expecting a different answer. A pool with nothing stronger records the fact and changes nothing.
                if (EscalationReasonFor(result) is { } escalationReason)
                {
                    escalation = await ResolveEscalationAsync(escalationReason, run.TeamId, modelCredentialId, modelProvider, dispatchedModel, cancellationToken).ConfigureAwait(false);
                    reviseTask = ApplyEscalation(reviseTask, escalation);

                    if (escalation.To is { Length: > 0 })
                    {
                        dispatchedModel = reviseTask.Model;
                        await AppendEscalationEventAsync(agentRunId, escalation, cancellationToken).ConfigureAwait(false);

                        // Keep the PERSISTED envelope truthful per round, not just at launch: the identity strip
                        // reads it live, and it is the fallback floor a later attempt's escalation measures from
                        // when the harness's own stream never names a model.
                        await PersistResolvedModelAsync(agentRunId, task with { Model = dispatchedModel }, cancellationToken).ConfigureAwait(false);
                    }
                    else if (!noStrongerModelNoted)
                    {
                        noStrongerModelNoted = true;
                        await AppendEscalationEventAsync(agentRunId, escalation, cancellationToken).ConfigureAwait(false);
                    }
                }

                var reviseSpec = HardenSpec(harness.BuildInvocation(AugmentToolsForMcp(reviseTask, mcp, mcpWiring)) with { Mcp = mcpWiring }, reviseTask, modelBaseUrl, modelProvider, workspaceProvision);

                var priorUsage = result.TokenUsage;

                // The seam separating this round's raw stream from what came before. Marked, not written: the same
                // spool carries every round, and a round that emits nothing must contribute no seam — exactly what
                // the string join's empty short-circuits did.
                transcript.MarkSeam(ReviseTranscriptSeam);

                result = await RunHarnessAsync(runContext with { Spec = reviseSpec, SpoolKey = ReviseSpoolKey(agentRunId, round) }, cancellationToken).ConfigureAwait(false);
                result = result with { TokenUsage = SumTokenUsage(priorUsage, result.TokenUsage), ReviseRounds = round };

                // Verify under the ORIGINAL goal: the composed REVISE goal is for the harness invocation only — the
                // output critic must judge goal-alignment against what the task actually asked for, not the feedback
                // wrapper (which quotes the failure and could bias or blind the reviewer).
                result = await VerifyProducedWorkAsync(agentRunId, run, harness, reviseTask with { Goal = effectiveTask.Goal }, result, workspace, claimedEpoch, cancellationToken).ConfigureAwait(false);

                priorReason = reason;
            }

            // D3: the escalation belongs on the durable result — the LAST one applied, which is the model the run
            // actually ended on. A record whose To is null (nothing in the pool beat the floor) still lands: the
            // one-model case must read as "we tried to reach higher and could not", never as silence.
            if (escalation is not null) result = result with { ModelEscalation = escalation };

            // The run is OVER and its evidence still says the model was the limit — so the escalation this run can
            // no longer spend belongs to the NEXT attempt. Resolved here rather than left for the respawning node
            // to derive: only the executor reads the pool, and the answer decides whether the failure is worth
            // respawning at all (a null To means a respawn would re-burn the identical model, and stays
            // deterministic). Costs one bounded read, and only on a run that already failed its own check.
            if (EscalationReasonFor(result) is { } nextReason)
                result = result with { ProposedEscalation = await ResolveEscalationAsync(nextReason, run.TeamId, modelCredentialId, modelProvider, dispatchedModel, cancellationToken).ConfigureAwait(false) };

            // P0-B2: stamp what the fabric ACTUALLY did — observed off the live endpoint while it is still open
            // (the await-using disposes it at scope end). The re-attach path deliberately leaves this null: the
            // original launch's declaration facts are not durably observable across a restart, and evidence is
            // never fabricated.
            result = AttachMcpEvidence(result, effectiveTask, mcp, mcpWiring);

            result = await AttachTranscriptAsync(_artifacts, result, run.TeamId, transcript, cancellationToken).ConfigureAwait(false);

            // The capture sequence ran to its persist (a CONFIRMED empty included) — commit the promise with the
            // observed facts before the terminal CAS, so a crash between the two replays as re-verify + idempotent
            // re-commit, never as a terminal run with an unresolved promise.
            await _captureIntents.CommitAsync(agentRunId, claimedEpoch, CaptureFactsOf(result, effectiveTask), cancellationToken).ConfigureAwait(false);

            await CompleteAndNotifyAsync(agentRunId, run.TeamId, result, claimedEpoch, cancellationToken).ConfigureAwait(false);
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
            await CompleteAndNotifyAsync(agentRunId, run.TeamId, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "executor-error", Error = redactor.Redact(ex.Message) }, claimedEpoch, cancellationToken).ConfigureAwait(false);
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
            // NOTE: deliberately NO branch PUSH on the re-attach path — re-attach never re-resolves a push credential
            // (ReattachAndFoldAsync folds the result from the spool + exit code, no git diff of its own), so a run
            // that needs its branch on the remote must still produce it on the original live ExecuteAsync path. The
            // clone directory itself DOES survive (deliberately left in place for this exact case) with its base SHA
            // persisted on the handle at launch, so EnrichWithReattachWorkspaceChangesAsync below still CAPTURES the
            // diff (I1) via IWorkspacePathCapture — read-only, credential-free — even though nothing gets pushed here.
            var reattach = new ReattachFoldContext
            {
                RunId = agentRunId, TeamId = run.TeamId, ActorId = run.CreatedBy, WorkerFenceEpoch = expectedEpoch,
                Durable = durable, Handle = handle, Task = task, Harness = harness,
            };
            var result = await ReattachAndFoldAsync(reattach, cancellationToken).ConfigureAwait(false);

            if (result is null) return;   // couldn't safely observe (no redactor, still running) — leave Running for a later sweep

            // P2 (capture-intent saga): the re-attach observed a finished process — ITS capture window opens here,
            // under the reclaim-bumped epoch this pass runs at (one promise per attempt).
            await _captureIntents.OpenAsync(agentRunId, run.TeamId, run.WorkflowRunId, expectedEpoch, CaptureExpectationsOf(task), cancellationToken).ConfigureAwait(false);

            result = await EnrichWithReattachWorkspaceChangesAsync(agentRunId, run.TeamId, handle, result, cancellationToken).ConfigureAwait(false);

            result = await MintPublishEvidenceAsync(agentRunId, run.TeamId, result, cancellationToken).ConfigureAwait(false);

            // S5: the acceptance invariant holds on THIS terminal path too — a contract-bearing run that completed
            // across a worker restart has no published branch to grade (see the no-push note above), so it fails
            // CLOSED rather than landing Succeeded ungraded because a crash happened at the right moment. The live
            // workspace handle (repo clone OR scratch) died with the worker, so the repo-less lane has no world
            // here either — null keeps the fail-closed posture.
            result = await GradeAcceptanceIfPresentAsync(run, task, result, workspace: null, cancellationToken).ConfigureAwait(false);

            // Publish-or-park (I1/I2): record what the re-attach path recovered, exactly like the live path.
            await PersistPublishManifestAsync(agentRunId, run, task, result, expectedEpoch, cancellationToken).ConfigureAwait(false);

            await _captureIntents.CommitAsync(agentRunId, expectedEpoch, CaptureFactsOf(result, task), cancellationToken).ConfigureAwait(false);

            await CompleteAndNotifyAsync(agentRunId, run.TeamId, result, expectedEpoch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;   // worker torn down again — leave Running for the next re-attach
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent run {RunId} failed during re-attach", agentRunId);
            await CompleteAndNotifyAsync(agentRunId, run.TeamId, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "reattach-error", Error = "The agent run could not be re-attached after a restart and was failed." }, expectedEpoch, cancellationToken).ConfigureAwait(false);
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
    ///
    /// <para>The run's MCP capability token is re-folded from the handle — it was minted at launch, not derived from
    /// the credential, so re-resolving alone would rebuild a NARROWER redactor that both leaks an echoed token and
    /// fails the fingerprint gate for every fabric-carrying run.</para>
    /// </summary>
    private async Task<AgentRunResult?> ReattachAndFoldAsync(ReattachFoldContext context, CancellationToken cancellationToken)
    {
        SecretRedactor redactor;
        try
        {
            redactor = WithMcpRunToken((await ResolveModelCredentialEnvAsync(context.Task, context.TeamId, context.Harness, cancellationToken).ConfigureAwait(false)).Redactor, context.Handle.McpRunToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not re-resolve the credential to redact the re-attached tail; completing from the exit marker only to avoid leaking an echoed secret", context.RunId);
            return await CompleteFromMarkerWithCaptureGapAsync(context, "redactor-resolution-failed", "The durable native log could not be captured because its original redaction credential was unavailable after worker recovery.", cancellationToken).ConfigureAwait(false);
        }

        // Re-tail ONLY when the rebuilt redactor provably matches the one that masked the original output — its
        // fingerprint must equal the one stamped at launch. A mismatch (credential deleted/rotated, team-default
        // changed; both-null = a run with no injected secret → safe) means we can no longer mask a key the spool
        // may echo, so complete from the marker only rather than freeze an unmaskable secret into the log.
        if (redactor.Fingerprint != context.Handle.InjectedKeyFingerprint)
        {
            _logger.LogWarning("Agent run {RunId}: the re-resolved credential no longer matches the one injected at launch (deleted/rotated); completing from the exit marker only to avoid leaking an echoed secret", context.RunId);
            return await CompleteFromMarkerWithCaptureGapAsync(context, "redactor-fingerprint-mismatch", "The durable native log could not be captured because its redaction credential changed after worker recovery.", cancellationToken).ConfigureAwait(false);
        }

        var folder = context.Harness.CreateFolder();   // BOUNDED, exactly as the live tail folds — a re-attached run must not be able to exhaust the heap either
        var facts = AgentRunFacts.For(context.Harness);   // driven alongside the folder, exactly as the live tail does, so both paths reach MapSandboxResult with the same inputs
        await using var transcript = new AgentTranscriptSpool(TranscriptSpillDirectory(context.RunId));   // D3/G0: the faithful raw stream of the RESUMED tail (the pre-crash prefix lived in the dead observer's run), bounded exactly as the live tail's is and spilled into the SAME run-owned, reaper-swept spool directory
        var writer = new BufferedEventWriter(_runs, context.RunId);   // same batched-append + flush-at-checkpoint path as the live tail
        var native = await OpenResumedCaptureAsync(context, redactor, cancellationToken).ConfigureAwait(false);   // G1: the RESUMED frame stream of the same process, continuing its source cursor and the execution's reduction
        var applicationSourceHead = context.Handle.StdoutOffset;

        async Task PersistFrameAsync(SandboxOutputFrame output)
        {
            var line = output.Text;
            var redactedLine = redactor.Redact(line);

            // A best-effort native flush may have failed after the normalized events were durable and before this
            // source head was checkpointed. Rewind the reader to the plane head to recover those native frames, but do
            // not feed their already-consumed text back into the transcript, normalized log, folder or facts.
            if (output.SourceStartOffsetBytes < applicationSourceHead)
            {
                var backfill = await native.CaptureBackfillAsync(output, redactedLine, context.Harness, cancellationToken).ConfigureAwait(false);
                foreach (var normalized in backfill.Events)
                {
                    var redacted = Redact(normalized, redactor);
                    native.Project(backfill, redacted);
                }
                return;
            }

            await transcript.AppendLineAsync(redactedLine, cancellationToken).ConfigureAwait(false);

            var frame = await native.CaptureAsync(output, redactedLine, context.Harness, cancellationToken).ConfigureAwait(false);

            foreach (var normalized in frame.Events)
            {
                var redacted = Redact(normalized, redactor);

                await writer.BufferAsync(redacted, cancellationToken).ConfigureAwait(false);

                native.Project(frame, redacted);
                folder.Add(redacted);
                facts.Add(redacted);
            }
        }

        var handle = EnsureLogCaptureHandle(context.Handle, context.Durable);
        if (handle != context.Handle)
            await _runs.SetRunnerHandleAsync(context.RunId, JsonSerializer.Serialize(handle, AgentJson.Options), cancellationToken).ConfigureAwait(false);
        var capture = await OpenLogCaptureAsync(new LogCaptureContext(context.TeamId, context.RunId, context.ActorId, context.WorkerFenceEpoch, redactor), context.Durable, handle, cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(capture.Handle, handle) && capture.Handle != handle)
            await _runs.SetRunnerHandleAsync(context.RunId, JsonSerializer.Serialize(capture.Handle, AgentJson.Options), cancellationToken).ConfigureAwait(false);
        var sandbox = await capture.ObserveAsync((capturedHandle, token) =>
        {
            var replayHandle = capturedHandle with { StdoutOffset = Math.Min(capturedHandle.StdoutOffset, native.ReplayStartOffset) };
            return context.Durable.AttachAsync(replayHandle, (frame, _) => PersistFrameAsync(frame), token, CheckpointHandleOffset(context.RunId, capturedHandle, new HarnessSinks(writer, native)));
        }, cancellationToken).ConfigureAwait(false);

        // Final flush for the terminal-drain lines (no trailing checkpoint), as in the live path.
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Same ordering as the live path: this stream's frames and their checkpoint become durable, and only then does
        // the diagnostics opening resume the execution's reduction from that checkpoint.
        await native.FlushAsync(cancellationToken).ConfigureAwait(false);
        await RecordDiagnosticsAsync(new DiagnosticCapture(context.TeamId, context.RunId, context.WorkerFenceEpoch, context.Harness, redactor, capture.Handle, context.Durable), cancellationToken).ConfigureAwait(false);

        // Same terminal drain for the frame plane, then close the process this re-attach was observing — the SAME
        // attempt the original launch appended, because a resumed opening records against it rather than inventing a
        // second row for one process.
        await native.CloseAsync(ObservedExitCode(sandbox), cancellationToken).ConfigureAwait(false);

        ReportUnestablishedFacts(context.Harness, facts, context.RunId);

        var result = await AttachTranscriptAsync(_artifacts, MapSandboxResult(Redacted(sandbox, redactor), folder, facts), context.TeamId, transcript, cancellationToken).ConfigureAwait(false);

        // Capture the resumable session transcript here too — a run that completes via durable re-attach (worker restart
        // mid-run) is exactly the durability case continuity serves; the config home still lives under the handle's spool.
        return await CaptureSessionTranscriptAsync(new SessionCapture(context.RunId, context.Task, context.Harness, capture.Handle), result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <see cref="AgentRunResult.ExitReason"/> a cgroup resource-ceiling kill stamps — the machine-readable marker
    /// the agent.run node's retry verdict keys on to tell "the ceiling killed it" (a respawn runs at the SAME committed
    /// ceiling and dies identically) from a plain non-zero exit (a candidate transient worth one more agent). Pinned by
    /// a unit test (Rule 8) so producer and consumer cannot drift into silently restoring the retry loop.
    /// </summary>
    public const string ResourceExhaustedExitReason = "resource-exhausted";

    /// <summary>
    /// Map a terminal <see cref="SandboxResult"/> onto the agent-run result. A budget overrun is <see cref="AgentRunStatus.TimedOut"/>;
    /// a C3 STALL (no output for the idle window — likely a nested interactive prompt the agent can't answer) is surfaced
    /// for a human as <see cref="AgentRunStatus.NeedsReview"/> / <see cref="CompletionDisposition.Blocked"/>; a run the
    /// sandbox's memory ceiling OOM-killed is a <see cref="AgentRunStatus.Failed"/> that NAMES the ceiling instead of
    /// letting the harness fold report the agent's last message as the cause; any other terminal is folded by the
    /// harness from its events. Shared by the live + reattach paths so they can't drift.
    /// All three forced-terminal branches also capture <see cref="AgentRunResult.SessionId"/> when the events carry one —
    /// the sole missing input a later RETRY needs to WARM-resume the killed agent's conversation instead of cold-starting.
    /// They read the executor's own <see cref="AgentRunFacts"/>, never the harness's folder: those three are
    /// harness-independent by construction, so making them depend on what a given folder chose to keep would let a
    /// harness silently drop them from every forced terminal (Rule 7 — a sibling accumulator, not a wider folder).
    /// The folded branch also hands the folder the run's stderr, which is the process's OTHER opening and therefore
    /// reaches no folder through its events: a harness whose protocol stream said nothing about the failure can fold
    /// the process's own last words in rather than reporting a bare exit code (see <see cref="AgentDiagnosticExcerpt"/>).
    /// The caller redacts it first — <see cref="Redacted"/> — because unlike the events it arrives raw.
    /// </summary>
    internal static AgentRunResult MapSandboxResult(SandboxResult sandbox, IAgentEventFolder folder, AgentRunFacts facts) => sandbox.Status switch
    {
        // A timed-out / stalled agent still BURNED tokens before we killed it — capture the usage from its events
        // (the harness's own fold does this for a clean/non-zero exit; these forced-terminal paths must too) so the
        // spend shows on the run regardless of outcome. It may ALSO have a resumable session (a harness's early
        // lifecycle event — Claude's system/init line, Codex's thread.started — carries the id before the kill), so
        // capture that too: this is what turns a forced-terminal's later RETRY warm (continuing the conversation)
        // instead of always cold — the fold's first-seen session id + the AgentRun.SessionId write + the supervisor's
        // FindResumableSubtaskAttemptAsync are already generic over every terminal status; this was the missing input.
        SandboxStatus.TimedOut => new AgentRunResult { Status = AgentRunStatus.TimedOut, ExitReason = "timed-out", Error = "The agent run exceeded its time budget and was terminated.", TokenUsage = facts.TokenUsage, SessionId = facts.SessionId, Model = facts.Model },
        SandboxStatus.Stalled => new AgentRunResult { Status = AgentRunStatus.NeedsReview, CompletionDisposition = CompletionDisposition.Blocked, ExitReason = AgentAcceptanceContract.StalledExitReason, Error = "The agent produced no output for the configured idle window and was terminated as stalled — it is likely blocked at an interactive prompt it cannot answer unattended; a human must take over.", TokenUsage = facts.TokenUsage, SessionId = facts.SessionId, Model = facts.Model },
        // The third forced terminal, and the reason it cannot be left to the harness fold: the fold's error falls back
        // to the agent's own last message, so an OOM-killed run reported whatever the CLI happened to be saying as its
        // cause. Say what actually happened, and let the retry verdict see it (see ResourceExhaustedExitReason).
        SandboxStatus.ResourceExhausted => new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = ResourceExhaustedExitReason, Error = $"The agent run exceeded its resource ceiling and its process tree was killed by the kernel (exit {SandboxExitCode.Describe(sandbox.ExitCode)}). This is the sandbox limit for the run's autonomy tier, not a fault in the agent's work — the fix is a higher ceiling (a less-narrow deployment memory budget, a higher autonomy tier, or a change to the committed per-tier table by PR), never another attempt under the same one.", TokenUsage = facts.TokenUsage, SessionId = facts.SessionId, Model = facts.Model },
        _ => folder.BuildResult(facts, sandbox.ExitCode, sandbox.Stderr),
    };

    /// <summary>
    /// The same masking every event already gets, applied to the run's diagnostics before they can reach the folded
    /// result. The event stream is redacted line by line as it is captured, so a result folded from it is redacted
    /// too; <see cref="SandboxResult.Stderr"/> comes back from the runner RAW, and it is now an input to that same
    /// result — so an echoed key on a fatal line would otherwise be frozen into <c>AgentRun.error</c>.
    /// </summary>
    private static SandboxResult Redacted(SandboxResult sandbox, SecretRedactor redactor) =>
        redactor.IsEmpty || sandbox.Stderr.Length == 0 ? sandbox : sandbox with { Stderr = redactor.Redact(sandbox.Stderr) };

    /// <summary>Fallback when the credential can't be re-resolved to redact a re-attached tail: complete from the exit marker WITHOUT re-tailing (so no unredacted line reaches the log) — Succeeded/Failed by the code if it's present, Failed if the process is gone, or null (leave Running for a later sweep) both when it's still alive and we can't safely observe it AND when this worker can't answer the handle's liveness at all (<see cref="SandboxRunState.Indeterminate"/> — another host minted it).</summary>
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

    private async Task<AgentRunResult?> CompleteFromMarkerWithCaptureGapAsync(ReattachFoldContext context, string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        var result = await CompleteFromMarkerOnlyAsync(context.Durable, context.Handle, cancellationToken).ConfigureAwait(false);
        if (result == null || _logCapture == null || context.Durable is not ISandboxDurableLogSource source) return result;
        var handle = EnsureLogCaptureHandle(context.Handle, context.Durable);
        if (handle != context.Handle)
            await _runs.SetRunnerHandleAsync(context.RunId, JsonSerializer.Serialize(handle, AgentJson.Options), cancellationToken).ConfigureAwait(false);
        try
        {
            await _logCapture.RecordGapAsync(new AgentRunLogCaptureGapRequest
            {
                TeamId = context.TeamId, AgentRunId = context.RunId, WorkerFenceEpoch = context.WorkerFenceEpoch,
                Handle = handle, Source = source, ErrorCode = errorCode, ErrorMessage = errorMessage,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Agent run {RunId} native-log capture gap could not be persisted; marker-only result remains unchanged", context.RunId);
        }
        return result;
    }

    private static SandboxHandle? DeserializeHandle(string? handleJson)
    {
        if (string.IsNullOrWhiteSpace(handleJson)) return null;

        try { return JsonSerializer.Deserialize<SandboxHandle>(handleJson, AgentJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Land the terminal result (fenced on the claim epoch, so a reclaimed-then-revived worker loses), then fire the completion notifier (which resumes the agent.run node parked on this run). The notifier is best-effort + swallows its own failures, so completion is never masked by a resume error.</summary>
    private async Task CompleteAndNotifyAsync(Guid runId, Guid teamId, AgentRunResult result, long expectedEpoch, CancellationToken cancellationToken)
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

        if (_logCapture != null)
        {
            using var shadow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shadow.CancelAfter(ShadowLogTerminalizationBudget);
            try { await _logCapture.CompleteRunAsync(teamId, runId, expectedEpoch, shadow.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Agent run {RunId} shadow log terminalization exceeded its post-terminal budget; durable Open state remains for reconciliation", runId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Agent run {RunId} shadow log terminalization failed after task completion; durable log state remains independently recoverable", runId);
            }
        }

        await TerminalizeHarnessExecutionAsync(teamId, runId, expectedEpoch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Close the run's live harness execution. This is the ONE place every executor terminal passes through — the
    /// clean close, the executor-error catch, a parent that went terminal at the claim, the re-attach's own two exits,
    /// and the forced terminals (a timeout or a stall reaches it through the same result) — so an execution is left
    /// Running only where nobody with authority closed it. Leaving it Running is not untidy but blocking: 0137 refuses
    /// to open a generation over a live predecessor, so the Agent Run's next execution would be unrepresentable.
    ///
    /// <para>The fence is passed on, and it is why this is safe to reach from the branch above that swallows
    /// <see cref="AgentRunTransitionException"/>. That branch cannot tell "the reconciler landed the run first" from "a
    /// reclaim took the run away", because a lost completion CAS raises the same exception. The first must terminalize
    /// and the second must not — this worker's process is not the one still running — so the plane refuses the write
    /// under a superseded fence rather than this caller guessing which case it is in.</para>
    ///
    /// <para>Best-effort and last, exactly like the shadow log's terminalization above: the run has already landed and
    /// been notified, and no failure here may change what it resolved to.</para>
    /// </summary>
    private async Task TerminalizeHarnessExecutionAsync(Guid teamId, Guid runId, long expectedEpoch, CancellationToken cancellationToken)
    {
        if (_nativeRecords is not INativeRecordExecutionPlane executions) return;

        try
        {
            await executions.TerminalizeAsync(teamId, runId, expectedEpoch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Agent run {RunId} harness execution could not be terminalized; the row stays live for a later sweep and the run completed unchanged", runId);
        }
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
    /// it NEVER flips an otherwise-successful run to Failed). The live path passes a null <see cref="SessionCapture.Handle"/>
    /// and re-reads the one recorded at launch; the durable RE-ATTACH path passes its in-scope handle (the config home under
    /// its spool is exactly what re-attach is tailing) so a run that completes after a worker restart stays resumable too.
    /// Also LAST-RESORT model capture: when the live stream named no model (<see cref="AgentModelReader"/> found none) but the
    /// harness reads one from this same transcript (<see cref="IAgentTranscriptModelSource"/> — Codex records its model only
    /// in the rollout), the captured model backfills <see cref="AgentRunResult.Model"/>. Rides the same guards as resume, so
    /// it degrades exactly where resume does (no session id / no durable handle / rollout not on disk / over the size cap).
    /// Internal (not private) so the size cap's SKIP is unit-pinned directly — that limit is a decision, and an untested
    /// decision is indistinguishable from an accident to the next author who deletes it.
    /// </summary>
    internal async Task<AgentRunResult> CaptureSessionTranscriptAsync(SessionCapture capture, AgentRunResult result, CancellationToken cancellationToken)
    {
        if (capture.Harness is not IAgentSessionTranscript resumable) return result;

        if (string.IsNullOrEmpty(result.SessionId)) return result;   // no captured session → nothing to resume (both harness shapes need it)

        try
        {
            var handle = capture.Handle ?? DeserializeHandle((await _runs.GetAsync(capture.RunId, cancellationToken).ConfigureAwait(false)).RunnerHandleJson);

            if (handle is null) return result;   // a non-durable runner has no on-disk config home to read

            var configHome = LocalProcessRunner.ConfigHomePath(handle.SpoolDirectory);

            // Locate the transcript WITHIN the config home — a computable path (Claude) or a glob (Codex, whose rollout
            // name carries a timestamp unknown ahead of time). Null → this run can't address one → cold-start on continue.
            if (resumable.SessionTranscriptRelativePath(configHome, capture.Task.WorkspaceDirectory, result.SessionId) is not { } relativePath) return result;

            if (ResolveSessionTranscriptPath(configHome, relativePath) is not { } path)
            {
                _logger.LogWarning("Agent run {RunId}: the session-transcript path escaped the config home (hostile session id?); skipping capture", capture.RunId);
                return result;
            }

            if (!File.Exists(path)) return result;   // the CLI wrote its session elsewhere (cwd mismatch) or not at all — cold-start on continue

            var length = new FileInfo(path).Length;
            var cap = MaxSessionTranscriptBytes();

            if (length > cap)   // a pathological session file — skip rather than read it whole into memory (cold-start >> OOM)
            {
                _logger.LogWarning("Agent run {RunId}: session transcript is {Bytes} bytes (> {Cap} cap); skipping capture — a continue will cold-start", capture.RunId, length, cap);
                return result;
            }

            var transcript = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

            return BackfillTranscriptModel(result, capture.Harness) with { SessionTranscript = transcript };

            AgentRunResult BackfillTranscriptModel(AgentRunResult captured, IAgentHarness h) =>
                string.IsNullOrEmpty(captured.Model) && h is IAgentTranscriptModelSource source && source.TryReadModelFromTranscript(transcript) is { Length: > 0 } model
                    ? captured with { Model = model }
                    : captured;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not capture the session transcript for resume; a continue will cold-start", capture.RunId);
            return result;
        }
    }

    /// <summary>The coordinates one session capture needs, as a record rather than a parameter list: which run owns it, the task whose workspace directory keys the harness's session path, the harness that locates the file, and the durable handle whose spool holds the config home (null on the live path, which re-reads the handle recorded at launch).</summary>
    internal sealed record SessionCapture(Guid RunId, AgentTask Task, IAgentHarness Harness, SandboxHandle? Handle);

    /// <summary>
    /// P3 (3.2c): resolve a REFERENCED restored transcript (the producer stamped <c>RestoredTranscriptArtifactId</c> to
    /// keep the bytes out of task_jsonb) to its bytes on <see cref="AgentTask.RestoredTranscript"/>, clearing the ref, so
    /// the harness's <c>BuildConfigHomeFiles</c> stays a pure bytes consumer. A task with no ref (inline bytes, or no
    /// resume at all) is returned unchanged. A referenced transcript is execution-required state: unavailable,
    /// corrupt, or inaccessible bytes fail closed before launch rather than silently cold-starting a named session.
    /// </summary>
    private async Task<AgentTask> ResolveRestoredTranscriptAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken)
    {
        if (task.RestoredTranscriptArtifactId is not { } artifactId) return task;

        var transcript = await _offloader.ResolveRequiredAsync(teamId, task.RestoredTranscript, artifactId, cancellationToken).ConfigureAwait(false);

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

    private async Task<AgentRunResult> EnrichWithWorkspaceChangesAsync(Guid runId, Guid teamId, AgentTask task, AgentRunResult result, IWorkspaceHandle? workspace, CancellationToken cancellationToken)
    {
        // A scratch (repo-less) workspace has no git to diff — its residue is the typed artifact capture, not a patch.
        if (workspace is null || workspace.Repositories.Count == 0) return result;

        try
        {
            // The PRIMARY repo's diff is git ground truth for the top-level fields — byte-identical to a single-repo run.
            var changes = await workspace.CaptureChangesAsync(cancellationToken).ConfigureAwait(false);

            result = result with { ChangedFiles = changes.ChangedFiles, FileStats = changes.FileStats, Patch = TruncatePatch(changes.Patch, MaxPatchChars), BaseSha = changes.BaseSha };

            // Publish-manifest (I1): offload the FULL, untruncated patch to the artifact store — a SEPARATE
            // best-effort step (its own try/catch below) so an artifact-store hiccup can NEVER discard the git
            // ground truth just assigned above. The existing 1MB inline cap stays byte-identical for every other
            // consumer; this only ADDS a durable reference a large diff wouldn't otherwise have.
            var offload = await TryOffloadPatchAsync(runId, teamId, changes.Patch, "patch:primary", cancellationToken).ConfigureAwait(false);
            result = result with { PatchArtifactId = offload.ArtifactId, PatchLossReason = offload.LossReason };

            // Multi-repo: ALSO surface every writable repo's outcome as a Change Set. A single-repo workspace skips
            // this branch entirely, so its result is unchanged (RepositoryResults empty, ChangeSetId null).
            if (workspace.Repositories.Count > 1)
                result = await CaptureRepositoryResultsAsync(runId, teamId, task, result, workspace, changes, cancellationToken).ConfigureAwait(false);

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

    /// <summary>Offload the FULL, untruncated patch to the artifact store for the publish-manifest — independently best-effort: an artifact-store failure here must never discard the git-ground-truth fields a caller already assigned. Returns null on any failure (the manifest then simply carries no PatchArtifactId; the inline, possibly-truncated <see cref="AgentRunResult.Patch"/> is unaffected either way).</summary>
    /// <summary>
    /// Mint the publish-evidence artifact — the same facts the completion composer serializes from the manifest row
    /// (publish state, alias, branch, shas, patch artifact, publish error) — minted HERE because this is where the
    /// push is observed, and carried on the result so a tape-side delivery attestation is auditable without a row
    /// read. Admission caps an unevidenced PASS on a required obligation at InfraUnknown, so without this the tape
    /// could state a push it can never prove. Mirrors <see cref="BuildManifestUpsert"/>'s PublishState derivation;
    /// best-effort like <see cref="TryOffloadPatchAsync"/> — a store hiccup never fails the run, the field stays
    /// null, and a downstream pass-claim honestly caps instead of lying.
    /// </summary>
    private async Task<AgentRunResult> MintPublishEvidenceAsync(Guid runId, Guid teamId, AgentRunResult result, CancellationToken cancellationToken)
    {
        try
        {
            if (result.RepositoryResults.Count > 0)
            {
                var updated = new List<RepositoryRunResult>(result.RepositoryResults.Count);

                foreach (var repo in result.RepositoryResults)
                    updated.Add(HasPublishCapture(repo.ChangedFiles, repo.PatchArtifactId, repo.ProducedBranch)
                        ? repo with { PublishEvidenceId = await PutPublishEvidenceAsync(teamId, repo.Alias, repo.ProducedBranch, repo.PushedCommitSha, repo.BaseSha, repo.PatchArtifactId, repo.PublishError, cancellationToken).ConfigureAwait(false) }
                        : repo);

                return result with { RepositoryResults = updated };
            }

            if (!HasPublishCapture(result.ChangedFiles, result.PatchArtifactId, result.ProducedBranch)) return result;

            return result with { PublishEvidenceId = await PutPublishEvidenceAsync(teamId, "primary", result.ProducedBranch, result.PushedCommitSha, result.BaseSha, result.PatchArtifactId, result.PublishError, cancellationToken).ConfigureAwait(false) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: failed to mint publish evidence; delivery attestations from this result honestly carry none", runId);
            return result;
        }
    }

    /// <summary>Whether this outcome has anything a manifest row would record — the same three-way gate <see cref="PersistPublishManifestAsync"/> applies (the tape-side reader mirrors it, so evidence exists exactly where a manifest will).</summary>
    private static bool HasPublishCapture(IReadOnlyList<string> changedFiles, Guid? patchArtifactId, string? producedBranch) =>
        changedFiles.Count > 0 || patchArtifactId is not null || producedBranch is { Length: > 0 };

    private async Task<Guid> PutPublishEvidenceAsync(Guid teamId, string alias, string? branch, string? commitSha, string? baseSha, Guid? patchArtifactId, string? publishError, CancellationToken cancellationToken)
    {
        var evidence = JsonSerializer.Serialize(new
        {
            publishState = branch is { Length: > 0 } ? nameof(PublishState.Pushed) : nameof(PublishState.PatchOnly),
            repositoryAlias = alias,
            branch,
            commitSha,
            baseSha,
            patchArtifactId,
            publishError,
        }, AgentJson.Options);

        return await _artifacts.PutAsync(teamId, System.Text.Encoding.UTF8.GetBytes(evidence), "application/json", cancellationToken).ConfigureAwait(false);
    }

    private async Task<(Guid? ArtifactId, string? LossReason)> TryOffloadPatchAsync(Guid runId, Guid teamId, string patch, string subject, CancellationToken cancellationToken)
    {
        try
        {
            return ((await _offloader.OffloadIfLargeAsync(teamId, patch, "text/x-diff", cancellationToken).ConfigureAwait(false)).ArtifactId, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliverable-loss honesty: the refusal is NAMED — on the result/manifest (so the preview can say why a
            // listed file has no bytes) AND as a capture gap (so completeness never reports data it does not have).
            var reason = $"the patch's bytes were not stored — {ex.GetType().Name}: {Truncate(ex.Message, 300)}";
            _logger.LogWarning(ex, "Agent run {RunId}: failed to offload the full patch to the artifact store; the manifest will carry no PatchArtifactId and names the loss", runId);
            await NoticeDeliverableLossAsync(runId, teamId, subject, reason).ConfigureAwait(false);
            return (null, reason);
        }
    }

    /// <summary>The blessed capture-gap producer shape (mirrors <c>WorkflowEngine.NoticeOutputsNotOffloadedAsync</c>): bad news lands on its own transaction and its own failure is loud — a loss that also loses its record is the one unacceptable silence.</summary>
    private async Task NoticeDeliverableLossAsync(Guid runId, Guid teamId, string subjectId, string detail)
    {
        if (_completeness is null) return;   // optional capture plane (test construction) — production DI always supplies it

        try
        {
            await _completeness.NoticeAsync(new Persistence.Entities.WorkflowRunCaptureGap
            {
                Id = Guid.NewGuid(), TeamId = teamId, AgentRunId = runId,
                SubjectKind = Messages.Contracts.WorkflowRunDataOwnerKinds.Deliverable, SubjectId = subjectId,
                RangeKind = Persistence.Entities.CaptureGapRangeKind.Unbounded, Reason = Persistence.Entities.CaptureGapReason.WriteRefused,
                ReasonDetail = detail, CaptureSource = "in-process",
                NoticedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Agent run {RunId}: a deliverable loss occurred AND its capture-gap record could not be written — this run may render files whose bytes were never stored ({Subject})", runId, subjectId);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// The RE-ATTACH counterpart of <see cref="EnrichWithWorkspaceChangesAsync"/>: no live <see cref="IWorkspaceHandle"/>
    /// survives a worker restart, but the primary repo's clone directory + base SHA were stamped onto
    /// <paramref name="handle"/> at launch — this resolves the SAME provider by <see cref="SandboxHandle.Kind"/> and
    /// captures via <see cref="IWorkspacePathCapture"/> when it's supported and both fields are present (an older
    /// handle written before this capability existed has neither — a no-op, exactly today's behavior). Best-effort,
    /// same posture as the live path: any capture failure (including the directory already having been reclaimed by
    /// the janitor) is logged and the result kept unchanged.
    /// </summary>
    private async Task<AgentRunResult> EnrichWithReattachWorkspaceChangesAsync(Guid runId, Guid teamId, SandboxHandle handle, AgentRunResult result, CancellationToken cancellationToken)
    {
        if (handle.WorkspaceDirectory is not { Length: > 0 } directory || handle.WorkspaceBaseSha is not { Length: > 0 } baseSha) return result;
        if (_workspaces.Resolve(handle.Kind) is not IWorkspacePathCapture capture) return result;

        try
        {
            var changes = await capture.CaptureChangesFromPathAsync(directory, baseSha, cancellationToken).ConfigureAwait(false);

            result = result with { ChangedFiles = changes.ChangedFiles, FileStats = changes.FileStats, Patch = TruncatePatch(changes.Patch, MaxPatchChars), BaseSha = changes.BaseSha };

            // Separate best-effort step (see TryOffloadPatchAsync) — an artifact-store hiccup must never discard the
            // git capture just assigned above.
            var reattachOffload = await TryOffloadPatchAsync(runId, teamId, changes.Patch, "patch:primary", cancellationToken).ConfigureAwait(false);
            return result with { PatchArtifactId = reattachOffload.ArtifactId, PatchLossReason = reattachOffload.LossReason };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: failed to capture workspace changes on re-attach (the clone may already be reclaimed); keeping the harness-reported file list", runId);
            return result;
        }
    }

    /// <summary>
    /// Multi-repo: capture EVERY writable repo's diff into <see cref="AgentRunResult.RepositoryResults"/> + stamp the
    /// run's <see cref="AgentRunResult.ChangeSetId"/>. The primary's already-captured changes are reused (no second git
    /// call), so the top-level fields and the primary's per-repo entry agree; each entry carries its <see cref="RepositoryRunResult.RepositoryId"/>
    /// resolved from the run's authoring spec. The push step fills in each entry's produced branch.
    ///
    /// <para>Per-repo ISOLATED + best-effort: a SECONDARY repo's capture failure does not abort the agent run, but the
    /// repo remains in the set with a stable <see cref="RepositoryRunResult.CaptureError"/>. This preserves the complete
    /// writable-repo identity set and makes downstream integration fail closed instead of silently shipping siblings.
    /// The primary's capture already succeeded (it's the top-level diff).</para>
    /// </summary>
    private async Task<AgentRunResult> CaptureRepositoryResultsAsync(Guid runId, Guid teamId, AgentTask task, AgentRunResult result, IWorkspaceHandle workspace, WorkspaceChanges primaryChanges, CancellationToken cancellationToken)
    {
        var repoIds = task.Workspace?.Repositories.ToDictionary(r => r.Alias, r => r.RepositoryId);
        var perRepo = new List<RepositoryRunResult>();

        foreach (var repo in workspace.Repositories.Where(r => r.Access == WorkspaceAccess.Write))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changes = await CaptureOneRepoOrNullAsync(runId, repo, workspace, primaryChanges, cancellationToken).ConfigureAwait(false);

            if (changes is null)
            {
                perRepo.Add(new RepositoryRunResult
                {
                    Alias = repo.Alias,
                    RepositoryId = repoIds is not null && repoIds.TryGetValue(repo.Alias, out var failedId) ? failedId : null,
                    BaseBranch = repo.BaseBranch,
                    Access = WorkspaceAccess.Write,
                    CaptureError = RepositoryCaptureUnavailableCode,
                });
                continue;
            }

            // Publish-manifest (I1): offload THIS repo's full, untruncated patch — TryOffloadPatchAsync is its OWN
            // best-effort step (never throws), so a per-repo artifact failure can only leave THAT repo's
            // PatchArtifactId null — it can never abort this loop or discard a sibling repo's already-captured diff.
            var patchArtifactId = (await TryOffloadPatchAsync(runId, teamId, changes.Patch, $"patch:{repo.Alias}", cancellationToken).ConfigureAwait(false)).ArtifactId;

            perRepo.Add(new RepositoryRunResult
            {
                Alias = repo.Alias,
                RepositoryId = repoIds is not null && repoIds.TryGetValue(repo.Alias, out var id) ? id : null,
                ChangedFiles = changes.ChangedFiles,
                FileStats = changes.FileStats,
                // Capture this repo's diff (capped inline like the top-level patch) — the durable, base-anchored input
                // the supervisor's per-repo on-disk integration consumes; a large one is offloaded at completion.
                Patch = TruncatePatch(changes.Patch, MaxPatchChars),
                PatchArtifactId = patchArtifactId,
                BaseSha = changes.BaseSha,
                BaseBranch = repo.BaseBranch,
                Access = WorkspaceAccess.Write,
            });
        }

        return result with { RepositoryResults = perRepo, ChangeSetId = ChangeSetIdFor(runId) };
    }

    /// <summary>Capture one writable repo's changes (the primary's are already in hand, so reuse them). Returns null when a SECONDARY repo's capture fails — logged and durably warned, isolated, never aborting the agent run. Cancellation still propagates.</summary>
    private async Task<WorkspaceChanges?> CaptureOneRepoOrNullAsync(Guid runId, WorkspaceRepositoryHandle repo, IWorkspaceHandle workspace, WorkspaceChanges primaryChanges, CancellationToken cancellationToken)
    {
        if (repo.Alias == workspace.PrimaryAlias) return primaryChanges;

        try
        {
            return await workspace.CaptureChangesAsync(repo.Alias, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: failed to capture changes for repo '{Alias}'; recording an unavailable repo fact and keeping the others", runId, repo.Alias);
            await AppendRepositoryCaptureFailureWarningAsync(runId, repo.Alias, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Persist a redacted timeline fact for a secondary-repository capture gap. Best-effort and shadow: observability failure never changes the harness result.</summary>
    private async Task AppendRepositoryCaptureFailureWarningAsync(Guid runId, string alias, CancellationToken cancellationToken)
    {
        try
        {
            await _runs.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.Warning, Text = $"Could not capture git changes for repository '{alias}'; the change set is incomplete and cannot be published as clean." }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not record the repository capture warning for '{Alias}'", runId, alias);
        }
    }

    /// <summary>The stable id for the SET of branches a multi-repo run produces — run-id-derived, so a re-push of the SAME run reuses it (idempotent) and its non-null-ness distinguishes a multi-repo run from a single-repo one. A workflow RETRY of agent.run is a new run id → a new change set (like the produced branch names). Internal + static so it's unit-pinned.</summary>
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
    /// When enabled, push a run's non-empty diff to a deterministically-named remote branch and fold the pushed
    /// name into the result so the agent.run node's <c>branch</c> output carries it — the handoff a downstream
    /// git.open_pr needs (that node requires the branch to pre-exist on the remote). A SIDE-EFFECTING write to
    /// the user's remote, so it is DEFAULT-ON but guarded: the publish guard chain (<see cref="IPublishGuard"/>)
    /// must clear, the run must have a non-empty diff, and the handle must be push-capable; a guard hit stamps
    /// <see cref="AgentRunResult.PublishSkipReason"/> and returns without pushing — never a silent no-op.
    ///
    /// <para>P2.2 (salvage): a FORCED-terminal run (<see cref="AgentRunStatus.TimedOut"/>, or the C3-stalled
    /// <see cref="AgentRunStatus.NeedsReview"/>) still qualifies — <see cref="EnrichWithWorkspaceChangesAsync"/>
    /// already captures whatever the agent had ACTUALLY written to disk before it was killed (git ground truth,
    /// independent of the kill signal), so a genuinely mid-progress kill no longer silently discards that work: it
    /// lands as a real, reviewable branch instead of vanishing with the process. Every OTHER non-Succeeded status
    /// (Failed, Cancelled) is unchanged — a run that failed or was cancelled on its own terms is a different
    /// question from one CodeSpace itself force-terminated mid-flight.</para>
    ///
    /// <para>Idempotence / no-replay: re-read the run's epoch and skip if it no longer matches the one this
    /// executor claimed — the run was reclaimed, so this side effect would be wasted (the completion CAS loses
    /// anyway) and we must not fire it. The branch name is run-id-derived AND generation-specific
    /// (see <see cref="BuildBranchName"/>): a workflow RETRY of agent.run is a new run id → a NEW branch
    /// (acceptable v1 branch-litter), a re-push of the SAME attempt is a plain --force overwrite (no divergent
    /// branch), and a RECLAIMED attempt pushes its own generation's ref — the pre-check below narrows the zombie
    /// window, the generation ref closes it at the remote.</para>
    ///
    /// <para>Best-effort like <see cref="EnrichWithWorkspaceChangesAsync"/>: a <see cref="WorkspaceException"/> is
    /// SWALLOWED (a push hiccup — e.g. a read-only credential 403 — never flips the run's own status) but is
    /// surfaced as a Warning event on the timeline (token already redacted in the message) so the operator sees
    /// WHY no branch appeared. Cancellation still propagates (worker torn down).</para>
    /// </summary>
    internal async Task<AgentRunResult> PushProducedBranchIfEnabledAsync(Guid runId, AgentTask task, AgentRunResult result, IWorkspaceHandle? workspace, long claimedEpoch, CancellationToken cancellationToken)
    {
        if (result.Status is not (AgentRunStatus.Succeeded or AgentRunStatus.TimedOut or AgentRunStatus.NeedsReview)) return result;
        if (workspace is not IWorkspacePushHandle pushHandle) return result;

        var multiRepo = workspace.Repositories.Count > 1;

        // Single-repo: skip the push when nothing changed (byte-identical gate). Multi-repo skips this global gate —
        // a secondary repo may have changes the primary's top-level fields don't reflect; each per-repo push self-gates.
        if (!multiRepo && result.ChangedFiles.Count == 0 && string.IsNullOrEmpty(result.Patch)) return result;

        if (!multiRepo && await EvaluatePublishGuardsAsync(task, task.RepositoryId, cancellationToken).ConfigureAwait(false) is { } verdict)
            return result with { PublishSkipReason = verdict.Reason };

        // No-replay: a reclaimed run (epoch bumped) would lose the completion CAS anyway — don't fire the side
        // effect. Read FRESH + untracked (GetAsync is AsNoTracking) so we see the reclaimer's bumped epoch.
        var current = await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);

        if (!AgentRunFence.StillOwns(current.FenceEpoch, claimedEpoch))
        {
            _logger.LogWarning("Agent run {RunId}: {Note}", runId, AgentRunFence.RefusalNote("branch push", current.FenceEpoch, claimedEpoch));
            return result;
        }

        try
        {
            if (multiRepo) return await PushRepositoryResultsAsync(runId, task, result, workspace, pushHandle, claimedEpoch, cancellationToken).ConfigureAwait(false);

            var branch = await PushWithRetryAsync(ct => pushHandle.PushChangesAsync(BuildBranchName(runId, claimedEpoch), ct), cancellationToken).ConfigureAwait(false);

            return branch is null ? result : result with { ProducedBranch = branch, PushedCommitSha = pushHandle.LastPushedCommitSha() };
        }
        catch (WorkspaceException ex)
        {
            // Best-effort: a push failure must never flip a Succeeded run to Failed. The exception message has the
            // token already redacted (the handle redacts it), so it's safe to persist onto the timeline.
            _logger.LogWarning(ex, "Agent run {RunId}: failed to push the produced branch after {Attempts} attempt(s); the run stays Succeeded with no branch output", runId, PushMaxAttempts);
            await AppendPushFailureWarningAsync(runId, ex.Message, cancellationToken).ConfigureAwait(false);
            return result with { PublishError = ex.Message };
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
    private async Task<AgentRunResult> PushRepositoryResultsAsync(Guid runId, AgentTask task, AgentRunResult result, IWorkspaceHandle workspace, IWorkspacePushHandle pushHandle, long claimedEpoch, CancellationToken cancellationToken)
    {
        var branchName = BuildBranchName(runId, claimedEpoch);
        var updated = new List<RepositoryRunResult>(result.RepositoryResults.Count);
        string? primaryBranch = null;

        foreach (var repo in result.RepositoryResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Per-repo guard eval: a multi-repo run can mix policies (e.g. one repo PatchOnly, a sibling Branch) —
            // each repo's own row decides its own fate, isolated exactly like a per-repo push failure already is.
            if (await EvaluatePublishGuardsAsync(task, repo.RepositoryId, cancellationToken).ConfigureAwait(false) is { } verdict)
            {
                updated.Add(repo with { PublishSkipReason = verdict.Reason });
                continue;
            }

            var (pushed, error) = await PushOneRepoOrNullAsync(runId, repo.Alias, branchName, pushHandle, cancellationToken).ConfigureAwait(false);

            updated.Add(repo with { ProducedBranch = pushed, PublishError = error, PushedCommitSha = pushed is null ? null : pushHandle.LastPushedCommitSha(repo.Alias) });

            if (repo.Alias == workspace.PrimaryAlias) primaryBranch = pushed;
        }

        return result with { RepositoryResults = updated, ProducedBranch = primaryBranch };
    }

    /// <summary>Push one repo by alias, ISOLATING its failure: a <see cref="WorkspaceException"/> — after <see cref="PushMaxAttempts"/> retries — is logged + surfaced as a per-repo Warning on the timeline (token already redacted) and returned as the error half of the tuple, so a sibling repo's already-pushed branch is never discarded. Cancellation propagates.</summary>
    private async Task<(string? Branch, string? Error)> PushOneRepoOrNullAsync(Guid runId, string alias, string branchName, IWorkspacePushHandle pushHandle, CancellationToken cancellationToken)
    {
        try
        {
            return (await PushWithRetryAsync(ct => pushHandle.PushChangesAsync(alias, branchName, ct), cancellationToken).ConfigureAwait(false), null);
        }
        catch (WorkspaceException ex)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: failed to push repo '{Alias}' after {Attempts} attempt(s); keeping the other repos' branches in the change set", runId, alias, PushMaxAttempts);
            await AppendPushFailureWarningAsync(runId, $"[{alias}] {ex.Message}", cancellationToken).ConfigureAwait(false);
            return (null, ex.Message);
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
        result = await CaptureSessionTranscriptAsync(new SessionCapture(runId, task, harness, Handle: null), result, cancellationToken).ConfigureAwait(false);

        result = await EnrichWithWorkspaceChangesAsync(runId, run.TeamId, task, result, workspace, cancellationToken).ConfigureAwait(false);

        result = await CaptureDeclaredArtifactsAsync(runId, run, task, result, workspace, claimedEpoch, cancellationToken).ConfigureAwait(false);

        result = await PushProducedBranchIfEnabledAsync(runId, task, result, workspace, claimedEpoch, cancellationToken).ConfigureAwait(false);

        result = await MintPublishEvidenceAsync(runId, run.TeamId, result, cancellationToken).ConfigureAwait(false);

        var claimedOutcome = result;

        result = await GradeAcceptanceIfPresentAsync(run, task, result, workspace, cancellationToken).ConfigureAwait(false);

        result = await PublishFoldedUnderClaimAsync(runId, run, task, claimedOutcome, result, workspace, claimedEpoch, cancellationToken).ConfigureAwait(false);

        // Publish-or-park (I1/I2): record what this pass produced + published REGARDLESS of Status — a Failed or
        // TimedOut run's captured diff gets a row exactly like a Succeeded one. Idempotent (upserts), so an S6 revise
        // round's re-verification safely overwrites this same row with its own latest state.
        await PersistPublishManifestAsync(runId, run, task, result, claimedEpoch, cancellationToken).ConfigureAwait(false);

        return await ReviewOutputIfEnabledAsync(task, result, run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// D4b: PUBLISH the work of a run whose grade just overturned its own self-reported failure. The push step runs
    /// BEFORE the grade (it feeds the branch lane) and <see cref="PushProducedBranchIfEnabledAsync"/> deliberately
    /// skips a <see cref="AgentRunStatus.Failed"/> run — so without this a folded under-claim landed Succeeded with
    /// an acceptance PASS and nothing published: the manifest read <c>PublishState.PatchOnly</c> while its
    /// <c>AcceptanceState</c> read Passed (the accepted-but-unpublished state publish-or-park exists to prevent),
    /// and the node bound no <c>branch</c> output for a downstream PR-open to consume. The run now qualifies, so it
    /// gets the SAME push + evidence mint every Succeeded run gets, BEFORE the manifest upsert reads the result —
    /// so one manifest row states the pushed truth rather than a stale patch-only claim.
    ///
    /// <para>Keyed on the STATUS transition, not on <see cref="AgentRunResult.Contradiction"/>: the fold is the only
    /// thing that turns a Failed self-report into a Succeeded run, and reading the transition keeps this independent
    /// of how that fact is labelled. Every other outcome returns the graded result untouched (byte-identical), and
    /// the push step keeps its own guards — the publish opt-in, the empty-diff gate, the fence epoch, the publish
    /// guard chain — so this buys no bypass, only the round the fold earned.</para>
    /// </summary>
    private async Task<AgentRunResult> PublishFoldedUnderClaimAsync(Guid runId, AgentRun run, AgentTask task, AgentRunResult claimed, AgentRunResult graded, IWorkspaceHandle? workspace, long claimedEpoch, CancellationToken cancellationToken)
    {
        if (claimed.Status != AgentRunStatus.Failed || graded.Status != AgentRunStatus.Succeeded) return graded;

        _logger.LogInformation("Agent run {RunId}: the acceptance check overturned a self-reported failure — publishing the work the run had withheld", runId);

        var pushed = await PushProducedBranchIfEnabledAsync(runId, task, graded, workspace, claimedEpoch, cancellationToken).ConfigureAwait(false);

        return await MintPublishEvidenceAsync(runId, run.TeamId, pushed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Upsert the publish-manifest row(s) for this run — ONE per repository, single-repo top-level fields when
    /// <see cref="AgentRunResult.RepositoryResults"/> is empty, one row per entry otherwise. Skips entirely when there
    /// is nothing to record (no workspace, empty diff) — an empty-diff run leaves no manifest row (nothing to
    /// publish or park). Best-effort: a manifest-write failure is logged and never flips the run's outcome, mirroring
    /// every other capture/push step's posture — the captured diff still lives on the result row either way.
    /// A multi-repo run's contract binds the WHOLE change (<see cref="GradeMultiRepoAcceptanceAsync"/> grades every
    /// repo and short-circuits on the first failure into ONE aggregate verdict) — every repo's row carries that SAME
    /// <see cref="AgentRunResult.AcceptancePassed"/>, never a hardcoded null, so the north-star scorecard's
    /// per-manifest <c>AcceptanceState</c> read sees a multi-repo grade exactly like a single-repo one.
    /// Internal (not private) so this wiring is unit-pinned directly (InternalsVisibleTo), not only through a full
    /// executor run.
    /// </summary>
    /// <summary>
    /// Commit this pass's delivery-ledger row, FENCED on the epoch this attempt claimed. The branch push a few
    /// steps earlier already refuses to fire for a reclaimed attempt; this row — the durable claim that the push
    /// happened and the acceptance passed — did not, so a zombie worker could stamp Pushed/Passed and only then
    /// lose the completion CAS. The reversible remote effect was guarded and the irreversible ledger claim was not.
    /// </summary>
    internal async Task PersistPublishManifestAsync(Guid runId, AgentRun run, AgentTask task, AgentRunResult result, long claimedEpoch, CancellationToken cancellationToken)
    {
        try
        {
            if (result.RepositoryResults.Count > 0)
            {
                foreach (var repo in result.RepositoryResults)
                    await _manifests.UpsertForAgentRunAsync(runId, BuildManifestUpsert(run, repo.Alias, repo.RepositoryId, repo.BaseSha, repo.PatchArtifactId, repo.ChangedFiles, repo.ProducedBranch, repo.PublishError, repo.PublishSkipReason, result.AcceptancePassed, repo.PushedCommitSha), claimedEpoch, cancellationToken).ConfigureAwait(false);

                return;
            }

            if (result.ChangedFiles.Count == 0 && string.IsNullOrEmpty(result.Patch) && result.PatchArtifactId is null)
            {
                // Nothing changed — no artifact to record. Named rather than silent: this is the exact spot whose
                // silence made "1 producer run(s) succeeded but none recorded a publish manifest" undiagnosable in
                // run 31170757534 — dependency staging then legitimately falls through to the default branch, and
                // WITHOUT this line the producer side of that story never appears in any log.
                _logger.LogInformation("Agent run {RunId}: no publish manifest recorded — nothing captured (changedFiles=0, patch=none, patchArtifact=none, producedBranch={Branch}); a dependent staged on this unit inherits the repository default branch", runId, string.IsNullOrEmpty(result.ProducedBranch) ? "(none)" : result.ProducedBranch);
                return;
            }

            await _manifests.UpsertForAgentRunAsync(runId, BuildManifestUpsert(run, "primary", task.RepositoryId, result.BaseSha, result.PatchArtifactId, result.ChangedFiles, result.ProducedBranch, result.PublishError, result.PublishSkipReason, result.AcceptancePassed, result.PushedCommitSha, result.PatchLossReason), claimedEpoch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: failed to record the publish manifest; the captured diff is still on the result row", runId);
        }
    }

    /// <summary>
    /// DC-4: capture the DECLARED deliverable files (a non-TestsPass acceptance's path list) as TYPED artifact-manifest
    /// rows — inside the already-open capture-intent window, best-effort like every sibling capture step (a store
    /// hiccup must never flip an otherwise-successful run). What was TAKEN rides the result, so the promise's facts
    /// stop reading a typed capture as "empty"; what was OWED is never read from here — the facts derive it from the
    /// acceptance, the same place the promise did. A skipped deliverable is an accounting fact — it never re-grades
    /// the run.
    /// </summary>
    private async Task<AgentRunResult> CaptureDeclaredArtifactsAsync(Guid runId, AgentRun run, AgentTask task, AgentRunResult result, IWorkspaceHandle? workspace, long claimedEpoch, CancellationToken cancellationToken)
    {
        if (workspace is null) return result;

        var captured = 0;

        try
        {
            captured = await _artifactManifests.CaptureDeclaredAsync(task, workspace.Directory, runId, run.WorkflowRunId, run.TeamId, claimedEpoch, cancellationToken).ConfigureAwait(false);

            // C2: only a SCRATCH world (a repo-less run) gets the undeclared walk. A git-backed workspace's
            // undeclared files are already captured — as the diff, with their history — so walking one would mint a
            // second, weaker copy of what the patch already holds.
            var walk = workspace.Repositories.Count == 0
                ? await _artifactManifests.CaptureUndeclaredAsync(task, workspace.Directory, runId, run.WorkflowRunId, run.TeamId, claimedEpoch, cancellationToken).ConfigureAwait(false)
                : UndeclaredCaptureOutcome.None;

            return result with { CapturedArtifactCount = captured, UndeclaredArtifactCount = walk.Captured, UncapturedScratchFileCount = walk.Refused };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: failed to capture deliverable artifacts; the acceptance oracle still grades them on the produced branch", runId);
            await NoticeDeliverableLossAsync(runId, run.TeamId, "declared-deliverables", $"declared deliverables were not captured — {ex.GetType().Name}: {Truncate(ex.Message, 300)}").ConfigureAwait(false);

            // The DECLARED pass may have completed before the walk threw — discarding its count here would report a
            // capture that really happened as zero. And the fault itself must be NAMED on the result: the repo-less
            // grade lane rebuilds its world from these rows, so a storage fault that captured nothing is otherwise
            // indistinguishable from an agent that produced nothing — which classifies GENUINE and buys retries no
            // retry can fix. The marker is what turns that verdict infra-classed downstream.
            return result with { CapturedArtifactCount = captured, DeliverableCaptureFault = $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}" };
        }
    }

    /// <summary>
    /// The capture promise's INTENT-time statement (P2 saga) — the deliverable paths THIS attempt undertook to
    /// capture, written with the promise and before any capture side effect, so a shortfall has something to be short
    /// OF. Null when the acceptance declares no paths, which is the honest "nothing was owed" rather than a silence a
    /// total loss could hide behind. Pure + internal so it is unit-pinned without a database.
    /// </summary>
    internal static string? CaptureExpectationsOf(AgentTask task)
    {
        var declared = ArtifactManifestStore.DeclaredDeliverablePaths(task);

        return declared.Count == 0 ? null : JsonSerializer.Serialize(new { deliverables = declared }, AgentJson.Options);
    }

    /// <summary>
    /// The capture promise's commit-time observation (P2 saga) — the compact JSON facts of what the capture sequence
    /// actually persisted, INCLUDING the explicit empty (a confirmed fact, never an absence). A run that owed
    /// deliverables and took none is NOT that empty: it is a shortfall against <c>declaredDeliverables</c>.
    /// <para><c>declaredDeliverables</c> is read off THE SAME acceptance <see cref="CaptureExpectationsOf"/> read, not
    /// off the capture pass — an attempt whose capture never ran at all (the re-attach: the live workspace died with
    /// the worker) would otherwise commit "none were owed" beside a promise naming three files, and a plane whose
    /// whole value is that its two numbers can be compared cannot ship two numbers that disagree by construction.</para>
    /// Pure + internal so it is unit-pinned without a database.
    /// </summary>
    internal static string CaptureFactsOf(AgentRunResult result, AgentTask task)
    {
        var declared = ArtifactManifestStore.DeclaredDeliverablePaths(task).Count;

        return JsonSerializer.Serialize(new
        {
            changedFiles = result.ChangedFiles.Count,
            patchInline = !string.IsNullOrEmpty(result.Patch),
            patchArtifactId = result.PatchArtifactId,
            branch = result.ProducedBranch,
            pushedCommitSha = result.PushedCommitSha,
            repos = result.RepositoryResults.Count,
            declaredDeliverables = declared,
            typedArtifacts = result.CapturedArtifactCount,
            // C2: the scratch walk's PAIR — what it took, and what it left. The second number is the shortfall the
            // walk's own file/byte ceiling can produce, which nothing else in these facts could reveal.
            undeclaredArtifacts = result.UndeclaredArtifactCount,
            uncapturedScratchFiles = result.UncapturedScratchFileCount,
            empty = result.ChangedFiles.Count == 0 && string.IsNullOrEmpty(result.Patch) && result.PatchArtifactId is null && result.RepositoryResults.Count == 0 && result.CapturedArtifactCount == 0 && result.UndeclaredArtifactCount == 0 && declared == 0,
        }, AgentJson.Options);
    }

    /// <summary>Pure mapping from a run's produced-artifact facts to the manifest upsert shape — the PublishState/AcceptanceState derivation this pins: <see cref="PublishState.Pushed"/> is a CONFIRMED claim (review hole 2) — it requires BOTH the produced branch AND the readback-confirmed remote tip (P3b-2's <paramref name="pushedCommitSha"/>); a branch whose readback failed or mismatched maps PatchOnly with a named PublishError, because a push command that RAN proves intent, not arrival — and Pushed flows straight into Delivered/Delivery-Passed. Everything else unchanged: no branch means PatchOnly (PublishError distinguishes an intentional FAILED attempt from a BY-CHOICE guard skip, whose reason lands on Summary); acceptance mirrors the grader's tri-state verbatim. Internal so it's unit-pinned without a database.</summary>
    internal static PublishManifestUpsert BuildManifestUpsert(AgentRun run, string alias, Guid? repositoryId, string? baseSha, Guid? patchArtifactId, IReadOnlyList<string> changedFiles, string? producedBranch, string? publishError, string? publishSkipReason, bool? acceptancePassed, string? pushedCommitSha = null, string? patchLossReason = null) => new()
    {
        CommitSha = pushedCommitSha,
        TeamId = run.TeamId,
        WorkflowRunId = run.WorkflowRunId,
        RepositoryAlias = alias,
        RepositoryId = repositoryId,
        BaseSha = baseSha,
        PatchArtifactId = patchArtifactId,
        PatchLossReason = patchLossReason,
        ChangedFileCount = changedFiles.Count,
        ChangedFilesJson = changedFiles.Count > 0 ? JsonSerializer.Serialize(changedFiles, AgentJson.Options) : null,
        AcceptanceState = acceptancePassed switch { true => PublishAcceptanceState.Passed, false => PublishAcceptanceState.Failed, null => PublishAcceptanceState.NotApplicable },
        PublishStateValue = producedBranch is { Length: > 0 } && pushedCommitSha is { Length: > 0 } ? PublishState.Pushed : PublishState.PatchOnly,
        PublishError = producedBranch is { Length: > 0 } && pushedCommitSha is not { Length: > 0 }
            ? publishError ?? "push-unconfirmed: the remote readback did not confirm the pushed tip — the branch may exist, but Delivered must not be claimed on intent alone"
            : publishError,
        Branch = producedBranch,
        Summary = publishSkipReason,
    };

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
        !AgentAcceptanceContract.IsInfraFailure(detail, WorkPresent(result));

    /// <summary>
    /// The ONE "this run produced WORK" read this executor shares — the infra classification above (which uses it to
    /// tell a publish failure from "the fix is to do the work") and D4b's gate (<see cref="SelfReportedSuccess"/>,
    /// which uses it to decide whether a self-reported failure has anything to grade), so the two can never drift on
    /// what "work exists" means. Git ground truth off the captured diff, which
    /// <c>EnrichWithWorkspaceChangesAsync</c> records for EVERY terminal status.
    /// <para>Deliberately no <see cref="AgentRunResult.ProducedBranch"/> disjunct, unlike
    /// <c>SupervisorOutcome.ResultShowsWork</c>'s supervisor-side twin: <see cref="PushProducedBranchIfEnabledAsync"/>
    /// never publishes a branch for a <see cref="AgentRunStatus.Failed"/> run, so on the one lane that would gain
    /// from it there is never a branch to read — while adding it would silently reclassify the pre-existing
    /// <c>no-branch-or-repo</c> revise verdict.</para>
    /// </summary>
    internal static bool WorkPresent(AgentRunResult result) =>
        result.ChangedFiles.Count > 0 || !string.IsNullOrEmpty(result.Patch);

    /// <summary>
    /// D4b's OWN work-present read: <see cref="WorkPresent"/> plus any PER-REPO work. A multi-repo run's top-level
    /// fields carry the PRIMARY repo only, so a Failed run whose work lives in a secondary repo reads as
    /// work-less there — and would never be graded, which is precisely the discarded work this gate exists to stop
    /// (<see cref="GradeMultiRepoAcceptanceAsync"/> grades every repo that produced one).
    /// <para>Deliberately a SEPARATE read from the one <see cref="IsAgentFixableOracleFailure"/> shares: widening
    /// that one would silently reclassify the pre-existing <c>no-branch-or-repo</c> revise verdict for a multi-repo
    /// run. This wider read is used only where D4b itself decides — the gate and its fold.</para>
    /// </summary>
    internal static bool AnyWorkPresent(AgentRunResult result) =>
        WorkPresent(result)
        || result.RepositoryResults.Any(repo => repo.ChangedFiles.Count > 0 || !string.IsNullOrEmpty(repo.Patch) || !string.IsNullOrEmpty(repo.ProducedBranch));

    // ─── D3: model escalation ────────────────────────────────────────────────

    /// <summary>
    /// WHY the next attempt should reach for a stronger model, or null when this round proved nothing about the
    /// model. The single-agent lane's projection into the SHARED <see cref="AgentModelEscalationTrigger"/> (the
    /// agent.run node projects its flat resume payload into the same primitives), so "the model was the limit"
    /// cannot come to mean two different things in the two lanes.
    /// </summary>
    internal static string? EscalationReasonFor(AgentRunResult result) =>
        AgentModelEscalationTrigger.Reason(result.Contradiction, result.AcceptancePassed is false, result.AcceptanceDetail, EscalationWorkPresent(result), result.Error);

    /// <summary>Git ground truth that the round produced SOMETHING — changed files, an inline diff, a pushed branch, or any writable repo's own contribution in a multi-repo run.</summary>
    private static bool EscalationWorkPresent(AgentRunResult result) =>
        result.ChangedFiles.Count > 0
        || !string.IsNullOrEmpty(result.Patch)
        || !string.IsNullOrEmpty(result.ProducedBranch)
        || result.RepositoryResults.Any(r => r.ChangedFiles.Count > 0 || !string.IsNullOrEmpty(r.ProducedBranch));

    /// <summary>Apply a resolved escalation to the task it governs: the picked model REPLACES whatever the task carried (a pin is a floor for untested work, not a ceiling once the run's own check has disproved it). A null pick — nothing in the pool beat the floor — leaves the task byte-identical; the fact lives on the result and the timeline, never in a perturbed dispatch.</summary>
    internal static AgentTask ApplyEscalation(AgentTask task, AgentModelEscalation? escalation) =>
        escalation?.To is { Length: > 0 } model ? task with { Model = model } : task;

    /// <summary>The escalation announcement's pinned prefix — the operator-visible marker on the run's timeline (and a stable hook for tests).</summary>
    internal const string ModelEscalationPrefix = "Model escalation";

    /// <summary>The one-line escalation note: the move it made, or — when the team credentialed nothing stronger — the honest no-op. Pure, so the timeline text and the tests can't drift.</summary>
    internal static string DescribeEscalation(AgentModelEscalation escalation) =>
        escalation.To is { Length: > 0 } to
            ? $"{ModelEscalationPrefix}: {escalation.From ?? "(unknown)"} → {to}. {escalation.Reason}"
            : $"{ModelEscalationPrefix}: no model stronger than {escalation.From ?? "the current one"} is credentialed for this team — staying on {escalation.From ?? "it"}. {escalation.Reason}";

    /// <summary>
    /// Resolve an escalation REQUEST into a concrete pick over the team's credentialed pool, reusing the supervisor
    /// lane's pure <see cref="SupervisorRetryEscalation.PickStrongerModel"/> — strictly above the floor's effective
    /// tier, <c>IsDefault</c>-first among the qualifying candidates, Frontier allowed (escalating is exactly the
    /// case that earns the priciest tier).
    ///
    /// <para>The pool is BOUNDED to the CREDENTIAL ROW whose key is already in this sandbox's environment
    /// (<paramref name="credentialId"/>) — never merely to its provider. A team can credential one provider twice (a
    /// direct vendor key and a gateway with its own base URL and model family), so a provider-wide bound could hand
    /// the escalated model a key and endpoint that never served it, silently breaking the resolver's own guarantee
    /// that a model id and its key come from the same row. Bounding to the row keeps the decrypted key, the egress
    /// base URL and the harness↔provider reconciliation valid by construction, so escalation NEVER crosses
    /// providers or credentials and nothing needs re-resolving. Only the operator-global key (no row) falls back to
    /// its provider, and only a run with no credential at all is unbounded — there is then no key to be
    /// inconsistent with.</para>
    ///
    /// <para>Known-unavailable rows are soft-filtered out first, the same anti-strand rule
    /// <see cref="ModelCredentials.ModelPoolSelector"/> applies to every other unpinned auto-pick (<c>Available !=
    /// false</c> keeps never-probed rows preferred; the full set stands when EVERY candidate is known-dead, since a
    /// maybe-dead stronger model still beats no escalation).</para>
    ///
    /// <para>Always returns a record — a null pick is the OUTCOME "nothing stronger exists", not an absence.</para>
    /// </summary>
    private async Task<AgentModelEscalation> ResolveEscalationAsync(string reason, Guid teamId, Guid? credentialId, string? provider, string? priorModel, CancellationToken cancellationToken)
    {
        var rows = await _db.ModelCredentialModel.AsNoTracking()
            .Where(m => m.Enabled && m.Credential.TeamId == teamId && m.Credential.DeletedDate == null && m.Credential.Status == CredentialStatus.Active)
            .Select(m => new { m.ModelId, m.IsDefault, m.CapabilityTier, m.ProbedCapabilityTier, m.Available, m.ModelCredentialId, m.Credential.Provider })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Provider matching in memory + case-insensitively: the pool is a team's handful of rows, and a provider tag
        // stored with different casing under two credentials must not silently shrink the candidate set.
        var bounded = credentialId is { } row ? rows.Where(m => m.ModelCredentialId == row).ToList()
            : provider is { Length: > 0 } ? rows.Where(m => string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase)).ToList()
            : rows;

        var reachable = bounded.Where(m => m.Available != false).ToList();
        var candidates = reachable.Count > 0 ? reachable : bounded;

        var picked = SupervisorRetryEscalation.PickStrongerModel(candidates, m => m.IsDefault, m => m.ProbedCapabilityTier, m => m.CapabilityTier, m => m.ModelId, priorModel)?.ModelId;

        return new AgentModelEscalation { From = priorModel, To = picked, Reason = reason };
    }

    /// <summary>The note a format-fault respawn announces on the timeline. Written as a fact about THIS attempt (never a promise about a next one, and never a count) — it is emitted from the dispatch of every task that already carries the degrade, so it exists exactly when a respawn does, and the supervisor lane can legitimately buy more than one per run. Wording pinned by unit test.</summary>
    internal const string FormatFaultMitigationNote = "Gateway format fault — respawned with thinking disabled (fresh conversation: the mangled block lives in the prior transcript).";

    /// <summary>Announce the format-fault repair on the timeline — the operator sees WHY this attempt starts cold and runs degraded, instead of a silent second agent. Best-effort like the escalation event beside it.</summary>
    private async Task AppendMitigationEventAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            await _runs.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.Warning, Text = FormatFaultMitigationNote }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not record the gateway-format-fault mitigation event", runId);
        }
    }

    /// <summary>Announce the escalation on the timeline — the operator sees the run reached for a stronger model and WHY, or that it wanted to and the team had nothing stronger. Best-effort like the other completion-tail events.</summary>
    private async Task AppendEscalationEventAsync(Guid runId, AgentModelEscalation escalation, CancellationToken cancellationToken)
    {
        try
        {
            await _runs.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.Warning, Text = DescribeEscalation(escalation) }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not record the model-escalation event", runId);
        }
    }

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

    /// <summary>The visible seam between the rounds' faithful raw streams, so the run's transcript holds the WHOLE run — every round — not just the last one. Marked on the spool at each round boundary and emitted lazily, which is what keeps an empty round from contributing one.</summary>
    internal const string ReviseTranscriptSeam = "\n--- revise round ---\n";

    /// <summary>
    /// Where a run's transcript spill file lives: the run's OWN round-0 spool directory — the operator's configured
    /// <c>Agents:RunSpoolDirectory</c> volume, never the system temp directory a Production host is refused
    /// (<see cref="CodeSpace.Core.Settings.DurableRootsGuard"/>) and never a path nothing can reclaim. Round 0
    /// deliberately, not the current revise round: ONE spool carries every round, and the bare round-0 directory is the
    /// first entry of <see cref="AgentRunSpoolReaper.RoundSpoolFamily"/> — so the existing terminal-gated sweep already
    /// reclaims the spill along with the rest of the run's spool, with no new reaper and under the same retention
    /// window. Derived from the run id alone, so the re-attach path resolves the same directory after a restart.
    /// </summary>
    internal static string TranscriptSpillDirectory(Guid runId) => LocalProcessRunner.SpoolDirectoryFor(ReviseSpoolKey(runId, round: 0));

    /// <summary>The transcript artifact's content type. MUST stay equal to the one <c>AgentRunService.OffloadLargeTranscriptAsync</c> uses: both paths mint an artifact for the same bytes, so a drift would file one run's transcript under a different type than the next's.</summary>
    private const string TranscriptContentType = "text/plain";

    /// <summary>
    /// The transcript's terminal attachment point (G0). A spool still inside its budget becomes the inline string —
    /// byte-identical to the whole-run <c>StringBuilder</c> it replaced, and small enough that
    /// <c>AgentRunService.OffloadLargeTranscriptAsync</c> keeps it inline exactly as before. A SPILLED spool is
    /// content that offloader was always going to move out, so it goes straight to the artifact store here and the
    /// result carries the same <c>("" + ref)</c> shape the offloader would have produced — the content-addressed
    /// store dedups by sha, so the very same bytes even land on the very same artifact id. Either way the persisted
    /// <c>result_jsonb</c> is unchanged; what changes is that neither the full string nor a full byte array has to exist.
    /// </summary>
    internal static async Task<AgentRunResult> AttachTranscriptAsync(IArtifactStore artifacts, AgentRunResult result, Guid teamId, AgentTranscriptSpool transcript, CancellationToken cancellationToken)
    {
        if (!transcript.Spilled) return result with { Transcript = transcript.RetainedText() };

        if (artifacts is not IArtifactStreamStore streams)
            throw new ArtifactStreamingWriteUnavailableException(artifacts?.GetType() ?? typeof(IArtifactStore), typeof(IArtifactStreamStore));
        await transcript.SealAsync(cancellationToken).ConfigureAwait(false);
        var artifactId = await streams.PutAsync(new ArtifactStreamWriteRequest(teamId, TranscriptContentType, transcript), cancellationToken).ConfigureAwait(false);

        return result with { Transcript = "", TranscriptArtifactId = artifactId };
    }

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
    /// no contract, a result that is not a genuine self-report (see <see cref="SelfReportedSuccess"/>), or an
    /// unanswered decision. Grader errors record not-accepted rather than crashing the completion.
    ///
    /// <para>D4b: a run that self-reported FAILURE but left WORK behind (<see cref="AnyWorkPresent"/>) is graded too —
    /// a self-report is a claim, not a verdict, and an agent that did the work but said "I couldn't finish" used to
    /// terminalize Failure with its work discarded. Its grade folds through
    /// <see cref="FoldSelfReportedFailureGrade"/>, the single-agent twin of the supervisor lane's per-unit
    /// under-claim fold. A failure with NOTHING to grade skips the gate exactly as before.</para>
    ///
    /// <para>S2: a run with NO pushed branch (a patch-only publish policy, or a guard-blocked push) is graded
    /// against its own RECORDED PATCH instead of failing closed, when one exists — the same agent-independent
    /// grader, just anchored on the base SHA instead of a ref (<see cref="ISupervisorAcceptanceGrader.GradePatchAsync"/>).
    /// Only when there is truly nothing to grade (no branch, no patch) does <see cref="AgentAcceptanceContract.ExpectsChanges"/>
    /// decide the outcome: <c>false</c> is the correctly-predicted no-diff case (a vacuous pass, never a failure);
    /// otherwise (the byte-identical default) it fails closed exactly as before this field existed.</para>
    /// </summary>
    internal async Task<AgentRunResult> GradeAcceptanceIfPresentAsync(AgentRun run, AgentTask task, AgentRunResult result, IWorkspaceHandle? workspace, CancellationToken cancellationToken)
    {
        if (!AgentAcceptanceContract.RequiresGrade(task)) return result;
        if (SelfReportedSuccess(result) is not { } claimedSuccess) return result;

        // A1 always takes precedence (the same defer the output critic honours): a run that left a decision.request
        // unanswered re-grades to NeedsReview(NeedsDecision) WITH the decision linkage at the completion choke point
        // — flunking it here first would strand the decision unlinked and skip the retry-resume loop. The answered,
        // resumed attempt gets graded at ITS completion. Applies to both the single- and multi-repo paths below.
        using (var ledgerScope = _scopeFactory.CreateScope())
        {
            var ledger = ledgerScope.ServiceProvider.GetRequiredService<IToolCallLedgerService>();
            if (await ledger.FindBlockingDecisionIdAsync(run.Id, cancellationToken).ConfigureAwait(false) is not null) return result;
        }

        var graded = await GradeAgainstOracleAsync(run, task, result, workspace, cancellationToken).ConfigureAwait(false);

        return claimedSuccess ? graded : FoldSelfReportedFailureGrade(result, graded);
    }

    /// <summary>
    /// Whether this result carries a genuine SELF-REPORT about the work — <c>true</c> (it says it finished),
    /// <c>false</c> (it says it failed AND left work the oracle can grade), or null when there is no claim to check
    /// against a verdict. The exact-status matching mirrors the supervisor lane's per-unit
    /// <c>SupervisorTurnService.Rehydrate.ClassifyUnitContradiction</c>: Cancelled (the user's own stop), TimedOut
    /// and NeedsReview (a watchdog / a human-owed park) never reached a verdict of their own, so grading them would
    /// mint an objective verdict for an attempt that never claimed to be finished. A self-reported failure with NO
    /// work has nothing to grade, which is the pre-D4b behaviour for every failure.
    /// </summary>
    internal static bool? SelfReportedSuccess(AgentRunResult result) => result.Status switch
    {
        AgentRunStatus.Succeeded => true,
        AgentRunStatus.Failed when AnyWorkPresent(result) => false,
        _ => null,
    };

    /// <summary>
    /// D4b: fold an objective grade onto a run that self-reported FAILURE — the single-agent twin of the supervisor
    /// lane's per-unit under-claim fold (<c>ClassifyUnitContradiction</c> + <see cref="AgentContradiction.Detect"/>,
    /// where an under-claimed unit with a PASSED gate folds as finished and recites as "objectively fine"):
    /// <list type="bullet">
    /// <item>PASSED ⇒ the verdict OUTRANKS the claim: the run lands <see cref="AgentRunStatus.Succeeded"/> with
    /// <see cref="AgentContradiction.UnderClaim"/> recorded, keeping the agent's own summary / error / exit reason
    /// intact — the status is corrected, its account of itself is not rewritten.</item>
    /// <item>FAILED ⇒ claim and verdict AGREE: the run stays Failed with the grade recorded and NO contradiction.
    /// The claim-side fields come from the ORIGINAL result, which is what keeps
    /// <see cref="AgentAcceptanceContract.FailClosed"/>'s over-claim stamp (correct for a Succeeded self-report,
    /// a lie for this one) off the folded result.</item>
    /// <item>INFRA (<see cref="AgentAcceptanceContract.IsInfraFailure"/>) ⇒ the check never ran, so no verdict is
    /// minted at all: <see cref="AgentRunResult.AcceptancePassed"/> stays null and only the detail is recorded.</item>
    /// </list>
    /// </summary>
    internal static AgentRunResult FoldSelfReportedFailureGrade(AgentRunResult claimed, AgentRunResult graded) => graded.AcceptancePassed switch
    {
        // A VACUOUS pass is not a verdict: nothing was checked (the contract declared no diff was expected and the
        // lane found no branch/patch to grade), so it can never outrank a claim. The run keeps its own outcome,
        // exactly as it did before this gate existed.
        true when AgentAcceptanceContract.IsVacuousPass(graded.AcceptanceDetail) => claimed,
        true => graded with { Status = AgentRunStatus.Succeeded, Contradiction = AgentContradiction.UnderClaim },
        false => claimed with
        {
            AcceptancePassed = AgentAcceptanceContract.IsInfraFailure(graded.AcceptanceDetail, AnyWorkPresent(claimed)) ? null : false,
            AcceptanceDetail = graded.AcceptanceDetail,
            AcceptanceEvidenceId = graded.AcceptanceEvidenceId,
        },
        null => graded,
    };

    /// <summary>The oracle grade itself, once the gate has decided this result is gradable: the multi-repo fold, the repo-less scratch fold, or the single-repo branch/patch fold.</summary>
    private async Task<AgentRunResult> GradeAgainstOracleAsync(AgentRun run, AgentTask task, AgentRunResult result, IWorkspaceHandle? workspace, CancellationToken cancellationToken)
    {
        if (result.RepositoryResults.Count > 0) return await GradeMultiRepoAcceptanceAsync(run, task, result, cancellationToken).ConfigureAwait(false);

        var spec = task.Acceptance!;
        var command = spec.Command.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

        if (task.RepositoryId is not { } repositoryId)
        {
            // DC-4 slice 2 (the repo-less lane): the scratch workspace IS the world — grade the oracle directly
            // against it while the handle is still alive (the agent process has exited; grading its left-behind
            // directory is equivalent to grading a clone). Only a contract with truly no world fails closed.
            if (workspace is { Repositories.Count: 0 } scratch)
                return await GradeScratchAsync(run, spec with { Command = command }, result, scratch, cancellationToken).ConfigureAwait(false);

            _logger.LogWarning("Agent run {RunId}: an acceptance contract is present but there is no repository to grade against — failing closed", run.Id);

            return AcceptanceFailed(result, "no-branch-or-repo");
        }

        var hasBranch = !string.IsNullOrEmpty(result.ProducedBranch);
        var hasPatch = HasGradeablePatch(result);

        if (!hasBranch && !hasPatch)
        {
            if (!AgentAcceptanceContract.ExpectsChanges(task))
            {
                _logger.LogInformation("Agent run {RunId}: no diff was expected and none was produced — the acceptance contract is vacuously satisfied", run.Id);
                return AgentAcceptanceContract.NotApplicable(result, AgentAcceptanceContract.NotApplicableDetail);
            }

            _logger.LogWarning("Agent run {RunId}: an acceptance contract is present but there is no produced branch or recorded patch to grade — failing closed", run.Id);
            return AcceptanceFailed(result, "no-branch-or-repo");
        }

        BenchmarkGrade grade;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var grader = scope.ServiceProvider.GetRequiredService<ISupervisorAcceptanceGrader>();
            var fullSpec = spec with { Command = command };
            var timeoutSeconds = spec.TimeoutSeconds ?? SupervisorLane.AcceptanceGradeTimeoutSeconds;

            // C3: the run's own recorded base is the oracle anchor — the grader restores the acceptance command's
            // program file from it, so an agent cannot buy its own pass by rewriting the check script it is graded
            // with. The patch lane always had this anchor; the BRANCH lane discarded it and graded the candidate's
            // bytes as the judge.
            grade = hasBranch
                ? await grader.GradeAsync(repositoryId, run.TeamId, result.ProducedBranch!, fullSpec, timeoutSeconds, result.BaseSha, cancellationToken).ConfigureAwait(false)
                : await grader.GradePatchAsync(repositoryId, run.TeamId, result.BaseSha!, result.Patch, result.PatchArtifactId, fullSpec, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: the acceptance grade failed unexpectedly; recording not-accepted", run.Id);

            grade = new BenchmarkGrade { Passed = false, Detail = $"grade-error: {ex.Message}", Class = Messages.Agents.Benchmark.GradeFailureClass.GraderFault };
        }

        if (grade.Passed)
        {
            _logger.LogInformation("Agent run {RunId}: the acceptance check passed ({Detail})", run.Id, grade.Detail);

            return result with { AcceptancePassed = true, AcceptanceDetail = grade.Detail, AcceptanceEvidenceId = grade.EvidenceArtifactId };
        }

        _logger.LogWarning("Agent run {RunId}: the acceptance check FAILED ({Detail}) — re-grading the run to Failed", run.Id, grade.Detail);

        return AcceptanceFailed(result, grade.Detail) with { AcceptanceEvidenceId = grade.EvidenceArtifactId };
    }

    /// <summary>
    /// The repo-less grade fold: the same per-kind oracle over the still-alive scratch directory, the same pass/fail
    /// stamping as the branch/patch lanes — a grader escape degrades to not-accepted, never a crash.
    /// <para>C2: every repo-less run now HAS a scratch world (the walk needs one), so the kind check that used to be
    /// implied by "no scratch existed for a TestsPass contract" is explicit here instead. A TestsPass argv in a
    /// directory of captured documents is a category error — a bare <c>exit 0</c> would pass vacuously — so it keeps
    /// failing closed on the exact same detail as before, from <see cref="AgentAcceptanceContract.GradesFromDeliverables"/>,
    /// the one rule the supervisor fold's twin reads too.</para>
    /// </summary>
    private async Task<AgentRunResult> GradeScratchAsync(AgentRun run, SupervisorAcceptanceSpec spec, AgentRunResult result, IWorkspaceHandle scratch, CancellationToken cancellationToken)
    {
        if (!AgentAcceptanceContract.GradesFromDeliverables(spec))
        {
            _logger.LogWarning("Agent run {RunId}: a TestsPass acceptance contract is present but there is no repository to run it against — failing closed", run.Id);

            return AcceptanceFailed(result, "no-branch-or-repo");
        }

        BenchmarkGrade grade;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var grader = scope.ServiceProvider.GetRequiredService<ISupervisorAcceptanceGrader>();
            var timeoutSeconds = spec.TimeoutSeconds ?? SupervisorLane.AcceptanceGradeTimeoutSeconds;

            grade = await grader.GradeDirectoryAsync(scratch.Directory, spec, run.TeamId, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: the scratch acceptance grade failed unexpectedly; recording not-accepted", run.Id);
            grade = new BenchmarkGrade { Passed = false, Detail = $"grade-error: {ex.Message}", Class = Messages.Agents.Benchmark.GradeFailureClass.GraderFault };
        }

        if (grade.Passed)
        {
            _logger.LogInformation("Agent run {RunId}: the scratch acceptance check passed ({Detail})", run.Id, grade.Detail);

            return result with { AcceptancePassed = true, AcceptanceDetail = grade.Detail, AcceptanceEvidenceId = grade.EvidenceArtifactId };
        }

        _logger.LogWarning("Agent run {RunId}: the scratch acceptance check FAILED ({Detail}) — re-grading the run to Failed", run.Id, grade.Detail);

        return AcceptanceFailed(result, grade.Detail) with { AcceptanceEvidenceId = grade.EvidenceArtifactId };
    }

    /// <summary>
    /// Grade a MULTI-repo run's acceptance contract against EVERY repo it actually changed — a contract binds the
    /// WHOLE change, not just one repo, mirroring the supervisor lane's per-unit
    /// <c>SupervisorTurnService.Rehydrate.GradeUnitAcceptanceMultiRepoAsync</c> (this executor previously deferred
    /// a multi-repo run's grade entirely, leaving <see cref="AgentRunResult.AcceptancePassed"/> null and the run
    /// Succeeded on self-report alone). A repo with no produced branch had nothing to verify there, so it is not a
    /// target; a run with no targets at all falls back to <see cref="AgentAcceptanceContract.ExpectsChanges"/>'s
    /// verdict, same as the single-repo path. All targets must pass, short-circuiting on the first failure (the
    /// detail names the failing repo); any unexpected non-cancellation grader escape degrades to not-accepted so
    /// the completion pipeline can never crash on a grade. Matching its supervisor-lane twin's own documented scope
    /// trim, S2's per-repo PATCH fallback is deliberately NOT extended here — a multi-repo target with no produced
    /// branch anywhere falls straight to the ExpectsChanges verdict above, never a per-repo patch-based grade.
    /// </summary>
    private async Task<AgentRunResult> GradeMultiRepoAcceptanceAsync(AgentRun run, AgentTask task, AgentRunResult result, CancellationToken cancellationToken)
    {
        var spec = task.Acceptance!;
        var command = spec.Command.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        var fullSpec = spec with { Command = command };
        var timeoutSeconds = spec.TimeoutSeconds ?? SupervisorLane.AcceptanceGradeTimeoutSeconds;

        var targets = result.RepositoryResults.Where(r => !string.IsNullOrEmpty(r.ProducedBranch) && r.RepositoryId is not null).ToList();

        if (targets.Count == 0)
        {
            if (!AgentAcceptanceContract.ExpectsChanges(task))
            {
                _logger.LogInformation("Agent run {RunId}: no diff was expected in any repo and none was produced — the acceptance contract is vacuously satisfied", run.Id);
                return AgentAcceptanceContract.NotApplicable(result, AgentAcceptanceContract.NotApplicableDetail);
            }

            _logger.LogWarning("Agent run {RunId}: an acceptance contract is present but no repo produced a branch to grade — failing closed", run.Id);
            return AcceptanceFailed(result, "no-branch-or-repo");
        }

        using var scope = _scopeFactory.CreateScope();
        var grader = scope.ServiceProvider.GetRequiredService<ISupervisorAcceptanceGrader>();

        foreach (var target in targets)
        {
            BenchmarkGrade grade;
            try
            {
                // C3: each repo's own recorded base anchors ITS oracle restore — same protection as the single-repo lane.
                grade = await grader.GradeAsync(target.RepositoryId!.Value, run.TeamId, target.ProducedBranch!, fullSpec, timeoutSeconds, target.BaseSha, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Agent run {RunId}: the acceptance grade for repo '{Alias}' failed unexpectedly; recording not-accepted", run.Id, target.Alias);
                return AcceptanceFailed(result, $"repo '{target.Alias}': grade-error: {ex.Message}");
            }

            if (!grade.Passed)
            {
                _logger.LogWarning("Agent run {RunId}: the acceptance check FAILED for repo '{Alias}' ({Detail}) — re-grading the run to Failed", run.Id, target.Alias, grade.Detail);
                // P5-2: carry the failing repo's evidence binding, mirroring the single-repo fail path above and the
                // supervisor twin's aggregate — without it this lane's multi-repo failure receipts stay evidence-less.
                return AcceptanceFailed(result, $"repo '{target.Alias}': {grade.Detail}") with { AcceptanceEvidenceId = grade.EvidenceArtifactId };
            }
        }

        _logger.LogInformation("Agent run {RunId}: the acceptance check passed for every repo", run.Id);

        return result with { AcceptancePassed = true, AcceptanceDetail = "accepted" };
    }

    /// <summary>Whether <paramref name="result"/> carries a recorded patch this executor could grade with (S2) — a base to anchor the independent clone on, PLUS either an inline diff or an offloaded artifact reference. Absent any one of these there is genuinely nothing to apply.</summary>
    private static bool HasGradeablePatch(AgentRunResult result) =>
        !string.IsNullOrEmpty(result.BaseSha) && (result.PatchArtifactId is not null || !string.IsNullOrEmpty(result.Patch));

    private static AgentRunResult AcceptanceFailed(AgentRunResult result, string? detail) => AgentAcceptanceContract.FailClosed(result, detail);

    /// <summary>
    /// Review the agent's produced change with an INDEPENDENT critic at completion. Off ⇒ byte-identical
    /// (no per-run <c>OutputReviewMode</c> baked). Self-skips
    /// when there's nothing to gate — a non-success, or a no-op / re-attach run with no captured diff. A DISAPPROVED change
    /// re-grades the would-be <see cref="AgentRunStatus.Succeeded"/> run to <see cref="AgentRunStatus.NeedsReview"/>
    /// (<see cref="CompletionDisposition.NeedsReview"/>) so a human looks before the downstream PR-open (Succeeded-gated)
    /// consumes it; the captured work is preserved, and the critique rides <see cref="AgentRunResult.ReviewFeedback"/>.
    /// FAILS OPEN — a failed review keeps the original result. Under <see cref="ReviewMode.Improve"/> the S6 revise loop
    /// reads the flag + feedback and buys the agent a bounded re-run before the flag stands (Gate never re-runs).
    ///
    /// <para>C1: a TEXT-ONLY result is reviewed too. Until this fix the gate demanded a diff, so the one shape whose
    /// whole output IS its text — a question answered in the summary, a report written into a captured deliverable —
    /// was the single shape that shipped past a configured Gate/Improve review untouched, exactly where an answer is
    /// least falsifiable. Such a result is rendered as an ANSWER and judged against the goal PLUS the task's
    /// acceptance criteria. A diff-bearing result is rendered byte-identically to before.</para>
    /// </summary>
    internal async Task<AgentRunResult> ReviewOutputIfEnabledAsync(AgentTask task, AgentRunResult result, AgentRun run, CancellationToken cancellationToken)
    {
        if (task.OutputReviewMode == ReviewMode.None) return result;
        if (result.Status != AgentRunStatus.Succeeded) return result;
        if (!HasReviewableOutput(result)) return result;

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

        // Built LAZILY, and at most ONCE. The two consumers below are mutually exclusive (the model rung runs only
        // when the agent rung failed; the co-sign only when it succeeded AND approved), so an agent DISAPPROVAL needs
        // no request at all — and building one eagerly charged that path a manifest listing plus a blob read per
        // captured deliverable for a render nobody would look at. Memoized so a future second consumer still pays once.
        CriticRequest? built = null;
        async Task<CriticRequest> RequestAsync() => built ??= await BuildReviewRequestAsync(task, result, run, cancellationToken).ConfigureAwait(false);

        if (verdict.Failed)
            verdict = await ReviewRecordedAsync(await RequestAsync().ConfigureAwait(false), run, task.ReviewerModelId, cancellationToken).ConfigureAwait(false);

        // D② approve co-sign: an AGENT reviewer's APPROVAL gets a cheap independent MODEL co-check before it counts.
        // The reviewer agent READS the produced tree — hostile committed content could try to instruct it to approve
        // (the injection prize) — so approval requires CONSENSUS across the two independent channels: a model
        // disagreement fails toward the human (NeedsReview carrying both sides), never a silent pass. A FAILED
        // co-check keeps the agent's approval (fail-open — a broken co-check must not manufacture a flag), and a
        // DISAPPROVING agent verdict needs no co-sign (the worst case of a wrong block is one wasted revise round).
        if (agentReviewed && verdict.Approved)
        {
            var coSign = await ReviewRecordedAsync(await RequestAsync().ConfigureAwait(false), run, task.ReviewerModelId, cancellationToken).ConfigureAwait(false);

            if (!coSign.Failed && !coSign.Approved)
                verdict = coSign with { Rationale = $"The reviewer agent approved, but the independent model co-check disagreed: {coSign.Rationale}" };
        }

        // FAIL-OPEN, but no longer in silence. A Failed verdict here means BOTH rungs could not produce one — the
        // configured output review did not happen and the change ships ungated. A STANDALONE run (no WorkflowRunId)
        // has no workflow ledger for the critic's review.skipped beat to land on, so the agent's own event stream is
        // the only surface its operator ever reads; the beat rides here for every agent run alike.
        if (verdict.Failed)
        {
            await AppendReviewSkippedWarningAsync(runId, verdict, cancellationToken).ConfigureAwait(false);

            return result;
        }

        if (verdict.Approved) return result;   // a clean pass ⇒ byte-identical

        await AppendOutputFlaggedWarningAsync(runId, verdict, cancellationToken).ConfigureAwait(false);

        return result with { Status = AgentRunStatus.NeedsReview, CompletionDisposition = CompletionDisposition.NeedsReview, ExitReason = "output-flagged", ReviewFeedback = RenderReviewFeedback(verdict) };
    }

    /// <summary>
    /// Run the output-review critic — the executor's one IN-PROCESS model call — WITH recording. The executor runs in a
    /// Hangfire job OUTSIDE the engine's per-node <see cref="LlmCallContext"/> scope (which the engine pushes around every
    /// node), so this call would otherwise record NOTHING. Mirror the engine: push the run's
    /// <c>(WorkflowRunId, NodeId, IterationKey)</c> cell + a FRESH-scope ledger writer/offloader (the long-running-job
    /// pattern above, not a ctor dep) around the call, so the recording decorator lands its <c>interaction.*</c> onto the
    /// SAME <c>workflow_run_record</c> ledger as the rest of the run, keyed to the spawning agent.run node. A standalone
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
    internal static string RenderReviewFeedback(CriticVerdict verdict)
    {
        var body = verdict.Issues.Count > 0 ? $"{verdict.Rationale} Issues: {string.Join("; ", verdict.Issues)}" : verdict.Rationale;

        // The reviewer's model is ATTRIBUTION, so it trails the actionable critique rather than leading it (this same
        // string is fed back to the agent for its bounded revise round — the guidance has to come first). It lets this
        // lane tell a real second opinion from the one-model fallback, which it previously could not. Absent for an
        // agent reviewer's verdict, which leaves the feedback byte-identical.
        return string.IsNullOrWhiteSpace(verdict.ReviewerModel) ? body : $"{body} (reviewed on {verdict.ReviewerModel})";
    }

    /// <summary>Render the produced change for the critic — the git unified diff (already capped), with the agent's summary + the changed-file list as context.</summary>
    private static string RenderChange(AgentRunResult result)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(result.Summary)) builder.AppendLine($"Agent summary: {result.Summary}").AppendLine();

        builder.AppendLine($"Changed files ({result.ChangedFiles.Count}): {string.Join(", ", result.ChangedFiles)}").AppendLine();
        builder.AppendLine("Diff:").AppendLine(string.IsNullOrEmpty(result.Patch) ? "(no unified diff captured)" : result.Patch);

        return builder.ToString();
    }

    /// <summary>C1 — the total captured-deliverable bytes a text-only review reads. Bounded so a large report cannot balloon the critic prompt; the overflow is stated in the render rather than silently dropped.</summary>
    internal const int MaxReviewedDeliverableChars = 64 * 1024;

    /// <summary>Whether <paramref name="result"/> carries a recorded diff — the pre-C1 review trigger, and still the one that selects the byte-identical change render.</summary>
    private static bool HasDiff(AgentRunResult result) => result.ChangedFiles.Count > 0 || !string.IsNullOrEmpty(result.Patch);

    /// <summary>C1 — whether there is anything for the critic to READ: a diff, an answer in the agent's summary, or a captured deliverable. All three absent ⇒ a genuine no-op / re-attach run, which still self-skips exactly as before.</summary>
    private static bool HasReviewableOutput(AgentRunResult result) =>
        HasDiff(result) || !string.IsNullOrWhiteSpace(result.Summary) || result.CapturedArtifactCount + result.UndeclaredArtifactCount > 0;

    /// <summary>
    /// C1 — the critic's request for THIS result: the unchanged change render for a diff-bearing run, else the answer
    /// render judged against the goal plus the task's own acceptance criteria (an answer's "done" is its contract, not
    /// its file list). BOTH shapes name <see cref="LlmStructuredCritic.OutputReviewCallKind"/>: this is the one review
    /// rung that examines a produced RESULT, and the Room's "did anything check this?" probe reads exactly that kind.
    /// </summary>
    private async Task<CriticRequest> BuildReviewRequestAsync(AgentTask task, AgentRunResult result, AgentRun run, CancellationToken cancellationToken)
    {
        if (HasDiff(result))
            return new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent change", Artifact = RenderChange(result), Goal = task.Goal, CallKind = LlmStructuredCritic.OutputReviewCallKind };

        var deliverables = await ReadCapturedDeliverablesAsync(result, run, cancellationToken).ConfigureAwait(false);

        return new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent answer", Artifact = RenderAnswer(result, deliverables), Goal = ReviewGoal(task), CallKind = LlmStructuredCritic.OutputReviewCallKind };
    }

    /// <summary>The goal the critic judges an ANSWER against — the task goal plus the acceptance criteria the operator/planner authored, so "is this done?" is asked against the stated contract rather than against the prose alone. No contract ⇒ the goal verbatim.</summary>
    internal static string? ReviewGoal(AgentTask task)
    {
        if (task.Acceptance?.Command is not { Count: > 0 } criteria) return task.Goal;

        var described = string.IsNullOrWhiteSpace(task.Acceptance.Description) ? "" : $" ({task.Acceptance.Description})";

        return $"{task.Goal}\n\nAcceptance criteria{described}: {string.Join(", ", criteria)}";
    }

    /// <summary>C1 — the answer the critic reads: the agent's own closing summary plus every captured deliverable's text, each under its own path header. Internal + static so the bounding + the no-deliverable wording are unit-pinned.</summary>
    internal static string RenderAnswer(AgentRunResult result, IReadOnlyList<(string Path, string Text)> deliverables)
    {
        var builder = new StringBuilder();

        builder.AppendLine("This run produced no code change — its output IS the answer below.").AppendLine();
        builder.AppendLine($"Agent summary: {(string.IsNullOrWhiteSpace(result.Summary) ? "(none)" : result.Summary)}").AppendLine();

        if (deliverables.Count == 0)
        {
            builder.AppendLine("Captured deliverables: (none)");
            return builder.ToString();
        }

        foreach (var (path, text) in deliverables)
            builder.AppendLine($"=== {path} ===").AppendLine(text).AppendLine();

        return builder.ToString();
    }

    /// <summary>
    /// Read this attempt's captured deliverables back out of the artifact store, bounded by
    /// <see cref="MaxReviewedDeliverableChars"/> across the whole set. BEST-EFFORT: an unresolvable row, or any
    /// store fault, degrades to the summary-only review rather than failing the run — the critic is advisory and
    /// fails open, so a storage hiccup must never manufacture a flag OR block completion.
    /// </summary>
    private async Task<IReadOnlyList<(string Path, string Text)>> ReadCapturedDeliverablesAsync(AgentRunResult result, AgentRun run, CancellationToken cancellationToken)
    {
        if (result.CapturedArtifactCount + result.UndeclaredArtifactCount == 0) return Array.Empty<(string, string)>();

        try
        {
            var rows = await _artifactManifests.ListForAgentRunAsync(run.Id, run.TeamId, cancellationToken).ConfigureAwait(false);
            var current = rows.Where(r => r.SupersededByManifestId is null).ToList();

            if (current.Count == 0) return Array.Empty<(string, string)>();

            var latest = current.Max(r => r.FenceEpoch);
            var read = new List<(string, string)>();
            var budget = MaxReviewedDeliverableChars;

            foreach (var row in current.Where(r => r.FenceEpoch == latest).OrderBy(r => r.LogicalPath, StringComparer.Ordinal))
            {
                if (budget <= 0) break;

                var bytes = await _artifacts.GetBytesAsync(run.TeamId, row.ContentArtifactId, cancellationToken).ConfigureAwait(false);

                if (bytes is null) continue;

                var text = System.Text.Encoding.UTF8.GetString(bytes.Bytes);
                var kept = text.Length <= budget ? text : text[..budget] + "\n… (truncated for review) …";

                budget -= Math.Min(text.Length, budget);
                read.Add((row.LogicalPath, kept));
            }

            return read;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not read the captured deliverables for the output review; reviewing the summary alone", run.Id);
            return Array.Empty<(string, string)>();
        }
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

    /// <summary>Append a Warning event saying the configured output review did NOT run, so a change that shipped ungated says so on the lane its operator actually reads (a standalone run has no workflow ledger for the critic's <c>review.skipped</c> beat). Best-effort, exactly like the flagged warning: reporting a skipped review may never mask the run's terminal write.</summary>
    private async Task AppendReviewSkippedWarningAsync(Guid runId, CriticVerdict verdict, CancellationToken cancellationToken)
    {
        try
        {
            await _runs.AppendEventAsync(runId, new AgentEvent { Kind = AgentEventKind.Warning, Text = $"Review skipped — the configured output review could not run, so this change was not gated: {verdict.Rationale}" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Agent run {RunId}: could not record the review-skipped warning event", runId);
        }
    }

    /// <summary>Deterministic, run-unique remote branch name for a produced diff. Pure + private so it's unit-pinned. Run-id-derived, so a workflow retry (new run id) → a new branch; a re-push of the same run → the same branch (plain --force overwrite).</summary>
    /// <summary>
    /// P3 (git-layer fence): the run's push ref is GENERATION-SPECIFIC — a reclaimed attempt (fence epoch &gt; 1)
    /// pushes <c>-g&lt;epoch&gt;</c>, so a superseded zombie worker and its reclaimed successor can NEVER write the
    /// same remote ref. The StillOwns pre-check above the push narrows the zombie window; this closes it at the
    /// remote: the zombie's late force-push lands on ITS OWN generation's ref — an orphan nothing references,
    /// because the manifest (whose write IS epoch-fenced, #1341) records the current generation's branch + confirmed
    /// sha and every consumer follows the manifest. First attempts (epoch 1, the overwhelming case) keep the
    /// unsuffixed name byte-identical.
    /// </summary>
    internal static string BuildBranchName(Guid runId, long fenceEpoch = 1) =>
        fenceEpoch <= 1 ? $"codespace/agent/{runId:N}" : $"codespace/agent/{runId:N}-g{fenceEpoch}";

    /// <summary>
    /// Evaluate the publish guard chain (see <see cref="IPublishGuard"/>), in ascending <see cref="IPublishGuard.Order"/>
    /// — the first guard whose verdict is non-null wins. Push is the DEFAULT for a non-empty diff now (the deleted
    /// env gate's replacement): a guard is an explicit, inspectable OPT-OUT, never an opt-in gate an operator has to
    /// flip. <paramref name="repositoryId"/> is resolved to its <see cref="Repository"/> row once so every guard sees
    /// the same snapshot; null when the task carries none (every guard then clears, since there is nothing to gate).
    /// </summary>
    private async Task<PublishGuardVerdict?> EvaluatePublishGuardsAsync(AgentTask task, Guid? repositoryId, CancellationToken cancellationToken)
    {
        var repository = repositoryId is { } id
            ? await _db.Repository.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false)
            : null;

        foreach (var guard in _publishGuards)
            if (guard.Evaluate(task, repository) is { } verdict)
                return verdict;

        return null;
    }

    /// <summary>
    /// The single gate deciding whether a run INTEGRATES its parallel agent contributions on disk: the profile's
    /// explicit choice, else <see cref="IntegrateBranchByDefault"/>. It used to OR with a deployment-wide environment
    /// flag, so a profile could ask for integration but never decline it; the choice now narrows as well as widens.
    /// Pure + internal so it's unit-pinned and production reads it through this single gate.
    /// </summary>
    internal static bool ShouldIntegrate(bool? perRunChoice) => perRunChoice ?? IntegrateBranchByDefault;

    /// <summary>
    /// The single gate deciding whether THIS run is served the full side-effecting tool fabric: the task's explicit
    /// per-run choice, or <see cref="FullToolCatalogByDefault"/> when it expresses none. The per-run field now narrows
    /// as well as widens — <c>false</c> holds a run to the read-only slice, which the benchmark's CLI-only arm needs
    /// now that the ambient default is on. Pure + internal so it's unit-pinned and the benchmark runner can read the
    /// SAME gate to record what the executor actually did (no mislabeled rows).
    /// </summary>
    internal static bool UsesFullToolCatalog(AgentTask task) => task.EnableMcpEndpoint ?? FullToolCatalogByDefault;

    /// <summary>
    /// Which slice of the tool catalog this run's endpoint serves. The endpoint now opens for EVERY run; this is the
    /// ONLY thing the opt-in changes. <see cref="UsesFullToolCatalog"/> ON (the ambient flag OR the per-run opt-in)
    /// selects <see cref="McpCatalogMode.Full"/> — the whole registry incl. the side-effecting fabric, byte-identical to
    /// before. OFF (the default) selects <see cref="McpCatalogMode.ReadOnly"/> — only read-only tools (e.g.
    /// <c>get_context</c> + the git reads) are served, so a default run still reaches the safe read tools without
    /// exposing any side effect. Pure + internal so it's unit-pinned.
    /// </summary>
    internal static McpCatalogMode ResolveMcpCatalogMode(AgentTask task) => UsesFullToolCatalog(task) ? McpCatalogMode.Full : McpCatalogMode.ReadOnly;

    /// <summary>
    /// A BOOT diagnostic the worker host calls once at startup so a mis-configured tool fabric is VISIBLE at deploy time,
    /// not silently discovered as a tool-less run hours later. The MCP endpoint now opens for EVERY run (serving the
    /// read-only tools by default, the full fabric on opt-in), so the <c>codespace-mcp</c> proxy is needed by every run:
    /// when it can't be resolved at <see cref="LocalProcessRunner.McpProxyBinaryPath"/>, log a clear Warning naming the
    /// resolved path + the override env var (every run will degrade to TOOL-LESS); otherwise log a confirming
    /// Information line that also notes whether the full side-effecting fabric is enabled deployment-wide
    /// (<see cref="FullToolCatalogByDefault"/>). Pure logging — never throws, never fails boot (the fabric is optional infra).
    /// The per-run <see cref="BuildMcpWiring"/> ALSO fail-closes + logs per run; this is the proactive deploy-time half of
    /// the same fail-closed signal. Internal + static so it's unit-pinnable without a host.
    /// </summary>
    public static void LogMcpProxyReadiness(ILogger logger)
    {
        var proxyPath = LocalProcessRunner.McpProxyBinaryPath();

        if (File.Exists(proxyPath))
        {
            logger.LogInformation("MCP tool fabric ready; codespace-mcp proxy resolved at '{ProxyPath}'. Read-only tools (get_context + git reads) serve by default; the full side-effecting fabric is {FabricState}.", proxyPath, FullToolCatalogByDefault ? "ENABLED by default" : "opt-in per run");
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

        // Governance is a committed constant; the endpoint threads it + the run's fence epoch into each connection's
        // handler so a side-effecting tool call is ledger-tracked.
        var governanceEnabled = McpRequestHandler.GovernanceEnabled;

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
    /// <summary>P0-B2: the seven fabric facts, composed from the live endpoint + the wiring the spec carried — configuration beside observation, so "the tools were available" is a recorded fact, not an inference.</summary>
    internal static AgentRunResult AttachMcpEvidence(AgentRunResult result, AgentTask task, Mcp.AgentMcpEndpoint? endpoint, McpServerWiring? wiring) => result with
    {
        McpEvidence = new McpFabricEvidence
        {
            RequestedCatalogMode = ResolveMcpCatalogMode(task).ToString(),
            EndpointBound = endpoint is not null,
            DeclarationWritten = wiring is not null,
            ProxyResolved = File.Exists(LocalProcessRunner.McpProxyBinaryPath()),
            HandshakeObserved = endpoint?.HandshakeObserved == true,
            ObservedToolCalls = endpoint?.ObservedToolCalls ?? 0,
            EffectiveCatalogDigest = endpoint?.EffectiveCatalogDigest(),
        },
    };

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
    private async Task<(IReadOnlyDictionary<string, string> Env, SecretRedactor Redactor, string? ModelBaseUrl, string? ModelProvider, string? DefaultModel, Guid? CredentialId)> ResolveModelCredentialEnvAsync(AgentTask task, Guid teamId, IAgentHarness harness, CancellationToken cancellationToken)
    {
        var projector = harness as IModelCredentialProjector;

        var credential = await _modelCredentials.ResolveAsync(task, teamId, projector, cancellationToken).ConfigureAwait(false);

        var env = projector is not null && credential is not null ? projector.ProjectToEnv(credential) : EmptySecretEnv;

        // Keyed on EVERY secret this launch injects into the child — over the merged env the run actually runs with,
        // so an author-supplied token is covered exactly like the resolved key.
        var redactor = BuildRunRedactor(MergeEnvironment(task.Environment, env), credential);

        // The non-secret base URL + provider tag flow out so a restricted (Allowlist) run can pin its model-API host
        // in the egress allowlist (B3.3b). DefaultModel flows out so a model-less ("auto") run falls back to one of the
        // credential's own models instead of the CLI default. All null when no credential resolved. CredentialId
        // names the ROW whose key is now in the environment (null for the operator-global key, which has no row) —
        // D3 bounds an escalation's candidate models to exactly that row.
        return (env, redactor, credential?.BaseUrl, credential?.Provider, credential?.DefaultModel, credential?.CredentialId);
    }

    /// <summary>
    /// The shortest value usable as a redaction needle. Below this a value is a fragment, not an identifier —
    /// striking it would shred unrelated text (an injected <c>1</c> would hit every line carrying a digit), so it is
    /// left readable. The SAME threshold the real-model artifact redactor applies (<c>MIN_NEEDLE_LENGTH</c> in
    /// <c>.github/scripts/collect-real-model-verdicts.sh</c>); no real api key or access token is shorter.
    /// </summary>
    internal const int MinimumNeedleLength = 8;

    /// <summary>
    /// The most needles <see cref="BuildRunRedactor"/> will hand back. <see cref="AgentTask.Environment"/> is
    /// author-supplied and uncapped, so without this a task naming a thousand secret-marked variables would make every
    /// redaction pass a thousand-pattern scan over every event line and every spooled byte — and the byte-stream
    /// redactor holds a carry suffix as long as its longest pattern. A run legitimately injects a handful; 64 is far
    /// above any real launch and far below the point where the scan costs anything. The overflow is dropped SILENTLY:
    /// naming the dropped variables would put author-chosen secret names into the log, which is the sort of thing this
    /// file exists to prevent.
    ///
    /// <para>It bounds what an AUTHOR can push in, which is why <see cref="WithMcpRunToken"/> adds the run's own minted
    /// token beyond it: that one is this launch's, exactly one per run, and dropping it would leak a live capability.</para>
    /// </summary>
    internal const int MaximumNeedles = 64;

    /// <summary>
    /// Name fragments that mark an injected value a SECRET — applied to an <see cref="AgentTask.Environment"/> entry's
    /// variable name and to a base URL's query-parameter name alike. Both are opaque name/value string pairs with no
    /// per-entry secret flag for the executor to read, so the name is the only signal available — and it is the signal
    /// that separates the token from the values beside it that must stay READABLE: the gateway base URL, the model
    /// tier pins, an <c>api-version</c> stamp. Those are what an operator reads the error FOR, and masking a low-entropy
    /// one would shred every unrelated line that happens to carry it (an <c>api-version</c> date also matches every
    /// timestamp of that day).
    ///
    /// <para>Substring matching over a deliberately WIDE list: a marker earns its place by naming a carrier that
    /// authenticates something (<c>PASSPHRASE</c> on a signing key, <c>PWD</c> on <c>MYSQL_PWD</c>, <c>DSN</c> and
    /// <c>CONNECTION_STRING</c> on a database URL with its password inline, <c>COOKIE</c> on a session, <c>WEBHOOK</c>
    /// on a URL whose path IS the capability). The known cost is a false-positive class — <c>KEY_ID</c>,
    /// <c>TOKEN_LIMIT</c>, an injected <c>PWD</c> — whose values are masked though they are not secret. That is the
    /// side to err on: a masked path is a garbled line an operator can work around, an unmasked token is a leak that
    /// the append-only log can never take back.</para>
    /// </summary>
    private static readonly string[] SecretNameMarkers =
        ["KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "PASSPHRASE", "PWD", "CREDENTIAL", "AUTH", "COOKIE", "DSN", "CONNECTION_STRING", "CONNSTR", "PRIVATE", "WEBHOOK"];

    /// <summary>Whether a name — an env variable's or a URL query parameter's — marks its value a secret.</summary>
    private static bool MarksASecret(string name) => SecretNameMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The run's redactor, built from EVERY secret this launch injects into the child process — not the model key
    /// alone. Three carriers: the decrypted api key; the credential parts embedded in the base URL (a gateway that
    /// authenticates by <c>user:key@host</c> or <c>?api-key=</c>); and each injected env value whose name marks it
    /// secret (an author-supplied git token, an MCP secret an agent definition carries). All three reach the same
    /// child and come back through the same failure modes — a 401 body, an init banner, a clone error — into text
    /// this run PERSISTS: <c>AgentRun.Error</c> (journal card, Room, supervisor prompt), the append-only event log,
    /// and the diagnostic records.
    ///
    /// <para>De-duplicated, with the env-derived needles ordinal-sorted, so WHICH needles survive
    /// <see cref="MaximumNeedles"/> is a function of the SET and never of dictionary enumeration order — and so the
    /// credential's own needles, added first, can never be the ones evicted. <see cref="SecretRedactor.Fingerprint"/>
    /// imposes its own total order, so the digest does not depend on this one.</para>
    ///
    /// <para>DEPLOY-WINDOW DRIFT (accepted, fail-safe): the fingerprint is a function of the needle RULE as well as of
    /// the secrets. A run launched by a worker on the previous build stamped a narrower set on its handle (the api key
    /// alone); a worker on this build re-attaching to it rebuilds the wider set and reads a MISMATCH, so it completes
    /// that run from the exit marker with a recorded capture gap instead of re-tailing the spool. That is the safe
    /// direction — never a leak, only a lost native log — and it is bounded to the runs in flight across one deploy.
    /// Any future change to the needle rule (a new marker, a different cap) has the same one-deploy cost.</para>
    ///
    /// <para>The needles are never logged — they only ever reach the redactor.</para>
    /// </summary>
    internal static SecretRedactor BuildRunRedactor(IReadOnlyDictionary<string, string> injectedEnv, ResolvedModelCredential? credential)
    {
        var needles = new List<string>();

        if (credential?.ApiKey is { } apiKey) needles.Add(apiKey);

        needles.AddRange(UrlEmbeddedSecrets(credential?.BaseUrl));

        needles.AddRange(injectedEnv.Where(entry => MarksASecret(entry.Key)).Select(entry => entry.Value).OrderBy(value => value, StringComparer.Ordinal));

        var usable = needles.Where(needle => needle.Length >= MinimumNeedleLength).Distinct(StringComparer.Ordinal).Take(MaximumNeedles).ToList();

        return usable.Count == 0 ? SecretRedactor.None : new SecretRedactor(usable);
    }

    /// <summary>
    /// A redactor widened by the run's per-run MCP capability token — the one secret the launch mints AFTER the
    /// credential resolve, so <see cref="BuildRunRedactor"/> cannot see it. A no-op when the run has no token (no
    /// endpoint opened → nothing was injected) or when the token is too short to be a needle, which keeps the
    /// launch-side and re-attach-side fingerprints equal for exactly the runs that carry one.
    /// </summary>
    private static SecretRedactor WithMcpRunToken(SecretRedactor redactor, string? mcpRunToken) =>
        mcpRunToken is { Length: >= MinimumNeedleLength } token ? redactor.With([token]) : redactor;

    /// <summary>
    /// The credential parts embedded IN a base URL — every userinfo segment, and each query value whose parameter
    /// name <see cref="MarksASecret">marks it a secret</see> — in both the raw and the percent-decoded spelling,
    /// since a CLI may echo either. Userinfo is credential material by construction, so all of it is a needle; a
    /// query string is not, so it is filtered by name. The URL itself is never a needle: its host is the fact that
    /// says WHICH endpoint answered, which is the point of surfacing the error at all.
    ///
    /// <para>ACCEPTED RESIDUAL: a gateway that carries its key in the URL PATH (<c>https://gw.example/v1/&lt;key&gt;/chat</c>)
    /// is not covered. A path segment has no name to read, so the only rules available are "mask every segment" —
    /// which strikes the route an operator reads the error for, and every <c>/v1/</c> beside it — or a shape guess,
    /// which is a fresh leak the moment a gateway picks a shape it doesn't match. Userinfo and a marked query
    /// parameter are self-declaring; a path segment is not, so it stays out until a credential can say so itself.</para>
    /// </summary>
    private static IEnumerable<string> UrlEmbeddedSecrets(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)) yield break;

        foreach (var part in parsed.UserInfo.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return part;
            yield return Uri.UnescapeDataString(part);
        }

        foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');

            if (separator < 0 || !MarksASecret(pair[..separator])) continue;

            yield return pair[(separator + 1)..];
            yield return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }
    }

    /// <summary>Re-persist the run's stored task with its RESOLVED model filled, so the live projection shows what an "auto" run actually dispatches from the moment it starts (mirrors the harness-reconciliation write). The task is the ORIGINAL (no injected secret env) with only <see cref="AgentTask.Model"/> set, so serializing it is safe.</summary>
    private async Task PersistResolvedModelAsync(Guid agentRunId, AgentTask taskWithModel, CancellationToken cancellationToken) =>
        await _db.AgentRun.Where(r => r.Id == agentRunId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TaskJson, JsonSerializer.Serialize(taskWithModel, AgentJson.Options)), cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Stamp the run's autonomy tier onto the sandbox spec's memory + cpu ceilings, so the durable launch has something
    /// to build this run's cgroup-v2 cap from. Applied HERE rather than inside each <c>IAgentHarness.BuildInvocation</c>
    /// for the same reason the tier clamp is applied at one choke point: this is the single place every harness's
    /// invocation passes through (the launch AND every revise round), so a harness added later is capped without
    /// touching it, and no harness can forget. It is a pure <c>with</c> on the built spec, like
    /// <see cref="ApplyEgressPolicy"/> above it.
    ///
    /// <para><paramref name="hostMemoryBudgetMb"/> is the operator's per-run host budget, which can only narrow the
    /// tier's committed memory row. The two ceilings are enforced ONLY by a runner with cgroup-v2 delegation (the
    /// durable local runner on an operator-delegated cgroup root); on any other runner or host they are carried and
    /// ignored, exactly as <see cref="SandboxSpec.MaxMemoryMb"/> documents.</para>
    /// </summary>
    internal static SandboxSpec ApplyResourceCeilings(SandboxSpec spec, AgentAutonomyLevel autonomy, int? hostMemoryBudgetMb)
    {
        var ceilings = AgentAutonomyPolicy.Ceilings(autonomy, hostMemoryBudgetMb);

        return spec with { MaxMemoryMb = ceilings.MemoryMb, MaxCpuPercent = ceilings.CpuPercent };
    }

    /// <summary>The harness invocation with BOTH of the executor's own spec post-processings applied — the egress posture and the tier's resource ceilings. One name so the launch and each revise round cannot drift apart on which hardening they got.</summary>
    private static SandboxSpec HardenSpec(SandboxSpec spec, AgentTask task, string? modelBaseUrl, string? modelProvider, WorkspaceProvisionRequest? workspace) =>
        ApplyResourceCeilings(ApplyEgressPolicy(spec, task.Permissions, modelBaseUrl, modelProvider, workspace), task.Autonomy, RuntimeSettings.Current.AgentMemoryCeilingMb);

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
    private async Task<bool> AbortIfParentTerminalAsync(Guid runId, Guid teamId, Guid? workflowRunId, long claimedEpoch, CancellationToken cancellationToken)
    {
        if (workflowRunId is not { } parentId) return false;   // standalone run — no parent to gate on, proceed unchanged

        var parentStatus = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == parentId)
            .Select(r => (WorkflowRunStatus?)r.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (parentStatus is not (WorkflowRunStatus.Cancelled or WorkflowRunStatus.Failure or WorkflowRunStatus.Success)) return false;   // live parent (or absent) — proceed unchanged

        _logger.LogInformation("Agent run {RunId}: parent workflow run {ParentId} is terminal ({Status}) at the claim point; cancelling instead of launching a sandbox", runId, parentId, parentStatus);

        await CompleteAndNotifyAsync(runId, teamId, new AgentRunResult { Status = AgentRunStatus.Cancelled, ExitReason = "parent-terminal", Error = ParentTerminalAtClaimError }, claimedEpoch, cancellationToken).ConfigureAwait(false);

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

    private async Task<AgentRunResult> RunHarnessAsync(HarnessRunContext context, CancellationToken cancellationToken)
    {
        var folder = context.Harness.CreateFolder();   // BOUNDED: the harness's OWN reductions, not the run's events — a long run must not be able to exhaust the heap here
        var facts = AgentRunFacts.For(context.Harness);   // the three facts a forced terminal reports without folding, read with THIS harness's declared spellings (or the fallback union when it declares none)
        var writer = new BufferedEventWriter(_runs, context.RunId);   // batches the DB inserts; flushed at each spool checkpoint + once at the end
        var native = await OpenNativeCaptureAsync(context, cancellationToken).ConfigureAwait(false);   // G1: the lossless frame plane, dual-written beside the log; a plane that won't open leaves this path unchanged

        async Task PersistAsync(string line, SandboxOutputFrame? output)
        {
            var redactedLine = context.Redactor.Redact(line);

            // Capture the faithful transcript FIRST — redact the raw line, then keep it whether or not ParseEvents
            // surfaces any event. ParseEvents drops blank/unrecognized lines; the transcript keeps them so a replay
            // is exact. Redacted before it's held, so no secret reaches the offloaded artifact.
            await context.Transcript.AppendLineAsync(redactedLine, cancellationToken).ConfigureAwait(false);

            // The redacted frame becomes its own durable record BEFORE the harness is asked to interpret it, so a line
            // ParseEvents DROPS is still recorded (which is the whole point — the normalized log has no row for a
            // native class the adapter never learned). The pump owns the parse from here, and owns it TRANSPARENTLY:
            // a parser that throws gets its record marked normalization-failed and the throw is then re-raised, so
            // this loop fails exactly where `foreach (var e in Harness.ParseEvents(line))` used to.
            var frame = output is { } source
                ? await native.CaptureAsync(source, redactedLine, context.Harness, cancellationToken).ConfigureAwait(false)
                : await native.CaptureAsync(line, redactedLine, context.Harness, cancellationToken).ConfigureAwait(false);

            // ONE native line can carry several content blocks (reasoning + tool_use + text) → several events, in
            // stream order. Each is redacted BEFORE the append-only log freezes it (the log can't be edited later).
            foreach (var normalized in frame.Events)
            {
                var redacted = Redact(normalized, context.Redactor);

                await writer.BufferAsync(redacted, cancellationToken).ConfigureAwait(false);   // buffered — one batched INSERT per spool checkpoint, not one per line

                native.Project(frame, redacted);   // the projection cites the exact frame it came from, and never replaces it
                folder.Add(redacted);   // O(1) in-memory reduction; the full ordered log lives durably in agent_run_event
                facts.Add(redacted);
            }
        }

        Task PersistLineAsync(string line) => PersistAsync(line, null);
        Task PersistFrameAsync(SandboxOutputFrame frame) => PersistAsync(frame.Text, frame);

        // The heartbeat is owned by ExecuteAsync (it spans the whole run, including the completion tail), so
        // streaming here just emits events — a quiet step's liveness is kept fresh by that outer heartbeat. The
        // redactor's fingerprint is stamped onto the durable handle so a re-attach can prove it rebuilt the SAME
        // key before re-tailing the spool (a rotated/deleted key → marker-only, never an unmaskable leak). The MCP
        // token rides the handle too so a re-attach re-binds the SAME socket+token the agent's declaration carries.
        var sandbox = await RunSandboxAsync(context, PersistLineAsync, PersistFrameAsync, new HarnessSinks(writer, native), cancellationToken).ConfigureAwait(false);

        // Final flush: the durable runner's terminal-drain paths (CompleteFromSpool/Timeout/Vanished) deliver the last
        // lines WITHOUT a trailing checkpoint, so anything buffered after the last checkpoint must be flushed here
        // before the result is folded + the run completes. (A no-op when the buffer is already empty.)
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Same terminal drain for the frame plane, then close its process attempt. A forced terminal observed NO exit
        // code (the runner reports -1 because it killed the process), so that attempt is recorded Lost with a reason
        // rather than as an exit nobody saw. Both halves are best-effort: neither can change the run's own outcome.
        await native.CloseAsync(ObservedExitCode(sandbox), cancellationToken).ConfigureAwait(false);

        ReportUnestablishedFacts(context.Harness, facts, context.RunId);

        // Events are already redacted, so a result the harness folds from them (summary / error) is redacted too; the
        // diagnostics are the one input that arrives raw, so they are masked here. The faithful raw transcript is NOT
        // attached here: it belongs to the whole run (every revise round streams into the same spool) and is
        // sealed/reopened through bounded storage streams at the end by AttachTranscriptAsync.
        return MapSandboxResult(Redacted(sandbox, context.Redactor), folder, facts);
    }

    /// <summary>
    /// Say it when a run's shared facts came back null from a harness that never declared where they live — the one
    /// failure mode this whole seam exists for, and the one nothing downstream rejects: <see cref="BuildReviseTask"/>
    /// reads a null session id as "cold" and silently restarts the conversation on every warm retry, and a null token
    /// usage leaves the run priced at nothing. A harness that DID declare stays quiet even when a fact is null, because
    /// then the null is what its own table says the stream carried (Codex names no model in-stream and recovers it via
    /// <see cref="IAgentTranscriptModelSource"/>) — see <see cref="AgentRunFacts.UnestablishedFacts"/>.
    /// </summary>
    private void ReportUnestablishedFacts(IAgentHarness harness, AgentRunFacts facts, Guid runId)
    {
        if (facts.UnestablishedFacts.Count == 0) return;

        _logger.LogWarning("Agent run {RunId}: harness {Harness} declares no run-fact keys (IAgentHarnessRunFactKeys) and the fallback key table found no {UnestablishedFacts} in its stream; a missing session id makes every warm retry cold-start and a missing token usage prices the run at nothing", runId, harness.Kind, string.Join(", ", facts.UnestablishedFacts));
    }

    /// <summary>
    /// Open this round's frame-capture stream. The runner locator is the ROUND's spool key — the backend-owned address
    /// this process's output is reachable at, and the only thing about it that is knowable before launch; a pid belongs
    /// to the runner and lands with the durable handle, not here.
    /// </summary>
    private Task<AgentNativeRecordPump> OpenNativeCaptureAsync(HarnessRunContext context, CancellationToken cancellationToken) =>
        AgentNativeRecordPump.OpenAsync(_nativeRecords, new NativeRecordCaptureRequest
        {
            TeamId = context.TeamId,
            AgentRunId = context.RunId,
            HarnessTypeKey = AgentNativeRecordPump.HarnessTypeKeyOf(context.Harness),
            ModelCallObservationCoverage = AgentNativeRecordPump.ModelCallObservationCoverageOf(context.Harness),
            RunnerKind = context.Runner.Kind,
            RunnerLocatorJson = JsonSerializer.Serialize(new { spoolKey = context.SpoolKey }, AgentJson.Options),
            WorkerFenceEpoch = context.WorkerFenceEpoch,

            // The executor's pump reads ONE stream: the runner delivers stdout line by line and buffers stderr
            // separately, so labelling these frames anything else would be a claim the delivery cannot support.
            Channel = NativeRecordChannel.Stdout,
        }, context.Redactor, _logger, cancellationToken);

    /// <summary>
    /// Re-open this run's frame capture on the RESUMED stream of the process it is re-attaching to. The plane re-enters
    /// the recorded process rather than appending a second one for it, and the cursor handed to it is the SAME
    /// <see cref="SandboxHandle.StdoutOffset"/> the observation below is about to resume reading at — so a line this
    /// re-attach is re-delivered is recorded at the position it already occupies, and the plane can tell it apart from
    /// one the process has not produced before. The runner locator is this handle's own spool directory, which is the
    /// address the resumed observation actually reads.
    /// </summary>
    private Task<AgentNativeRecordPump> OpenResumedCaptureAsync(ReattachFoldContext context, SecretRedactor redactor, CancellationToken cancellationToken) =>
        AgentNativeRecordPump.OpenAsync(_nativeRecords, new NativeRecordCaptureRequest
        {
            TeamId = context.TeamId,
            AgentRunId = context.RunId,
            HarnessTypeKey = AgentNativeRecordPump.HarnessTypeKeyOf(context.Harness),
            ModelCallObservationCoverage = AgentNativeRecordPump.ModelCallObservationCoverageOf(context.Harness),
            RunnerKind = context.Handle.Kind,
            RunnerLocatorJson = JsonSerializer.Serialize(new { spoolDirectory = context.Handle.SpoolDirectory }, AgentJson.Options),
            WorkerFenceEpoch = context.WorkerFenceEpoch,
            Channel = NativeRecordChannel.Stdout,
            Resume = true,
            ResumeSourceOffset = context.Handle.StdoutOffset,
        }, redactor, _logger, cancellationToken);

    /// <summary>
    /// The exit code the observer actually SAW, or null when nothing did. A timeout or a stall is a process this side
    /// killed, and the runner reports -1 for it — recording that as an observed exit would turn "we do not know how it
    /// ended" into a fact, which is precisely the distinction the attempt's Exited/Lost states exist to keep.
    /// </summary>
    private static int? ObservedExitCode(SandboxResult sandbox) =>
        sandbox.Status is SandboxStatus.TimedOut or SandboxStatus.Stalled ? null : sandbox.ExitCode;

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
    private async Task<SandboxResult> RunSandboxAsync(HarnessRunContext context, Func<string, Task> persistLine, Func<SandboxOutputFrame, Task> persistFrame, HarnessSinks sinks, CancellationToken cancellationToken)
    {
        if (context.Runner is ISandboxDurableRunner durable)
            return await RunDurableAsync(context, durable, persistFrame, sinks, cancellationToken).ConfigureAwait(false);

        // Non-durable fallback (no spool/checkpoint): the writer's size cap + the caller's final flush drain it.
        // It applies no OS confinement at all, which is a posture in its own right — recorded so a reader is told
        // "nothing was attempted" rather than being left to assume the sandbox severed something.
        await RecordConfinementAsync(context.RunId, new SandboxConfinement { Outcome = SandboxConfinementOutcome.NotApplicable }, cancellationToken).ConfigureAwait(false);

        return await RunAndStreamAsync(context.Runner, context.Spec, persistLine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persist, once per launch, what confinement the run actually got. Written beside the handle rather than inside
    /// it because the spool reaper nulls the handle 24h after a terminal run, and a run's posture must stay readable
    /// for as long as its journal is. A runner that stamps nothing (an older handle) records nothing — the readers'
    /// no-record branch then keeps the hedged wording instead of inventing an enforced one.
    /// </summary>
    private async Task RecordConfinementAsync(Guid runId, SandboxConfinement? confinement, CancellationToken cancellationToken)
    {
        if (confinement is null) return;

        await _runs.SetSandboxConfinementAsync(runId, JsonSerializer.Serialize(confinement, AgentJson.Options), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Launch the run to its durable spool, persist the returned handle (keyed by the run id) BEFORE
    /// observing, then attach + tail. Persisting first is what lets the reconciler recover this run if this
    /// observer dies mid-tail. On a host-shutdown cancel the attach stops observing WITHOUT killing the
    /// process (leaving the run Running for re-attach/recovery); only the spec timeout terminates it.
    /// </summary>
    private async Task<SandboxResult> RunDurableAsync(HarnessRunContext context, ISandboxDurableRunner durable, Func<SandboxOutputFrame, Task> persistFrame, HarnessSinks sinks, CancellationToken cancellationToken)
    {
        // Stamp the injected-key fingerprint + the MCP run token onto the handle at launch. The fingerprint lets a
        // re-attach verify it rebuilt the same redactor before re-tailing (rotated/deleted credential → marker-only);
        // the token lets it RE-OPEN the endpoint with the SAME socket+token the agent's declaration file already holds.
        // The spool key is round-scoped (ReviseSpoolKey) — a revise round must never inherit a finished spool's exit marker.
        // The workspace directory + base SHA (primary repo only) let a re-attach capture the agent's diff via
        // IWorkspacePathCapture even though the live IWorkspaceHandle that prepared the clone died with this worker.
        // The progress-lease directory is RUN-scoped (not round-scoped, like the spool key): it is the same path the
        // run's platform endpoint renews from LocalProcessRunner.ProgressLeaseFor, so the observer's no-progress
        // watchdog reads exactly the lease the endpoint writes — including across a worker restart, which re-attaches to
        // this handle. Resolved from the layout owner here for the same reason the MCP socket path is (MintMcpConnect).
        // This hard-codes the LOCAL runner's layout: ISandboxDurableRunner.LaunchAsync is never handed the run id, so a
        // second durable runner cannot resolve a run-scoped lease of its own — adopting one means giving the interface
        // the run id (or the lease directory) first. A null directory means "no lease".
        var handle = (await durable.LaunchAsync(context.Spec, context.SpoolKey, cancellationToken).ConfigureAwait(false)) with
        {
            InjectedKeyFingerprint = context.Redactor.Fingerprint, McpRunToken = context.McpToken,
            WorkspaceDirectory = context.WorkspaceDirectory, WorkspaceBaseSha = context.WorkspaceBaseSha,
            ProgressLeaseDirectory = LocalProcessRunner.ProgressLeaseDirectoryFor(context.RunId),
        };
        handle = EnsureLogCaptureHandle(handle, durable);

        await _runs.SetRunnerHandleAsync(context.RunId, JsonSerializer.Serialize(handle, AgentJson.Options), cancellationToken).ConfigureAwait(false);
        await RecordConfinementAsync(context.RunId, handle.Confinement, cancellationToken).ConfigureAwait(false);
        var capture = await OpenLogCaptureAsync(new LogCaptureContext(context.TeamId, context.RunId, context.ActorId, context.WorkerFenceEpoch, context.Redactor), durable, handle, cancellationToken).ConfigureAwait(false);
        if (capture.Handle != handle)
            await _runs.SetRunnerHandleAsync(context.RunId, JsonSerializer.Serialize(capture.Handle, AgentJson.Options), cancellationToken).ConfigureAwait(false);

        // Checkpoint the advancing spool offset onto the handle as we tail, so a backend restart mid-run can
        // re-attach (ReattachAsync) and resume from here instead of re-emitting the whole spool.
        var result = await capture.ObserveAsync((capturedHandle, token) => durable.AttachAsync(capturedHandle, (frame, _) => persistFrame(frame), token, CheckpointHandleOffset(context.RunId, capturedHandle, sinks)), cancellationToken).ConfigureAwait(false);

        // The stdout stream's terminal-drain frames and the checkpoint they complete must be durable BEFORE the
        // diagnostics fold resumes from that checkpoint — two openings of one execution advance one reduction, and
        // they may only do it in sequence.
        await sinks.Frames.FlushAsync(cancellationToken).ConfigureAwait(false);
        await RecordDiagnosticsAsync(new DiagnosticCapture(context.TeamId, context.RunId, context.WorkerFenceEpoch, context.Harness, context.Redactor, capture.Handle, durable), cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Most frames of a harness's diagnostics that one round records. The drain joins the flushes that already sat
    /// between a round's terminal <see cref="SandboxResult"/> and the mapping of it, and unlike them it would scale
    /// with the run: one durable row per stderr line, and a <c>set -x</c> trace or a crash loop can spool hundreds of
    /// megabytes — the round's outcome sitting unwritten behind millions of INSERTs. This ceiling is what bounds the
    /// ROW COUNT of that stretch. Frames past it are not recorded; the whole stream stays on the run's spool, which is
    /// where it lived before this plane existed.
    /// </summary>
    private const int MaxDiagnosticFrames = 2_000;

    /// <summary>
    /// Most SOURCE bytes of those diagnostics that one round reads. It is not a bound on what is WRITTEN: the read is decoded as UTF-8, so a byte that is not valid UTF-8 becomes a replacement character costing three bytes, and a pathological stderr can therefore write up to three times this figure.
    /// A frame ceiling alone does not give one: each frame carries its line inline into a <c>text</c> column with no
    /// length of its own, so two thousand frames of a megabyte each is two gigabytes written between the round's
    /// terminal <see cref="SandboxResult"/> and the mapping of it — a bound in rows and none at all in bytes, which is
    /// the dimension the delay is actually paid in. The two together are what make the stretch a constant.
    ///
    /// <para>It does not decide what happens to a line longer than one of the reader's own passes: that line is
    /// delivered cut and recorded as a partial whatever this is set to, because forward progress is the reader's
    /// guarantee. What this bounds is how many bytes of such a stream one round pays for.</para>
    /// </summary>
    private const int MaxDiagnosticBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Record the harness's OWN diagnostics — its stderr — as native records on their own stream.
    ///
    /// <para><b>Why they are not parsed.</b> A harness parser is written against that harness's stdout protocol. A
    /// diagnostic that happened to resemble a protocol frame would be normalized into a semantic event and projected as
    /// something the harness never said, so the frames land with no parse attempted at all
    /// (<see cref="NativeRecordNormalization.NotParsed"/>) and nothing here reaches the normalized event log.</para>
    ///
    /// <para><b>Why a RESUMED opening.</b> The process being drained is the one the stdout opening already recorded, so
    /// re-entering it records against the SAME process attempt on a stream of its own — a launching opening would
    /// append a second process row for one process. It is also why nothing here closes the attempt: one process is
    /// closed once, by the round that owns it.</para>
    ///
    /// <para><b>Where it sits, and why that is survivable.</b> This runs on the completion path: the round's terminal
    /// <see cref="SandboxResult"/> already exists, and <see cref="MapSandboxResult"/> has not yet turned it into the
    /// run's outcome. Two properties, together, are what keep that placement from costing a run its result.
    /// It CANNOT THROW — every exception is contained here, <see cref="OperationCanceledException"/> included, so a
    /// worker tear-down landing inside the drain never unwinds past a computed-but-unmapped result; the round returns
    /// from here and carries on into exactly the statement that would have ended it had the tear-down landed a moment
    /// earlier. That containment is the opposite of what <see cref="AgentNativeRecordPump.CaptureAsync"/> does with a
    /// parser's throw, and deliberately: the parser already failed the run before this plane existed, while every line
    /// of this method is work that did not exist, and work that did not exist may not decide a round.
    /// And it is BOUNDED IN BOTH DIMENSIONS — at most <see cref="MaxDiagnosticFrames"/> frames AND at most
    /// <see cref="MaxDiagnosticBytes"/> source bytes, one bounded read pass at a time — so the stretch it adds before
    /// the mapping is a constant of this executor in rows and in bytes, and not a function of the run's stderr volume
    /// in either. Reaching either budget is LOGGED: the drain answers where it stopped, and a drain that began at 0
    /// answers exactly what it read, so "recorded a whole stream" is distinguishable here from "recorded a prefix and
    /// parked the rest". What it costs the round is that bounded delay and nothing else: it returns nothing, and the
    /// status, exit reason and error text <see cref="MapSandboxResult"/> then computes are exactly what they are with
    /// no plane deployed.</para>
    ///
    /// <para><b>A diagnostic the reader had to cut.</b> A single line longer than one of the reader's passes is
    /// delivered cut rather than stopping the drain, and is recorded as a NON-FINAL frame — the honest record of a
    /// frame this side holds half of. Recording it as two whole frames would put two diagnostics in the durable stream
    /// where the harness wrote one.</para>
    /// </summary>
    private async Task RecordDiagnosticsAsync(DiagnosticCapture capture, CancellationToken cancellationToken)
    {
        if (_nativeRecords is null || capture.Durable is not ISandboxDurableDiagnosticSource diagnostics) return;

        try
        {
            var pump = await AgentNativeRecordPump.OpenAsync(_nativeRecords, DiagnosticRequest(capture), capture.Redactor, _logger, cancellationToken).ConfigureAwait(false);

            if (!pump.IsCapturing) return;

            var delivered = 0;

            async Task RecordAsync(SandboxDiagnosticLine line, CancellationToken token)
            {
                delivered++;
                await pump.CaptureDiagnosticAsync(line.Text, capture.Redactor.Redact(line.Text), line.IsComplete, token).ConfigureAwait(false);
            }

            var budget = new SandboxDiagnosticBudget { MaxLines = MaxDiagnosticFrames, MaxBytes = MaxDiagnosticBytes };
            var parked = await diagnostics.DrainDiagnosticsAsync(capture.Handle, 0, budget, RecordAsync, cancellationToken).ConfigureAwait(false);

            await pump.FlushAsync(cancellationToken).ConfigureAwait(false);

            // The drain began at 0, so what it answers IS the number of source bytes it read — comparing it with the
            // byte budget is how a drain that stopped at a budget is told apart from one that reached the end. Counted
            // as DELIVERED rather than recorded: a line the pump drops as already below its recorded head still cost
            // the budget, and the budget is what this reports on.
            if (delivered >= MaxDiagnosticFrames || parked >= MaxDiagnosticBytes)
                _logger.LogWarning("Agent run {RunId}: drained {Lines} diagnostic lines covering the first {Bytes} bytes of the harness's stderr and stopped at the budget; anything past that stays on the run's spool and was not made durable", capture.RunId, delivered, parked);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Agent run {RunId}: the harness's diagnostics could not be recorded; the run completes exactly as it does where the native record plane is not deployed", capture.RunId);
        }
    }

    /// <summary>
    /// The diagnostics opening: this process again, on the <see cref="NativeRecordChannel.Stderr"/> channel, reading
    /// its source from the beginning. Zero is the honest cursor because a diagnostic stream is drained at a terminal
    /// rather than tailed, so there is no observation position to resume from; the plane's recorded head — scoped to
    /// this attempt AND this channel — is what keeps a second drain of the same process from recording a line twice.
    /// </summary>
    private static NativeRecordCaptureRequest DiagnosticRequest(DiagnosticCapture capture) => new()
    {
        TeamId = capture.TeamId,
        AgentRunId = capture.RunId,
        HarnessTypeKey = AgentNativeRecordPump.HarnessTypeKeyOf(capture.Harness),
        ModelCallObservationCoverage = AgentNativeRecordPump.ModelCallObservationCoverageOf(capture.Harness),
        RunnerKind = capture.Handle.Kind,
        RunnerLocatorJson = JsonSerializer.Serialize(new { spoolDirectory = capture.Handle.SpoolDirectory }, AgentJson.Options),
        WorkerFenceEpoch = capture.WorkerFenceEpoch,
        Channel = NativeRecordChannel.Stderr,
        Resume = true,
        ResumeSourceOffset = 0,
    };

    private SandboxHandle EnsureLogCaptureHandle(SandboxHandle handle, ISandboxDurableRunner durable)
    {
        if (_logCapture == null || durable is not ISandboxDurableLogSource || handle.AgentRunLogCaptureSessionId is { } sessionId && sessionId != Guid.Empty) return handle;
        return handle with { AgentRunLogCaptureSessionId = Guid.NewGuid() };
    }

    private async Task<IAgentRunLogCaptureSession> OpenLogCaptureAsync(LogCaptureContext context, ISandboxDurableRunner durable, SandboxHandle handle, CancellationToken cancellationToken)
    {
        if (_logCapture == null || durable is not ISandboxDurableLogSource source) return new PassthroughLogCaptureSession(handle);
        try
        {
            return await _logCapture.OpenAsync(new AgentRunLogCaptureOpenRequest
            {
                TeamId = context.TeamId, AgentRunId = context.RunId, ActorId = context.ActorId,
                WorkerFenceEpoch = context.WorkerFenceEpoch, Handle = handle, Source = source, Redactor = context.Redactor,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Agent run {RunId} shadow log capture could not open; sandbox observation remains unchanged", context.RunId);
            return new PassthroughLogCaptureSession(handle);
        }
    }

    /// <summary>
    /// The onCheckpoint callback for <see cref="ISandboxDurableRunner.AttachAsync"/>: FLUSH the buffered events for the
    /// poll's lines, THEN persist the advanced spool offset onto the handle. The flush-before-offset ordering is the
    /// durability invariant — the persisted offset must never run ahead of flushed events, so a re-attach at worst
    /// re-emits the last batch (never loses a line). A pure jsonb UPDATE for the offset; never blocks completion.
    /// </summary>
    private Func<long, CancellationToken, Task> CheckpointHandleOffset(Guid runId, SandboxHandle handle, HarnessSinks sinks) =>
        async (offset, ct) =>
        {
            await sinks.Events.FlushAsync(ct).ConfigureAwait(false);
            await sinks.Frames.FlushAsync(ct).ConfigureAwait(false);   // the frame plane rides the same checkpoint — best-effort, so a refused frame flush stops capture for the round rather than holding the offset back
            await _runs.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle with { StdoutOffset = Math.Max(handle.StdoutOffset, offset) }, AgentJson.Options), ct).ConfigureAwait(false);
        };

    /// <summary>
    /// The two durable sinks one harness round streams into: the normalized event log and the native-frame plane. One
    /// record rather than two parameters because they are flushed together, at the same checkpoints, for the same
    /// reason — and because threading a second one through the observe path put its signatures over the parameter cap.
    /// </summary>
    private sealed record HarnessSinks(BufferedEventWriter Events, AgentNativeRecordPump Frames);

    private sealed record HarnessRunContext
    {
        public required Guid RunId { get; init; }
        public required Guid TeamId { get; init; }
        public required Guid ActorId { get; init; }
        public required long WorkerFenceEpoch { get; init; }
        public required IAgentHarness Harness { get; init; }
        public required ISandboxRunner Runner { get; init; }
        public required SandboxSpec Spec { get; init; }
        public string? McpToken { get; init; }
        public required SecretRedactor Redactor { get; init; }
        public required string SpoolKey { get; init; }

        /// <summary>The run's transcript accumulator, shared across every revise round (the seam between them is marked on it), so the whole run's faithful stream lands in ONE record without any round retaining its own copy.</summary>
        public required AgentTranscriptSpool Transcript { get; init; }
        public string? WorkspaceDirectory { get; init; }
        public string? WorkspaceBaseSha { get; init; }
    }

    private sealed record ReattachFoldContext
    {
        public required Guid RunId { get; init; }
        public required Guid TeamId { get; init; }
        public required Guid ActorId { get; init; }
        public required long WorkerFenceEpoch { get; init; }
        public required ISandboxDurableRunner Durable { get; init; }
        public required SandboxHandle Handle { get; init; }
        public required AgentTask Task { get; init; }
        public required IAgentHarness Harness { get; init; }
    }

    private sealed record LogCaptureContext(Guid TeamId, Guid RunId, Guid ActorId, long WorkerFenceEpoch, SecretRedactor Redactor);

    /// <summary>What recording a round's diagnostics needs: the run it belongs to and the fence it speaks under, the harness and redactor its frames are captured with, and the launched process whose spooled stderr is the source. One record because both observe paths assemble it and it is well past the parameter cap.</summary>
    private sealed record DiagnosticCapture(Guid TeamId, Guid RunId, long WorkerFenceEpoch, IAgentHarness Harness, SecretRedactor Redactor, SandboxHandle Handle, ISandboxDurableRunner Durable);

    private sealed class PassthroughLogCaptureSession(SandboxHandle handle) : IAgentRunLogCaptureSession
    {
        public SandboxHandle Handle { get; } = handle;
        public Task<SandboxResult> ObserveAsync(Func<SandboxHandle, CancellationToken, Task<SandboxResult>> observer, CancellationToken cancellationToken) => observer(Handle, cancellationToken);
    }

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

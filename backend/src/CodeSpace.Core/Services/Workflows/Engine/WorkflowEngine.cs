using System.Text.Json;
using Autofac;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Jobs;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// The DAG walker. Frontier algorithm (not single-pass topo) so we can support branch routing,
/// fan-in joins, and iteration / resume without reshaping the loop. The high-level shape:
///
///   ready := { trigger node }
///   while ready not empty:
///     node := pop one
///     if node already fired in any state → skip
///     if node's incoming requirements not satisfied → wait for them
///     execute, persist, write outputs to scope, record routing hints
///     for each outgoing edge whose source-handle is allowed by the result's RoutingHints:
///         when target's other incoming edges all settled → push target onto ready
///     if successful Terminal → stop the run
///
/// Halts on Failure. Skip propagation is implicit — a node whose every incoming edge is
/// "dead" (source skipped, source failed, OR source's RoutingHints exclude this edge's
/// handle) becomes Skipped.
///
/// Concurrency: each run is independent. The DB context is scoped per-run so multiple engine
/// instances can process different runs in parallel without state collision.
/// </summary>
public sealed class WorkflowEngine : IWorkflowEngine, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly INodeRegistry _nodeRegistry;
    private readonly IVariableService _variableService;
    private readonly IRunRecordLogger _recordLogger;
    private readonly IPayloadRedactor _redactor;
    private readonly IArtifactStore _artifactStore;
    private readonly ILogger<WorkflowEngine> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICodeSpaceBackgroundJobClient _backgroundJobClient;
    private readonly ISubworkflowService _subworkflowService;
    private readonly IWorkflowResumeService _resumeService;
    private readonly IAgentRunService _agentRunService;
    private readonly IAgentDefinitionResolver _agentDefinitionResolver;
    // The run's lifetime scope — BeginLifetimeScope() per parallel node gives each its own DbContext +
    // record logger so concurrent nodes never share an EF change-tracker (see RunNodeInChildScopeAsync).
    private readonly ILifetimeScope _lifetimeScope;
    private readonly int _maxParallelism;

    /// <summary>
    /// Max nodes from one ready frontier run concurrently (each in its own DI scope). Env-overridable
    /// (Rule 8) so an operator can tune throughput, or pin to <c>1</c> to force fully-sequential
    /// execution; parsed once at construction, clamped to [1, <see cref="MaxParallelismCeiling"/>],
    /// default <see cref="DefaultMaxParallelism"/>.
    /// </summary>
    public const string MaxParallelismEnvVar = "CODESPACE_WORKFLOW_MAX_PARALLELISM";
    internal const int DefaultMaxParallelism = 8;
    internal const int MaxParallelismCeiling = 64;

    public WorkflowEngine(CodeSpaceDbContext db, INodeRegistry nodeRegistry, IVariableService variableService, IRunRecordLogger recordLogger, IPayloadRedactor redactor, IArtifactStore artifactStore, ILogger<WorkflowEngine> logger, ILoggerFactory loggerFactory, ICodeSpaceBackgroundJobClient backgroundJobClient, ISubworkflowService subworkflowService, IWorkflowResumeService resumeService, IAgentRunService agentRunService, IAgentDefinitionResolver agentDefinitionResolver, ILifetimeScope lifetimeScope)
    {
        _db = db;
        _nodeRegistry = nodeRegistry;
        _variableService = variableService;
        _recordLogger = recordLogger;
        _redactor = redactor;
        _artifactStore = artifactStore;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _backgroundJobClient = backgroundJobClient;
        _subworkflowService = subworkflowService;
        _resumeService = resumeService;
        _agentRunService = agentRunService;
        _agentDefinitionResolver = agentDefinitionResolver;
        _lifetimeScope = lifetimeScope;
        _maxParallelism = ParseMaxParallelism(Environment.GetEnvironmentVariable(MaxParallelismEnvVar));
    }

    /// <summary>Parse + clamp the max-parallelism env value. Unset / unparseable ⇒ the default; out-of-range ⇒ clamped. Pure + internal so it's unit-pinned (Rule 8).</summary>
    internal static int ParseMaxParallelism(string? raw) =>
        int.TryParse(raw, out var value) ? Math.Clamp(value, 1, MaxParallelismCeiling) : DefaultMaxParallelism;

    /// <summary>Resolve a loop body's effective max-parallelism: a per-loop override (clamped to [1, ceiling]) wins; null inherits the engine-wide value. Pure + internal so it's unit-pinned.</summary>
    internal static int ResolveBodyParallelism(int? loopOverride, int engineDefault) =>
        loopOverride is { } v ? Math.Clamp(v, 1, MaxParallelismCeiling) : engineDefault;

    public async Task ExecuteRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Atomic claim: Enqueued → Running.
        //
        // The dispatcher already transitioned the row Pending → Enqueued; we now claim
        // ownership atomically by flipping Enqueued → Running. Two workers racing on the
        // same runId (Hangfire retry + reconciler re-enqueue, or simply two workers in a
        // multi-replica deploy) cannot both succeed: Postgres's UPDATE...WHERE returns
        // rows-affected = 1 for one of them and 0 for the other. The loser bails without
        // touching the engine — this is the single-writer guarantee that no side-effecting
        // node ever fires twice for the same run.
        //
        // Rows-affected = 0 can mean several things, all of which are safe to silently skip:
        //   • Another worker is currently Running this run (the loser of a concurrent race)
        //   • The run is already terminal (Success / Failure / Cancelled)
        //   • The run is still Pending (a reconciler reverted after a failed earlier dispatch
        //     but Hangfire still had the old job — the row will be re-Enqueued by reconciler
        //     and we'll claim it on a subsequent worker pickup)
        //
        // We don't distinguish these cases here; the caller (Hangfire worker) treats
        // "engine returned" as the contract regardless.
        var startedAt = DateTimeOffset.UtcNow;
        var claimed = await _db.WorkflowRun
            .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Enqueued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, WorkflowRunStatus.Running)
                .SetProperty(r => r.StartedAt, startedAt), cancellationToken)
            .ConfigureAwait(false);

        if (claimed == 0)
        {
            var currentStatus = await _db.WorkflowRun.AsNoTracking()
                .Where(r => r.Id == runId)
                .Select(r => (WorkflowRunStatus?)r.Status)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Run {RunId} not claimable for execution (current status: {Status}); short-circuiting — no-double-execution guarantee enforced",
                runId, currentStatus);
            return;
        }

        // Post-claim work is wrapped in a try/catch so that ANY failure between claim and
        // walk (run load throwing, definition deserialisation throwing, hash mismatch,
        // snapshot persist throwing) lands the row in Failure rather than leaving it stuck
        // in Running forever — the reconciler's "abandoned Running" sweep would eventually
        // catch it, but operators see the failure immediately this way + the timeline gets
        // the run.failed ledger record with the specific exception message.
        try
        {
            await RunAfterClaimAsync(runId, startedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancel handling already happens INSIDE RunAfterClaimAsync (it writes the
            // Cancelled status + ledger). Re-throw so Hangfire records the job as cancelled.
            throw;
        }
        catch (Exception ex)
        {
            // Bootstrap-phase failure — load / hash-verify / scope-build / snapshot
            // persist threw. The walk's own try/catch never got a chance. Mark Failure
            // here so the run row reaches a terminal state.
            await MarkBootstrapFailureAsync(runId, startedAt, ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Everything between the atomic Enqueued→Running CAS and the walker's try/catch.
    /// Extracted so the outer <see cref="ExecuteRunAsync"/> can wrap THIS in a
    /// bootstrap-failure guard without nesting the walker's own catch clauses.
    /// </summary>
    private async Task RunAfterClaimAsync(Guid runId, DateTimeOffset engineStartedAt, CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(runId, cancellationToken).ConfigureAwait(false);
        await _recordLogger.RunStartedAsync(run.Id, cancellationToken).ConfigureAwait(false);

        var (definition, releaseHash) = await LoadDefinitionAndHashAsync(run, cancellationToken).ConfigureAwait(false);
        await _recordLogger.ReleaseLoadedAsync(run.Id, run.WorkflowVersion ?? 0, releaseHash, definition.Nodes.Count, definition.Edges.Count, cancellationToken).ConfigureAwait(false);

        // Fork on snapshot existence. First-run path builds scope from current variable state
        // + writes the snapshot. Replay path reads plain values from snapshot (frozen) and
        // re-resolves secrets from current (rotation safety).
        var isReplay = await _db.WorkflowRunVariable.AnyAsync(v => v.RunId == run.Id, cancellationToken).ConfigureAwait(false);
        NodeRunScope scope;
        if (isReplay)
        {
            scope = await BuildScopeForReplayAsync(run, definition, cancellationToken).ConfigureAwait(false);
            var snapshotCount = await _db.WorkflowRunVariable.CountAsync(v => v.RunId == run.Id, cancellationToken).ConfigureAwait(false);
            await _recordLogger.RunReplayedAsync(run.Id, run.ParentRunId, snapshotCount, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            scope = await BuildScopeFreshAndPersistSnapshotAsync(run, definition, releaseHash, cancellationToken).ConfigureAwait(false);
        }
        await _recordLogger.ScopeResolvedAsync(run.Id, scope.Wf.Count, scope.Team.Count, scope.Sys.Count, scope.SecretPaths.Count, cancellationToken).ConfigureAwait(false);

        await StartRunAsync(run, cancellationToken).ConfigureAwait(false);

        try
        {
            var walkerOutputs = await WalkGraphAsync(run, definition, scope, cancellationToken).ConfigureAwait(false);
            run.OutputsJson = JsonSerializer.Serialize(walkerOutputs);
            await CompleteRunAsync(run, WorkflowRunStatus.Success, error: null, cancellationToken).ConfigureAwait(false);
            await _recordLogger.RunCompletedAsync(run.Id, DateTimeOffset.UtcNow - engineStartedAt, outputsPresent: walkerOutputs.Count > 0, cancellationToken).ConfigureAwait(false);
        }
        catch (NodeFailureException ex)
        {
            await CompleteRunAsync(run, WorkflowRunStatus.Failure, ex.Message, cancellationToken).ConfigureAwait(false);
            await _recordLogger.RunFailedAsync(run.Id, ex.Message, DateTimeOffset.UtcNow - engineStartedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (RunSuspendedException)
        {
            // A node paused the run. The wait row + node.suspended record are already persisted
            // (SuspendNodeAsync) and the timer resume (if any) is scheduled. Flip the run to
            // Suspended and return WITHOUT completing — the resume signal will re-dispatch it and
            // the durable walker continues from the suspended node. NOT a terminal state.
            run.Status = WorkflowRunStatus.Suspended;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Now that the parent is COMMITTED Suspended, dispatch any sub-workflow child it staged.
            // Deferring the dispatch to here (not at suspend time) closes the race where a fast
            // child could finish and try to resume the parent before the parent was parked.
            await DispatchPendingSubworkflowChildAsync(run.Id, cancellationToken).ConfigureAwait(false);
            await DispatchPendingAgentRunAsync(run.Id, cancellationToken).ConfigureAwait(false);
            await DispatchPendingSupervisorAdvanceAsync(run.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowSecretLeakException ex)
        {
            // Contract violation — Terminal output referenced a Secret variable. Surface the
            // exact message (it names the node + path) so the operator can fix the wiring.
            _logger.LogError("Run {RunId} failed: secret-leak guard tripped. {Message}", run.Id, ex.Message);
            await CompleteRunAsync(run, WorkflowRunStatus.Failure, ex.Message, cancellationToken).ConfigureAwait(false);
            await _recordLogger.RunFailedAsync(run.Id, ex.Message, DateTimeOffset.UtcNow - engineStartedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Operator-initiated cancel — distinct from a node Failure. CompleteRun uses CT.None
            // since the supplied token is already triggered; we still want the DB write to land.
            await CompleteRunAsync(run, WorkflowRunStatus.Cancelled, error: null, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await _recordLogger.RunCancelledAsync(run.Id, DateTimeOffset.UtcNow - engineStartedAt, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Bootstrap-failure path: invoked when post-claim code throws BEFORE the walker
    /// starts (most likely <see cref="ReleaseTamperedException"/>, a deserialisation
    /// failure, or a snapshot-persist conflict). We mark the run Failed via a CAS UPDATE
    /// (Running → Failure) AND emit a <c>run.failed</c> ledger record so the timeline
    /// surfaces the cause. CancellationToken.None is used everywhere — the original token
    /// may already be tripped, but we want this write to land regardless.
    /// </summary>
    private async Task MarkBootstrapFailureAsync(Guid runId, DateTimeOffset engineStartedAt, Exception failure)
    {
        var message = $"Engine bootstrap failure ({failure.GetType().Name}): {failure.Message}";
        _logger.LogError(failure, "Run {RunId} failed during bootstrap before walker started", runId);

        var now = DateTimeOffset.UtcNow;
        await _db.WorkflowRun
            .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, WorkflowRunStatus.Failure)
                .SetProperty(r => r.Error, message)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)now), CancellationToken.None)
            .ConfigureAwait(false);

        // Best-effort ledger record — if THIS throws we just log; the row's already
        // terminal and that's the important guarantee.
        try
        {
            await _recordLogger.RunFailedAsync(runId, message, now - engineStartedAt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} bootstrap failure recorded in workflow_run but ledger emit failed", runId);
        }
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.WorkflowRun
            .Include(r => r.Workflow)
            .Include(r => r.RunRequest)          // normalised payload + source live on the request
            .SingleOrDefaultAsync(r => r.Id == runId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"WorkflowRun {runId} not found.");

    /// <summary>
    /// Loads the run's frozen <see cref="WorkflowDefinition"/> AND its
    /// <c>DefinitionHash</c> in a single query. The hash is captured into
    /// <c>workflow_run.release_hash_at_run</c> at first-run snapshot time and verified
    /// against the version row at replay time — divergence throws
    /// <see cref="ReleaseTamperedException"/>.
    ///
    /// <para>Tamper check (Phase 3.0 hardening): we recompute the hash from the CURRENT
    /// <c>definition_json</c> and compare against the stored <c>definition_hash</c>.
    /// workflow_version rows are immutable by contract, so the two MUST match. A mismatch
    /// means someone bypassed the publish path and mutated the JSON directly in the DB —
    /// we refuse to execute. This is independent of (and strictly stricter than) the
    /// replay-time check that compares <c>workflow_run.release_hash_at_run</c> against
    /// today's <c>definition_hash</c>: the new check catches the case where BOTH columns
    /// were mutated together (a malicious operator zeros their own audit trail).</para>
    /// </summary>
    private async Task<(WorkflowDefinition Definition, string DefinitionHash)> LoadDefinitionAndHashAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        // Fork on definition source. A SNAPSHOT run carries its own frozen definition inline (no
        // Workflow / WorkflowVersion row); an AUTHORED run loads its pinned version — byte-identical
        // to the pre-snapshot path. Both apply the SAME canonical-hash tamper check.
        if (run.DefinitionSnapshotJson != null)
            return LoadDefinitionFromSnapshot(run);

        return await LoadDefinitionFromVersionAsync(run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Authored-run load: read the pinned <c>workflow_version</c> JSON + hash and tamper-check it. Byte-identical to the pre-snapshot behaviour.</summary>
    private async Task<(WorkflowDefinition Definition, string DefinitionHash)> LoadDefinitionFromVersionAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        var version = await _db.WorkflowVersion
            .SingleAsync(v => v.WorkflowId == run.WorkflowId && v.Version == run.WorkflowVersion, cancellationToken)
            .ConfigureAwait(false);

        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(version.DefinitionJson, WorkflowJson.Options)
            ?? throw new InvalidOperationException($"WorkflowVersion ({run.WorkflowId},{run.WorkflowVersion}) has empty JSON.");

        var recomputed = DefinitionHash.Compute(definition);
        if (!string.Equals(recomputed, version.DefinitionHash, StringComparison.Ordinal))
            throw new ReleaseTamperedException(run.WorkflowId!.Value, run.WorkflowVersion!.Value, version.DefinitionHash, recomputed);

        return (definition, version.DefinitionHash);
    }

    /// <summary>
    /// Snapshot-run load: deserialise the run's inline frozen definition and tamper-check it against
    /// its stored snapshot hash with the SAME canonical hash + same exception semantics as an authored
    /// version. No DB read — the definition travels on the run row itself.
    /// </summary>
    private static (WorkflowDefinition Definition, string DefinitionHash) LoadDefinitionFromSnapshot(WorkflowRun run)
    {
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(run.DefinitionSnapshotJson!, WorkflowJson.Options)
            ?? throw new InvalidOperationException($"WorkflowRun {run.Id} has an empty definition snapshot.");

        var storedHash = run.DefinitionSnapshotHash ?? string.Empty;
        var recomputed = DefinitionHash.Compute(definition);
        if (!string.Equals(recomputed, storedHash, StringComparison.Ordinal))
            throw ReleaseTamperedException.ForSnapshot(run.Id, storedHash, recomputed);

        return (definition, storedHash);
    }

    /// <summary>
    /// First-run scope build: pulls every variable from the LIVE table, then writes a
    /// normalised snapshot capturing the resolved state. Subsequent replays of this run
    /// read from the snapshot rather than the (potentially mutated) live state.
    /// <para>Project-scoped variables are intentionally NOT snapshotted — they're always
    /// re-resolved from live state at replay time (similar to how Secret rotation is
    /// reflected). The rationale: projects are meant to be living "config namespaces"
    /// shared across many workflows; freezing them per-run would let stale dataset_url /
    /// endpoint values bleed into newer replays. Operators who need per-run freeze should
    /// model the value as a workflow-level <c>wf.*</c> variable.</para>
    /// </summary>
    private async Task<NodeRunScope> BuildScopeFreshAndPersistSnapshotAsync(WorkflowRun run, WorkflowDefinition definition, string releaseHash, CancellationToken cancellationToken)
    {
        var trigger = ParsePayloadObject(run.RunRequest?.NormalizedPayloadJson);
        var input = BuildInputScope(definition, trigger);

        // Fail-fast: surface required inputs the caller didn't supply AND that have no Default
        // in the definition — the validator throws (lands the run in Failure) rather than
        // letting them resolve to null. Only runs on FRESH runs — replays use the snapshotted
        // scope from first-run, so this can't retro-actively fail a replay.
        EnsureRequiredInputsSatisfied(run, definition, input);

        var sys = BuildSysScope(run);

        // Fetch typed sets ONCE so we can both build scope AND persist snapshot rows
        // without a second round-trip per scope. A snapshot run has no parent workflow, so its
        // workflow-scope lookup runs against Guid.Empty (resolves to no rows) and team scope reads
        // the run's denormalised TeamId — same value an authored run's run.Workflow.TeamId carries.
        var wfResolved = await _variableService.GetAllForEngineAsync(VariableScope.Workflow, WorkflowScopeId(run), cancellationToken).ConfigureAwait(false);
        var teamResolved = await _variableService.GetAllForEngineAsync(VariableScope.Team, run.TeamId, cancellationToken).ConfigureAwait(false);

        var wf = BagFromResolved(wfResolved);
        var team = BagFromResolved(teamResolved);

        // Project scope — load only the projects this definition references (via slug),
        // not every project in the team. Falls back to an empty bag when the definition
        // contains no project.* refs (cheap).
        var (projects, projectSecretPaths) = await LoadReferencedProjectVariablesAsync(run.TeamId, WorkflowScopeId(run), definition, cancellationToken).ConfigureAwait(false);

        var secretPaths = CollectSecretPaths(wfResolved, teamResolved, projectSecretPaths);

        await PersistFirstRunSnapshotAsync(run, releaseHash, wfResolved, teamResolved, cancellationToken).ConfigureAwait(false);

        return new NodeRunScope { Trigger = trigger, Input = input, Wf = wf, Sys = sys, Team = team, Projects = projects, SecretPaths = secretPaths };
    }

    /// <summary>
    /// Replay scope build: plain values come from the run's frozen snapshot rows; secret
    /// values are re-resolved fresh from the current <c>variable</c> table; sys is
    /// re-injected with the new run's identity (new run id, new started_at). Trigger
    /// payload is preserved verbatim from the run row.
    /// <para>Project-scope variables are re-resolved from the LIVE state at replay (not
    /// snapshotted). Match the documentation on <see cref="BuildScopeFreshAndPersistSnapshotAsync"/>:
    /// projects are shared config namespaces; freezing them would let stale endpoint /
    /// dataset values cross between original run and replay.</para>
    /// </summary>
    private async Task<NodeRunScope> BuildScopeForReplayAsync(WorkflowRun run, WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        var trigger = ParsePayloadObject(run.RunRequest?.NormalizedPayloadJson);
        var input = BuildInputScope(definition, trigger);
        var sys = BuildSysScope(run);

        var snapshot = await _db.WorkflowRunVariable.AsNoTracking()
            .Where(v => v.RunId == run.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Plain values for both scopes come from the snapshot. Secret values are
        // intentionally NOT in the snapshot (name-only audit rows) — re-resolved from
        // the live table so rotation invalidates old leaked credentials at replay time.
        // Scope discriminator must match what BuildSnapshotRow writes ("Workflow", aligned
        // with VariableScope enum).
        var wf = BuildScopeFromSnapshot(snapshot, "Workflow");
        var team = BuildScopeFromSnapshot(snapshot, "Team");

        var wfSecretNames = await MergeCurrentSecretsAsync(wf, VariableScope.Workflow, WorkflowScopeId(run), cancellationToken).ConfigureAwait(false);
        var teamSecretNames = await MergeCurrentSecretsAsync(team, VariableScope.Team, run.TeamId, cancellationToken).ConfigureAwait(false);

        // Project scope — load fresh (no snapshot path for projects).
        var (projects, projectSecretPaths) = await LoadReferencedProjectVariablesAsync(run.TeamId, WorkflowScopeId(run), definition, cancellationToken).ConfigureAwait(false);

        // Secret-paths used by the engine's Terminal-output leak guard. Replay re-resolves
        // secrets from the live table (see MergeCurrentSecretsAsync), so we use the same
        // freshly-fetched set to drive the paths bag — the snapshot itself stores no plaintext.
        var secretPaths = new HashSet<string>(wfSecretNames.Select(n => $"wf.{n}").Concat(teamSecretNames.Select(n => $"team.{n}")));
        foreach (var p in projectSecretPaths) secretPaths.Add(p);

        _logger.LogInformation(
            "Replay scope built. RunId={RunId} ParentRunId={ParentRunId} SnapshotCount={SnapshotCount}",
            run.Id, run.ParentRunId, snapshot.Count);

        return new NodeRunScope
        {
            Trigger = trigger,
            Input = input,
            Wf = wf,
            Sys = sys,
            Team = team,
            Projects = projects,
            SecretPaths = secretPaths,
        };
    }

    private static Dictionary<string, JsonElement> BuildScopeFromSnapshot(List<WorkflowRunVariable> snapshot, string scopeDiscriminator)
    {
        var bag = new Dictionary<string, JsonElement>();
        foreach (var row in snapshot)
        {
            if (row.Scope != scopeDiscriminator) continue;
            if (row.ValueType == "Secret") continue;   // secrets re-resolved later from live table

            // value_plain is JSON-encoded; parse back to JsonElement so the resolver can
            // walk it just like any other scope entry.
            var element = JsonDocument.Parse(row.ValuePlain ?? "null").RootElement.Clone();
            bag[row.Name] = element;
        }
        return bag;
    }

    /// <summary>
    /// Overlays the current Secret-typed variables for <paramref name="scope"/> onto
    /// the supplied bag. Rotation safety: replay sees the CURRENT secret value, not
    /// whatever the original run used. Non-secret rows from the live table are ignored
    /// here — those came from the snapshot.
    /// <para>Returns the names of secrets that were merged; caller uses them to populate
    /// <see cref="NodeRunScope.SecretPaths"/> for the leak guard.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> MergeCurrentSecretsAsync(Dictionary<string, JsonElement> bag, VariableScope scope, Guid scopeId, CancellationToken cancellationToken)
    {
        var current = await _variableService.GetAllForEngineAsync(scope, scopeId, cancellationToken).ConfigureAwait(false);
        var secretNames = new List<string>();
        foreach (var v in current)
        {
            if (v.ValueType != VariableValueType.Secret) continue;
            bag[v.Name] = v.Value;
            secretNames.Add(v.Name);
        }
        return secretNames;
    }

    /// <summary>
    /// Builds the fully-qualified secret-path set for the Terminal-output leak guard. Returns
    /// strings like <c>"wf.PASSWORD"</c> / <c>"team.API_KEY"</c> / <c>"project.MyProject.API_KEY"</c>
    /// — the same dotted form a {{ref}} template uses, so
    /// <see cref="VariableResolver.ExtractReferencedPaths"/> output can be intersected against
    /// this set directly.
    /// </summary>
    private static HashSet<string> CollectSecretPaths(IReadOnlyList<ResolvedVariable> wfResolved, IReadOnlyList<ResolvedVariable> teamResolved, IReadOnlyCollection<string>? projectSecretPaths = null)
    {
        var paths = new HashSet<string>();
        foreach (var v in wfResolved) if (v.ValueType == VariableValueType.Secret) paths.Add($"wf.{v.Name}");
        foreach (var v in teamResolved) if (v.ValueType == VariableValueType.Secret) paths.Add($"team.{v.Name}");
        if (projectSecretPaths != null) foreach (var p in projectSecretPaths) paths.Add(p);
        return paths;
    }

    /// <summary>
    /// Phase 3.0 — load every project's variables referenced by the workflow definition.
    /// Returns a slug-keyed bag of (varName → JsonElement) PLUS the fully-qualified secret
    /// paths (e.g. <c>"project.MyProject.API_KEY"</c>) for the leak guard.
    ///
    /// <para>References are extracted from the WHOLE definition (every node's <c>Inputs</c>
    /// + <c>Config</c>) — paths starting with <c>project.</c> contribute their second
    /// segment as a slug. Save-time validation enforces shape only
    /// (<c>project.&lt;slug&gt;.&lt;name&gt;</c>) and does NOT verify the slug exists —
    /// that would require a DB lookup at every save and would race with concurrent project
    /// deletes.</para>
    ///
    /// <para>Missing slugs at run time (project deleted after save, or never existed)
    /// flow through <see cref="MissingProjectRefValidator"/>, which throws
    /// <see cref="MissingProjectRefException"/> — the run lands in Failed with the missing
    /// slug(s) named in <c>WorkflowRun.Error</c> instead of silently resolving to null.</para>
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Bag, IReadOnlyList<string> SecretPaths)> LoadReferencedProjectVariablesAsync(Guid teamId, Guid workflowId, WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        var referencedSlugs = CollectReferencedProjectSlugs(definition);
        if (referencedSlugs.Count == 0)
            return (new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(), Array.Empty<string>());

        // Resolve slug → project.id in one query, filtered to the team for safety.
        var projects = await _db.Project.AsNoTracking()
            .Where(p => p.TeamId == teamId && p.DeletedDate == null && referencedSlugs.Contains(p.Slug))
            .Select(p => new { p.Id, p.Slug })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Fail-fast on the diff between referenced and found slugs: a stale ref throws here
        // (caught by the engine's main loop, which lands the run in Failed with the
        // exception's message in WorkflowRun.Error) instead of silently resolving to null.
        var foundSlugs = projects.Select(p => p.Slug).ToHashSet(StringComparer.Ordinal);
        var refCtx = new MissingProjectRefContext(referencedSlugs, foundSlugs, teamId, workflowId);
        MissingProjectRefValidator.EnsureKnown(refCtx);

        var bag = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>();
        var secretPaths = new List<string>();

        foreach (var p in projects)
        {
            var resolved = await _variableService.GetAllForEngineAsync(VariableScope.Project, p.Id, cancellationToken).ConfigureAwait(false);
            var projectBag = new Dictionary<string, JsonElement>(resolved.Count);
            foreach (var v in resolved)
            {
                projectBag[v.Name] = v.Value;
                if (v.ValueType == VariableValueType.Secret) secretPaths.Add($"project.{p.Slug}.{v.Name}");
            }
            bag[p.Slug] = projectBag;
        }

        return (bag, secretPaths);
    }

    /// <summary>
    /// Walks every node's Inputs + Config templates extracting the unique set of project
    /// slugs referenced via <c>project.{slug}.X</c>. Distinct slugs drive how many
    /// per-project variable queries the engine issues per run. Inputs declarations
    /// (workflow IO contract) are not scanned — they declare the workflow's surface,
    /// they don't reference variables themselves.
    /// </summary>
    private static HashSet<string> CollectReferencedProjectSlugs(WorkflowDefinition definition)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in definition.Nodes)
        {
            foreach (var path in VariableResolver.ExtractReferencedPaths(node.Inputs))
                AddProjectSlug(slugs, path);
            foreach (var path in VariableResolver.ExtractReferencedPaths(node.Config))
                AddProjectSlug(slugs, path);
        }

        return slugs;
    }

    private static void AddProjectSlug(HashSet<string> slugs, string path)
    {
        var segments = path.Split('.');
        if (segments.Length < 3) return;
        if (segments[0] != "project") return;
        slugs.Add(segments[1]);
    }

    private static Dictionary<string, JsonElement> BagFromResolved(IReadOnlyList<ResolvedVariable> resolved)
    {
        var bag = new Dictionary<string, JsonElement>(resolved.Count);
        foreach (var r in resolved) bag[r.Name] = r.Value;
        return bag;
    }

    /// <summary>
    /// Throws <see cref="WorkflowSecretLeakException"/> when the Terminal's raw Inputs template
    /// references any path that resolved to a Secret-typed variable. The check is path-based
    /// (not value-based) so an author who tries to obfuscate by wrapping the secret in a
    /// template like <c>{ "key": "prefix-{{team.API_KEY}}-suffix" }</c> still fails — any path
    /// usage is rejected, not just the sole-template case.
    /// </summary>
    private static void EnsureNoSecretInTerminalOutputs(NodeDefinition terminal, NodeRunScope scope)
    {
        if (scope.SecretPaths.Count == 0) return;

        var referenced = VariableResolver.ExtractReferencedPaths(terminal.Inputs);
        foreach (var path in referenced)
        {
            if (!scope.SecretPaths.Contains(path)) continue;

            throw new WorkflowSecretLeakException(
                $"Terminal node '{terminal.Id}' output mapping references secret variable '{{{{{path}}}}}'. " +
                $"Secrets cannot be persisted to workflow_run.OutputsJson — they're designed for in-process " +
                $"node consumption (HTTP auth headers, LLM API keys). Remove the reference, or change the " +
                $"underlying variable's type from Secret to a non-secret type if intentional.");
        }
    }

    /// <summary>
    /// First-run persist: stamps the release hash onto the run row + bulk-inserts
    /// <c>workflow_run_variable</c> rows for every resolved variable. Secret rows carry
    /// <c>value_plain = NULL</c> — the value is intentionally NOT persisted so the snapshot
    /// can never leak rotated credentials.
    /// </summary>
    private async Task PersistFirstRunSnapshotAsync(WorkflowRun run, string releaseHash, IReadOnlyList<ResolvedVariable> wfResolved, IReadOnlyList<ResolvedVariable> teamResolved, CancellationToken cancellationToken)
    {
        run.ReleaseHashAtRun = releaseHash;

        var rows = new List<WorkflowRunVariable>(wfResolved.Count + teamResolved.Count);
        // Snapshot scope discriminator matches the canonical VariableScope enum
        // (Workflow/Team). Diagnostic joins against the source `variable` table rely on this.
        foreach (var v in wfResolved) rows.Add(BuildSnapshotRow(run.Id, "Workflow", v));
        foreach (var v in teamResolved) rows.Add(BuildSnapshotRow(run.Id, "Team", v));

        if (rows.Count > 0) _db.WorkflowRunVariable.AddRange(rows);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The variables.snapshotted record marks the moment the snapshot is committed. Emitted
        // even when there are zero variables — the row count is part of the payload so an
        // operator reading the ledger can tell "snapshot ran, found nothing" apart from
        // "snapshot phase was skipped entirely".
        await _recordLogger.VariablesSnapshottedAsync(run.Id, wfResolved.Count, teamResolved.Count, releaseHash, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Snapshot persisted. RunId={RunId} ReleaseHash={Hash} WfCount={WfCount} TeamCount={TeamCount}",
            run.Id, releaseHash, wfResolved.Count, teamResolved.Count);
    }

    private static WorkflowRunVariable BuildSnapshotRow(Guid runId, string scope, ResolvedVariable v) => new()
    {
        Id = Guid.NewGuid(),
        RunId = runId,
        Scope = scope,
        Name = v.Name,
        ValueType = v.ValueType.ToString(),
        // Secret type → NULL (name-only audit row). Everything else → JSON-encoded value
        // straight from the engine's resolved JsonElement. GetRawText preserves the
        // canonical type representation (numbers stay numeric, strings keep their quotes).
        ValuePlain = v.ValueType == VariableValueType.Secret ? null : v.Value.GetRawText(),
        CapturedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Build the {{sys.*}} bag — engine-injected per-run context. Keys are pinned by
    /// <see cref="SystemScopeKeys"/>; populating a NEW key here also requires updating that
    /// list + the editor's system-variables tab so the autocomplete picker shows it.
    /// <para>UserId is null for runs not initiated by a person (webhook / cron); we emit a
    /// JSON null so the resolver returns null when referenced, matching how
    /// <c>{{input.missing}}</c> behaves.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> BuildSysScope(WorkflowRun run)
    {
        var startedAt = (run.StartedAt ?? DateTimeOffset.UtcNow).ToString("o");
        var sourceType = run.RunRequest?.SourceType ?? string.Empty;
        var userIdValue = run.CreatedBy == Guid.Empty ? (Guid?)null : run.CreatedBy;

        // WorkflowId / WorkflowVersion are nullable — a snapshot run has neither, so {{sys.workflow_id}}
        // / {{sys.workflow_version}} resolve to JSON null (same shape as {{sys.user_id}} for an
        // anonymous run). TeamId reads the denormalised run column (always present, snapshot or not).
        return new Dictionary<string, JsonElement>
        {
            [SystemScopeKeys.WorkflowId]      = ToJson(run.WorkflowId),
            [SystemScopeKeys.WorkflowRunId]   = ToJson(run.Id),
            [SystemScopeKeys.WorkflowVersion] = ToJson(run.WorkflowVersion),
            [SystemScopeKeys.SourceType]      = ToJson(sourceType),
            [SystemScopeKeys.StartedAt]       = ToJson(startedAt),
            [SystemScopeKeys.TeamId]          = ToJson(run.TeamId),
            [SystemScopeKeys.UserId]          = userIdValue.HasValue ? ToJson(userIdValue.Value) : ToJsonNull(),
        };
    }

    private static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement ToJsonNull() => JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>
    /// Build the {{input.*}} bag from declared inputs:
    ///   - take the value from trigger payload when present
    ///   - else use the definition's default
    ///   - else omit (resolver returns null when referenced)
    ///
    /// <para>Required-input enforcement is performed by <see cref="MissingRequiredInputValidator"/>
    /// at first-run scope build (caller's responsibility), NOT here — this method stays
    /// pure data assembly so it stays trivially safe to call from the replay path.</para>
    ///
    /// <para>Per-value type-validation against <c>Schema</c> is deferred to a later pass — for
    /// now we trust the frontend SchemaForm + the Inputs declaration. A future pass could
    /// reject inputs that don't match Schema; today the engine doesn't validate JSON-Schema
    /// conformance at runtime (the NodeRunContext doc-comment is explicit about this).</para>
    /// </summary>
    /// <summary>
    /// Fail-fast on required inputs that <see cref="BuildInputScope"/> failed to populate:
    /// <see cref="MissingRequiredInputValidator"/> throws <see cref="MissingRequiredInputException"/>
    /// so the run lands in Failed instead of silently resolving the missing input to null.
    /// </summary>
    private void EnsureRequiredInputsSatisfied(WorkflowRun run, WorkflowDefinition definition, IReadOnlyDictionary<string, JsonElement> resolvedInputs)
    {
        if (definition.Inputs.Count == 0) return;

        var requiredNames = new List<string>();
        foreach (var declared in definition.Inputs)
            if (declared.Required) requiredNames.Add(declared.Name);

        if (requiredNames.Count == 0) return;

        var ctx = new MissingRequiredInputContext(requiredNames, resolvedInputs.Keys.ToList(), run.TeamId, WorkflowScopeId(run));
        MissingRequiredInputValidator.EnsureSatisfied(ctx);
    }

    /// <summary>
    /// The workflow id to use for workflow-scoped variable lookups + diagnostic context. A snapshot
    /// run has no parent workflow, so it resolves to <see cref="Guid.Empty"/> — a workflow-scope
    /// variable query against Guid.Empty matches no rows (snapshot runs carry no <c>wf.*</c>
    /// variables), and the value only ever appears in fail-fast exception text otherwise.
    /// </summary>
    private static Guid WorkflowScopeId(WorkflowRun run) => run.WorkflowId ?? Guid.Empty;

    private static IReadOnlyDictionary<string, JsonElement> BuildInputScope(WorkflowDefinition definition, IReadOnlyDictionary<string, JsonElement> triggerPayload)
    {
        if (definition.Inputs.Count == 0) return new Dictionary<string, JsonElement>();

        var input = new Dictionary<string, JsonElement>();

        foreach (var declared in definition.Inputs)
        {
            if (triggerPayload.TryGetValue(declared.Name, out var supplied))
            {
                input[declared.Name] = supplied;
            }
            else if (declared.Default.HasValue)
            {
                input[declared.Name] = declared.Default.Value;
            }
            // else: omitted. {{input.name}} resolves to null; downstream node's schema
            // validation (if any) catches missing-but-required.
        }

        return input;
    }

    private static Dictionary<string, JsonElement> ParsePayloadObject(string? json)
    {
        var dict = new Dictionary<string, JsonElement>();

        if (string.IsNullOrWhiteSpace(json)) return dict;

        var root = JsonDocument.Parse(json).RootElement;
        if (root.ValueKind != JsonValueKind.Object) return dict;

        foreach (var prop in root.EnumerateObject()) dict[prop.Name] = prop.Value.Clone();
        return dict;
    }

    private async Task StartRunAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        // The atomic Enqueued→Running CAS at the entry of ExecuteRunAsync already flipped
        // Status + StartedAt. This method is kept as a no-op for symmetry with
        // CompleteRunAsync (and as a hook for future "start ceremony" extensions).
        await Task.CompletedTask;
    }

    private async Task CompleteRunAsync(WorkflowRun run, WorkflowRunStatus status, string? error, CancellationToken cancellationToken)
    {
        run.Status = status;
        run.Error = error;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // A run reaching Failure/Cancelled with branch waits still parked (a terminate-mode map failure that
        // fails the run, or an operator cancel) would otherwise leave its staged-but-undispatched AgentRun /
        // Subworkflow children orphaned. Clean them at the source — best-effort; the reconciler's parent-run-
        // terminal guard is the robust net that catches any orphan this misses.
        if (status is WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled)
            await CancelPendingWaitsAndChildrenAsync(run, cancellationToken).ConfigureAwait(false);

        // If this run is a sub-workflow child, wake the parent that's parked on it.
        await ResumeParentIfSubworkflowChildAsync(run, status, error, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort source-side orphan cleanup when a run lands Failure/Cancelled: resolve its still-Pending
    /// waits (so none dangle) and mark the staged children they parked on Cancelled — Queued <c>AgentRun</c>
    /// children via the agent service's Queued-guarded CAS (a worker that already claimed one loses, untouched),
    /// and non-terminal <c>Subworkflow</c> children via a direct status-guarded CAS. NEVER throws out of
    /// completion (a cleanup failure must not turn a clean terminal into a stuck run); the reconciler's
    /// parent-run-terminal guard re-cleans anything missed.
    /// </summary>
    private async Task CancelPendingWaitsAndChildrenAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        try
        {
            var pending = await _db.WorkflowRunWait.AsNoTracking()
                .Where(w => w.RunId == run.Id && w.Status == WorkflowWaitStatuses.Pending)
                .Select(w => new { w.WaitKind, w.Token })
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (pending.Count == 0) return;

            foreach (var wait in pending)
                await CancelStagedChildAsync(wait.WaitKind, wait.Token, cancellationToken).ConfigureAwait(false);

            await _db.WorkflowRunWait
                .Where(w => w.RunId == run.Id && w.Status == WorkflowWaitStatuses.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.Status, WorkflowWaitStatuses.Resolved)
                    .SetProperty(w => w.ResolvedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} reached a terminal state but cleaning up its staged children failed; the reconciler will catch any orphan", run.Id);
        }
    }

    /// <summary>Cancel the staged child a pending wait parked on: a Queued AgentRun (its Token is the agent-run id) or a non-terminal Subworkflow child run (its Token is the child run id). Other wait kinds (timer / approval / callback / action / supervisor-decision) have no staged child to cancel — a SupervisorDecision wait's self-advance is gated on the wait still being Pending, so the caller's resolve of the wait (in <see cref="CancelPendingWaitsAndChildrenAsync"/>) already neutralises any in-flight or reconciler-re-fired advance once the run is terminal.</summary>
    private async Task CancelStagedChildAsync(string waitKind, string token, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(token, out var childId)) return;

        if (waitKind == WorkflowWaitKinds.AgentRun)
            await _agentRunService.CancelQueuedAsync(childId, "Parent workflow run reached a terminal state before this branch launched.", cancellationToken).ConfigureAwait(false);
        else if (waitKind == WorkflowWaitKinds.Subworkflow)
            await _db.WorkflowRun
                .Where(r => r.Id == childId && (r.Status == WorkflowRunStatus.Pending || r.Status == WorkflowRunStatus.Enqueued))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, WorkflowRunStatus.Cancelled)
                    .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatch the not-yet-dispatched sub-workflow child a just-suspended run staged. Looks up the
    /// run's pending <c>Subworkflow</c> wait (its Token is the child run id) and dispatches it. No-op
    /// for non-subworkflow suspends (timer / approval / callback) — those have no Subworkflow wait.
    /// </summary>
    private async Task DispatchPendingSubworkflowChildAsync(Guid parentRunId, CancellationToken cancellationToken)
    {
        // Dispatch EVERY child this suspend cycle staged — a parallel fan-out of sub-workflow nodes parks on
        // K Subworkflow waits, each with its own child run id; a single FirstOrDefault left K-1 children
        // never dispatched (stranded until the reconciler). Each child run is independent.
        var tokens = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == parentRunId && w.WaitKind == WorkflowWaitKinds.Subworkflow && w.Status == WorkflowWaitStatuses.Pending)
            .Select(w => w.Token)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var token in tokens)
            if (Guid.TryParse(token, out var childRunId))
                await _subworkflowService.DispatchChildRunAsync(childRunId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatch the agent run a just-suspended <c>agent.code</c> node staged. Looks up the run's pending
    /// <c>AgentRun</c> wait (its Token is the agent-run id) and enqueues the executor on the background
    /// queue — so the harness streams in its sandbox out-of-band while this run stays Suspended. No-op
    /// for non-agent suspends. Enqueued (not awaited): a worker crash mid-run is re-claimed by the
    /// reconciler, and the executor's claim guard makes a double-dispatch a no-op (no wasted tokens).
    /// </summary>
    private async Task DispatchPendingAgentRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Enqueue EVERY agent run this suspend cycle staged — a parallel wave parks K agent.code nodes on K
        // AgentRun waits, and each needs its executor launched now. A single FirstOrDefault left K-1 stranded
        // until the reconciler's stale-window re-dispatch. The executor's claim CAS makes a re-dispatch a no-op.
        var tokens = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
            .Select(w => w.Token)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var token in tokens)
            if (Guid.TryParse(token, out var agentRunId))
                _backgroundJobClient.Enqueue<IAgentRunExecutor>(e => e.ExecuteAsync(agentRunId, CancellationToken.None));
    }

    /// <summary>
    /// Advance the supervisor turn a just-suspended <c>agent.supervisor</c> node parked on. A
    /// <c>SupervisorDecision</c> wait has NO external work item — it is a SELF-ADVANCE: now that the run is
    /// COMMITTED Suspended, enqueue a resume of its own pending wait, which resolves the wait + flips
    /// Suspended → Pending + re-dispatches, so the supervisor node re-enters for the next turn (reading the
    /// next decision from its durable ledger, NOT from the resume payload — a bare marker suffices). Reuses
    /// the SAME post-commit <c>Enqueue&lt;IWorkflowResumeService&gt;.ResumeWaitAsync</c> machinery the timer
    /// wait self-wakes through — no parallel scheduler. Deferring to AFTER the Suspended commit (like the
    /// agent-run dispatch) means the resume can't run before the wait it resolves is visible. The resume's
    /// own wait-status CAS makes a double-enqueue (a Hangfire retry, the reconciler re-fire) idempotent.
    /// Enqueues EVERY pending supervisor wait so a future multi-turn fan-out is covered; E2 parks one per turn.
    /// </summary>
    private async Task DispatchPendingSupervisorAdvanceAsync(Guid runId, CancellationToken cancellationToken)
    {
        var waitIds = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision && w.Status == WorkflowWaitStatuses.Pending)
            .Select(w => w.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var waitId in waitIds)
            _backgroundJobClient.Enqueue<IWorkflowResumeService>(s => s.ResumeWaitAsync(runId, waitId, null, CancellationToken.None));
    }

    /// <summary>
    /// When a child run reaches a terminal state, resume the parent parked on it (Phase 3). The
    /// parent is found via its pending <c>Subworkflow</c> wait whose Token equals this child's id —
    /// which also distinguishes a sub-workflow child from a replay child (a replay's "parent" holds
    /// no such wait). Resumes with the child's status + outputs + error so the node can map them.
    /// Best-effort: a failure here is logged, not propagated (the child already completed cleanly).
    /// </summary>
    private async Task ResumeParentIfSubworkflowChildAsync(WorkflowRun child, WorkflowRunStatus status, string? error, CancellationToken cancellationToken)
    {
        if (child.ParentRunId == null) return;

        try
        {
            var childIdToken = child.Id.ToString();
            var waitId = await _db.WorkflowRunWait.AsNoTracking()
                .Where(w => w.RunId == child.ParentRunId && w.WaitKind == WorkflowWaitKinds.Subworkflow
                            && w.Token == childIdToken && w.Status == WorkflowWaitStatuses.Pending)
                .Select(w => (Guid?)w.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (waitId is null) return;   // not a sub-workflow child (e.g. a replay) — nothing to resume

            var payload = JsonSerializer.Serialize(new
            {
                status = status.ToString(),
                outputs = JsonSafeParseObject(child.OutputsJson),
                error,
            });

            // Resolve ONLY this child's own wait — never the sibling waits a parallel fan-out of children
            // holds — so each parent subworkflow node resumes with its own child's outputs.
            await _resumeService.ResumeOnWaitCompletionAsync(child.ParentRunId.Value, waitId.Value, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sub-workflow child {ChildRunId} completed but resuming parent {ParentRunId} failed", child.Id, child.ParentRunId);
        }
    }

    /// <summary>Parse a run's OutputsJson into a JsonElement object for the resume payload; an empty object on any trouble.</summary>
    private static JsonElement JsonSafeParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return JsonDocument.Parse("{}").RootElement.Clone();
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            return root.ValueKind == JsonValueKind.Object ? root.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    // Returns the workflow's outputs (filled by the last successful Terminal). Empty when
    // no Terminal ran or the Terminal had no Inputs declared.
    private async Task<IReadOnlyDictionary<string, JsonElement>> WalkGraphAsync(WorkflowRun run, WorkflowDefinition definition, NodeRunScope scope, CancellationToken cancellationToken)
    {
        // The top-level walk sees only top-level nodes (ParentId == null); a flow.loop's body
        // (ParentId == loopId) is owned by its container and walked per-iteration inside
        // ExecuteLoopAsync, never by this frontier. The FULL definition is still threaded to the
        // loop dispatcher so it can carve out its body subgraph.
        var topLevel = SubgraphView(definition, n => n.ParentId == null);
        var state = new WalkerState(topLevel);

        // Durable re-entry (Engine v2 Phase 0): rebuild walker state from the persisted ledger
        // so a run resumed after a crash / re-dispatch continues from where it stopped instead
        // of re-running completed nodes. For a fresh run (no node records yet) this is a no-op
        // and the ready frontier collapses to the graph roots — behaviour-identical to the
        // original single-pass walk.
        await RehydrateFromLedgerAsync(run, topLevel, scope, state, cancellationToken).ConfigureAwait(false);

        EnqueueReadyFrontier(state, topLevel);

        // Walk by WAVES: drain the whole ready frontier, then execute it. The frontier invariant
        // guarantees the drained nodes are mutually independent (a node enters Ready only once ALL its
        // incoming edges' sources have settled, so an edge A→B can't put A and B in Ready together),
        // so they're safe to run concurrently. Each wave: settle skips (cheap), run the regular nodes
        // as a bounded parallel batch (each in its own DI scope), then run loop containers sequentially
        // (engine-driven). Effects merge single-threaded in drain order — deterministic regardless of
        // task completion order. Re-draining picks up the downstream nodes this wave just enqueued.
        while (state.Ready.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var wave = DrainReadyWave(state);

            var regular = new List<NodeDefinition>(wave.Count);
            var containers = new List<NodeDefinition>();
            foreach (var node in wave)
            {
                if (ShouldSkip(node, state))
                {
                    await MarkSkippedAsync(run, node, state, cancellationToken).ConfigureAwait(false);
                    EnqueueDownstreamWhenReady(node, state);
                }
                else if (IsContainerNode(node)) containers.Add(node);
                else regular.Add(node);
            }

            // Regular nodes run concurrently and RETURN their effects; merge + advance in drain order
            // (single-threaded), so a failure / suspend still aborts the walk deterministically.
            var outcomes = await RunReadyNodesAsync(run, regular, scope, state, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < regular.Count; i++)
            {
                MergeNodeOutcome(regular[i], outcomes[i], scope, state);
                AdvanceAfterNodeSettled(regular[i], outcomes[i].Status, state);
            }

            // An engine-driven container (loop / try) is dispatched on Kind (like Trigger/Terminal): it
            // runs its body sub-walk + writes its own scope/state sequentially, so it stays off the
            // parallel batch.
            foreach (var container in containers)
            {
                var status = await ExecuteContainerAsync(run, definition, container, scope, state, cancellationToken).ConfigureAwait(false);
                state.Statuses[container.Id] = status;
                AdvanceAfterNodeSettled(container, status, state);
            }

            // Reaching a Terminal doesn't short-circuit the walker — we keep draining so any remaining
            // ready/skip-pending nodes get persisted as Skipped (the run-detail UI needs those rows to
            // show "this branch didn't run because of routing"). The outer try/catch sets the run to
            // Success when the frontier empties.
        }

        return state.WorkflowOutputs;
    }

    /// <summary>Drain the entire ready frontier into one wave (mutually-independent nodes), skipping any already fired in a prior wave and de-duping within the drain.</summary>
    private static List<NodeDefinition> DrainReadyWave(WalkerState state)
    {
        var wave = new List<NodeDefinition>();
        var seen = new HashSet<string>();
        while (state.Ready.Count > 0)
        {
            var id = state.Ready.Dequeue();
            if (state.Statuses.ContainsKey(id)) continue;   // already settled in an earlier wave
            if (!seen.Add(id)) continue;                    // same node enqueued twice this drain
            wave.Add(state.NodeById[id]);
        }
        return wave;
    }

    /// <summary>An engine-driven container that owns a body subgraph (a loop, a try, or a map) — dispatched specially + run sequentially, off the parallel batch.</summary>
    private bool IsContainerNode(NodeDefinition node) => _nodeRegistry.Resolve(node.TypeKey).Manifest.Kind is NodeKind.Loop or NodeKind.Try or NodeKind.Map;

    /// <summary>Dispatch a container node on its Kind: a try runs its body once with a catch boundary; a map fans its body once per element; a loop re-runs per iteration. Each writes its own scope/state + returns the node's status.</summary>
    private Task<NodeStatus> ExecuteContainerAsync(WorkflowRun run, WorkflowDefinition definition, NodeDefinition node, NodeRunScope scope, WalkerState state, CancellationToken cancellationToken, string nodeIterationKey = NoIteration) =>
        _nodeRegistry.Resolve(node.TypeKey).Manifest.Kind switch
        {
            NodeKind.Try => ExecuteTryAsync(run, definition, node, scope, state, cancellationToken, nodeIterationKey),
            NodeKind.Map => ExecuteMapAsync(run, definition, node, scope, state, cancellationToken, nodeIterationKey),
            _ => ExecuteLoopAsync(run, definition, node, scope, state, cancellationToken, nodeIterationKey),
        };

    /// <summary>
    /// The shared post-settle tail for any node (regular or loop): a failure halts the run UNLESS the
    /// node has an <c>error</c> edge (then IsEdgeLive routes the run down the handler branch — the
    /// merged outcome already exposed the failure as the node's <c>error</c> output); a suspend aborts
    /// the walk so the run lands Suspended (the wait + node.suspended are already persisted); otherwise
    /// release the downstream nodes that are now ready.
    /// </summary>
    private void AdvanceAfterNodeSettled(NodeDefinition node, NodeStatus status, WalkerState state)
    {
        if (status == NodeStatus.Failure && !HasErrorEdge(node, state))
            throw new NodeFailureException($"Node '{node.Id}' failed.");

        if (status == NodeStatus.Suspended)
            throw new RunSuspendedException(node.Id);

        EnqueueDownstreamWhenReady(node, state);
    }

    /// <summary>
    /// The loop-body variant of <see cref="AdvanceAfterNodeSettled"/>: a suspend always parks the run,
    /// but an UNHANDLED failure honours the loop's error policy — <see cref="LoopErrorHandling.Terminate"/>
    /// throws (fails the loop), <see cref="LoopErrorHandling.Continue"/> returns <c>true</c> to tell the
    /// pass to abandon the rest of this iteration (the loop counts it as a failed pass + moves on). A
    /// node with its own <c>error</c> edge is "handled" regardless of policy. Returns <c>true</c> ONLY
    /// for the continue-abandon case; otherwise releases the ready downstream and returns <c>false</c>.
    /// </summary>
    private bool AdvanceBodyNodeOrAbandon(NodeDefinition node, NodeStatus status, WalkerState bodyState, LoopErrorHandling errorHandling)
    {
        if (status == NodeStatus.Failure && !HasErrorEdge(node, bodyState))
        {
            if (errorHandling == LoopErrorHandling.Terminate)
                throw new NodeFailureException($"Node '{node.Id}' failed.");

            return true;   // continue: abandon the rest of this pass
        }

        if (status == NodeStatus.Suspended)
            throw new RunSuspendedException(node.Id);

        EnqueueDownstreamWhenReady(node, bodyState);
        return false;
    }

    /// <summary>
    /// Execute one wave's regular nodes, returning their effects in the SAME order (the caller merges
    /// single-threaded). A single ready node runs on THIS scope — the fast, behaviour-identical path
    /// for a linear workflow (no child scope, no semaphore). A genuine fan-out (≥2 ready nodes) runs
    /// each node in its OWN DI scope, bounded by <paramref name="maxParallelism"/> (null ⇒ the
    /// engine-wide <see cref="_maxParallelism"/>; a loop passes its own per-loop cap): the nodes only
    /// READ the shared scope/state during the wave (writes happen in the caller's single-threaded
    /// merge), so concurrent execution can't race on the shared collections, and each node's own
    /// DbContext / record logger keeps EF change-trackers thread-isolated.
    /// </summary>
    private async Task<NodeRunOutcome[]> RunReadyNodesAsync(WorkflowRun run, IReadOnlyList<NodeDefinition> nodes, NodeRunScope scope, WalkerState state, CancellationToken cancellationToken, string iterationKey = NoIteration, int? maxParallelism = null)
    {
        if (nodes.Count == 0) return [];

        if (nodes.Count == 1)
            return [await ExecuteNodeAsync(run, nodes[0], scope, state, cancellationToken, iterationKey).ConfigureAwait(false)];

        using var gate = new SemaphoreSlim(maxParallelism ?? _maxParallelism);
        var tasks = new Task<NodeRunOutcome>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
            tasks[i] = RunNodeInChildScopeAsync(run, nodes[i], scope, state, gate, cancellationToken, iterationKey);

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Run a single node in its OWN lifetime scope (its own CodeSpaceDbContext + IRunRecordLogger +
    /// ISubworkflowService), bounded by the wave's semaphore. The fresh engine reuses the SAME node-
    /// execution path (ExecuteNodeAsync) but with scoped DB writers, so two nodes running at once never
    /// touch one EF change-tracker. It reads the shared (read-only-during-the-wave) scope/state and
    /// returns its effects for the caller to merge.
    /// </summary>
    private async Task<NodeRunOutcome> RunNodeInChildScopeAsync(WorkflowRun run, NodeDefinition node, NodeRunScope scope, WalkerState state, SemaphoreSlim gate, CancellationToken cancellationToken, string iterationKey = NoIteration)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var childScope = _lifetimeScope.BeginLifetimeScope();
            var nodeEngine = childScope.Resolve<WorkflowEngine>();
            return await nodeEngine.ExecuteNodeAsync(run, node, scope, state, cancellationToken, iterationKey).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Carve a subgraph out of the definition: the nodes matching <paramref name="belongs"/> plus the
    /// edges wholly within them. Splits the graph into the top-level view (ParentId == null) and each
    /// loop's body view (ParentId == loopId). Edges never cross a container boundary (validator-
    /// enforced), so an edge belongs iff both endpoints do.
    /// </summary>
    private static WorkflowDefinition SubgraphView(WorkflowDefinition definition, Func<NodeDefinition, bool> belongs)
    {
        var nodes = definition.Nodes.Where(belongs).ToList();
        var ids = nodes.Select(n => n.Id).ToHashSet();
        var edges = definition.Edges.Where(e => ids.Contains(e.From) && ids.Contains(e.To)).ToList();
        return definition with { Nodes = nodes, Edges = edges };
    }

    /// <summary>
    /// Run a <c>flow.loop</c> container. Seeds the <c>loop.*</c> scope from its variables, then re-runs
    /// the body subgraph once per iteration until a termination condition is met OR a runaway budget
    /// (max-iterations / wall-clock / total body-node executions) is hit. Each pass persists its body
    /// nodes under iteration key <c>"&lt;loopId&gt;#&lt;i&gt;"</c>; loop variables thread across passes
    /// via their optional update ref. On exit the loop emits <c>{loop vars…, iterations,
    /// failedIterations, terminationReason}</c>. An unhandled body failure (no error edge) is governed
    /// by the config's <c>errorHandling</c>: <c>terminate</c> (default) fails the loop (whose OWN error
    /// edge can then catch it); <c>continue</c> skips just that pass and carries on. A body suspend or
    /// nested loop is refused with a clear message (follow-ups).
    /// </summary>
    private async Task<NodeStatus> ExecuteLoopAsync(WorkflowRun run, WorkflowDefinition definition, NodeDefinition loopNode, NodeRunScope scope, WalkerState state, CancellationToken cancellationToken, string nodeIterationKey = NoIteration)
    {
        var startedAt = DateTimeOffset.UtcNow;

        // Nesting guard: refuse pathologically deep loop-in-loop nesting (recursion + runaway safety),
        // mirroring the sub-workflow depth cap. Depth = the number of enclosing loop iterations.
        if (LoopNestingDepth(nodeIterationKey) >= LoopPlan.MaxNestingDepth)
            throw new NodeFailureException($"Loop '{loopNode.Id}' is nested deeper than the {LoopPlan.MaxNestingDepth}-level limit.");

        // The loop NODE's own ledger record is keyed by the context it runs in: NoIteration at top level,
        // or the enclosing loop's body-iteration key when this is a nested loop (so a loop re-run once per
        // outer iteration gets a distinct record per outer pass).
        await _recordLogger.NodeStartedAsync(run.Id, loopNode.Id, nodeIterationKey, EmptyJsonBag, EmptyJsonBag, cancellationToken).ConfigureAwait(false);

        try
        {
            var config = ParseLoopConfig(loopNode.Config);
            var plan = LoopPlan.From(config);
            var body = SubgraphView(definition, n => n.ParentId == loopNode.Id);
            var deadline = startedAt + plan.WallClock;
            // A per-loop maxParallelism throttles THIS body's wave (e.g. a rate-limited API); null inherits the engine-wide cap.
            var bodyParallelism = ResolveBodyParallelism(plan.MaxParallelism, _maxParallelism);

            // Resume-aware: a body node may have suspended on an earlier engine invocation. Rebuild how
            // far the loop got (completed iterations + threaded loop vars + failure count) from the
            // per-iteration ledger so we re-enter at the suspended pass instead of restarting at 0. A
            // fresh run finds no body rows → ResumeFrom 0 with freshly-initialised loop vars.
            var resume = await RehydrateLoopStateAsync(run, loopNode, body, config, scope, nodeIterationKey, cancellationToken).ConfigureAwait(false);
            var loopVars = resume.LoopVars;
            var nodeBudget = plan.NodeBudget;
            var iterations = resume.Iterations;
            var failures = resume.Failures;
            var reason = "maxIterations";

            for (var i = resume.ResumeFrom; i < plan.MaxIterations; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (DateTimeOffset.UtcNow > deadline)
                    throw new NodeFailureException($"Loop '{loopNode.Id}' exceeded its {plan.WallClock.TotalMinutes:0}-minute wall-clock budget after {iterations} iteration(s).");

                var iterScope = BuildLoopScope(scope, scope.Nodes, loopVars, i);
                // Body iteration key extends THIS loop's node key: "<loopId>#<i>" at top level, or
                // "<outerKey>/<loopId>#<i>" when nested — so nested iterations never collide across passes.
                var outcome = await RunLoopBodyOnceAsync(run, definition, body, iterScope, CombineIterationKey(nodeIterationKey, $"{loopNode.Id}#{i}"), plan.ErrorHandling, bodyParallelism, cancellationToken).ConfigureAwait(false);
                nodeBudget -= outcome.Executed;

                if (nodeBudget < 0)
                    throw new NodeFailureException($"Loop '{loopNode.Id}' exceeded its {plan.NodeBudget}-node execution budget.");

                iterations++;

                // continue-on-error: a failed pass is error-tainted, so we neither thread its loop-var
                // updates forward nor let it satisfy the termination condition — just count it and move on.
                // (Terminate mode never reaches here on failure: RunLoopBodyOnceAsync throws instead.)
                if (outcome.Failed)
                {
                    failures++;
                    continue;
                }

                loopVars = ApplyLoopVarUpdates(config.LoopVariables, loopVars, iterScope);

                if (IsTerminationMet(config.Termination, BuildLoopScope(scope, iterScope.Nodes, loopVars, i)))
                {
                    reason = "condition";
                    break;
                }
            }

            var outputs = BuildLoopOutputs(loopVars, iterations, failures, reason);
            scope.Nodes[loopNode.Id] = outputs;
            await _recordLogger.NodeCompletedAsync(run.Id, loopNode.Id, nodeIterationKey, outputs, null, DateTimeOffset.UtcNow - startedAt, cancellationToken).ConfigureAwait(false);
            return NodeStatus.Success;
        }
        catch (NodeFailureException ex)
        {
            // A body failure (no error edge) or a tripped budget — expose it as the loop's `error`
            // output so the loop's own error edge can catch it, then write node.failed.
            scope.Nodes[loopNode.Id] = BuildErrorOutput(ex.Message, loopNode.Id);
            await _recordLogger.NodeFailedAsync(run.Id, loopNode.Id, nodeIterationKey, ex.Message, DateTimeOffset.UtcNow - startedAt, cancellationToken).ConfigureAwait(false);
            return NodeStatus.Failure;
        }
    }

    /// <summary>
    /// Run a <c>flow.try</c> scope container: execute its body subgraph ONCE; if any body node fails
    /// unhandled, CATCH it — route the run down the container's <c>catch</c> handle (exposing the
    /// failure as the container's <c>error</c> output) instead of failing the run; otherwise route down
    /// the default success output. The container always settles Success (it handled the body failure,
    /// or there was none). A body SUSPEND propagates as <see cref="RunSuspendedException"/>, parking the
    /// run durably — resume re-enters this try and RehydrateLoopBodyAsync re-runs only the suspended
    /// body node. Reuses <see cref="RunLoopBodyOnceAsync"/> with continue-on-error semantics (an
    /// unhandled failure is REPORTED, not thrown — exactly the "caught" outcome), so durable suspend,
    /// nested loops/tries, and the parallel wave all work inside a try for free. The body runs under
    /// iteration key <c>CombineIterationKey(nodeIterationKey, tryId)</c> so its ledger rows never
    /// collide with the surrounding scope's.
    /// </summary>
    private async Task<NodeStatus> ExecuteTryAsync(WorkflowRun run, WorkflowDefinition definition, NodeDefinition tryNode, NodeRunScope scope, WalkerState state, CancellationToken cancellationToken, string nodeIterationKey = NoIteration)
    {
        var startedAt = DateTimeOffset.UtcNow;
        await _recordLogger.NodeStartedAsync(run.Id, tryNode.Id, nodeIterationKey, EmptyJsonBag, EmptyJsonBag, cancellationToken).ConfigureAwait(false);

        var body = SubgraphView(definition, n => n.ParentId == tryNode.Id);
        var bodyKey = CombineIterationKey(nodeIterationKey, tryNode.Id);

        // Run the body once with continue-on-error: an unhandled body failure is REPORTED (Failed) here
        // rather than thrown — that IS the caught outcome. A body suspend throws RunSuspendedException
        // out of here (parks the run; resume rehydrates the body + re-runs only the suspended node).
        var outcome = await RunLoopBodyOnceAsync(run, definition, body, scope, bodyKey, LoopErrorHandling.Continue, _maxParallelism, cancellationToken).ConfigureAwait(false);

        // Body OK → the default success output; body failed → the `catch` handle (failure handled).
        var hint = outcome.Failed ? WorkflowHandles.Catch : EdgeLiveness.DefaultHandle;
        state.RoutingHints[tryNode.Id] = new HashSet<string> { hint };

        var outputs = outcome.Failed
            ? BuildErrorOutput("A node in the try body failed.", tryNode.Id)
            : EmptyJsonBag;
        scope.Nodes[tryNode.Id] = outputs;

        await _recordLogger.NodeCompletedAsync(run.Id, tryNode.Id, nodeIterationKey, outputs, new[] { hint }, DateTimeOffset.UtcNow - startedAt, cancellationToken).ConfigureAwait(false);

        // The try always succeeds (it caught the body failure, or there was none); the routing hint
        // sends the run down `catch` or the default success path.
        return NodeStatus.Success;
    }

    /// <summary>
    /// Run a <c>flow.map</c> fan-out container. Resolves the <c>items</c> input to a JSON array, then
    /// runs the body subgraph ONCE PER ELEMENT as a BOUNDED-PARALLEL batch (each branch a full body
    /// sub-walk in its own DI lifetime scope, keyed <c>"&lt;mapId&gt;#&lt;i&gt;"</c>, seeing its element as
    /// <c>{{item}}</c> / <c>{{index}}</c>). Each branch's terminal body-node output becomes that element's
    /// result; the per-element results reduce — in element order — into <c>scope.Nodes[mapId] =
    /// { &lt;resultKey&gt;: [...], count, failed }</c>. An empty array is a clean no-op (empty results,
    /// Success). A non-array <c>items</c> is a clean NodeFailure. An unhandled branch failure is governed
    /// by the config's <c>errorHandling</c>: <c>terminate</c> (default) fails the map (whose own error edge
    /// can catch it); <c>continue</c> records that element's result as a failure marker + increments
    /// <c>failed</c>, and the map proceeds.
    ///
    /// <para>PR2 durable parallel-branch resume: a body node that SUSPENDS parks the run under the branch's
    /// own iteration key <c>"&lt;mapId&gt;#&lt;i&gt;"</c> — K parking branches = K independent
    /// <c>WorkflowRunWait</c> rows. When one-or-more branches parked, the map itself SUSPENDS (it does NOT
    /// complete): the standard <see cref="RunSuspendedException"/> propagates up through the container
    /// dispatch and lands the run Suspended (exactly how a suspend inside a loop body parks the run). The
    /// run stays Suspended until EVERY branch wait resolves (the wait-for-all barrier in
    /// <see cref="WorkflowResumeService.ResumeOnWaitCompletionAsync"/>). On a re-walk this method re-enters
    /// fresh: <see cref="RehydrateMapResultsAsync"/> replays already-finished branches from the ledger
    /// (their terminal output reconstructed into <c>results[index]</c> WITHOUT re-running their side effects
    /// — the exactly-once-per-branch guarantee) and re-runs ONLY still-suspended branches from their
    /// suspended node with the resolved-wait payload. The map completes (reduce + emit) only when all
    /// branches are terminal; otherwise it re-suspends.</para>
    /// </summary>
    private async Task<NodeStatus> ExecuteMapAsync(WorkflowRun run, WorkflowDefinition definition, NodeDefinition mapNode, NodeRunScope scope, WalkerState state, CancellationToken cancellationToken, string nodeIterationKey = NoIteration)
    {
        var startedAt = DateTimeOffset.UtcNow;

        // Nesting guard: refuse pathologically deep map-in-map (recursion + runaway safety), reusing the
        // loop nesting cap. Depth = the number of enclosing container iterations on the key.
        if (LoopNestingDepth(nodeIterationKey) >= MapPlan.MaxNestingDepth)
            throw new NodeFailureException($"Map '{mapNode.Id}' is nested deeper than the {MapPlan.MaxNestingDepth}-level limit.");

        await _recordLogger.NodeStartedAsync(run.Id, mapNode.Id, nodeIterationKey, EmptyJsonBag, EmptyJsonBag, cancellationToken).ConfigureAwait(false);

        try
        {
            var plan = MapPlan.From(ParseMapConfig(mapNode.Config));
            var elements = ResolveMapElements(mapNode, scope);

            if (elements.Count > MapPlan.MaxBranchesCeiling)
                throw new NodeFailureException($"Map '{mapNode.Id}' fans out over {elements.Count} elements, exceeding the {MapPlan.MaxBranchesCeiling}-branch ceiling.");

            var body = SubgraphView(definition, n => n.ParentId == mapNode.Id);
            var terminal = FindMapBodyTerminal(body, mapNode.Id);
            var branchParallelism = ResolveBodyParallelism(plan.MaxParallelism, _maxParallelism);

            // Resume-aware: read the ledger for branches that already reached a TERMINAL state on an earlier
            // engine invocation — a Success/Skipped terminal, OR (continue mode) an abandoning failure — and
            // reconstruct their results WITHOUT re-running them. A fresh run finds no branch rows → every
            // branch runs fresh.
            var replay = await RehydrateMapResultsAsync(run, mapNode, body, terminal, elements.Count, plan.ErrorHandling, nodeIterationKey, cancellationToken).ConfigureAwait(false);

            var (results, failed, suspended) = await FanOutBranchesAsync(run, definition, mapNode, body, terminal, scope, elements, plan, branchParallelism, nodeIterationKey, replay, cancellationToken).ConfigureAwait(false);

            // Any branch parked? The map itself suspends — its wait rows are persisted; the run stays
            // Suspended until every branch resolves. Propagated like a loop-body suspend (no node.completed).
            // This wins over a terminate-mode sibling failure (FanOutBranchesAsync defers it while suspended>0):
            // failing here would orphan the parked branches' durable waits + leak their dispatched work. The
            // terminate failure re-surfaces on the re-walk once no branch is parked.
            if (suspended > 0)
                throw new RunSuspendedException(mapNode.Id);

            var outputs = BuildMapOutputs(plan.ResultKey, results, failed);
            scope.Nodes[mapNode.Id] = outputs;
            await _recordLogger.NodeCompletedAsync(run.Id, mapNode.Id, nodeIterationKey, outputs, null, DateTimeOffset.UtcNow - startedAt, cancellationToken).ConfigureAwait(false);
            return NodeStatus.Success;
        }
        catch (NodeFailureException ex)
        {
            // A non-array binding, a tripped ceiling, or (terminate mode) a branch failure — expose it as
            // the map's `error` output so the map's own error edge can catch it, then write node.failed.
            scope.Nodes[mapNode.Id] = BuildErrorOutput(ex.Message, mapNode.Id);
            await _recordLogger.NodeFailedAsync(run.Id, mapNode.Id, nodeIterationKey, ex.Message, DateTimeOffset.UtcNow - startedAt, cancellationToken).ConfigureAwait(false);
            return NodeStatus.Failure;
        }
    }

    /// <summary>
    /// Dispatch every element-branch as a bounded-parallel batch (Task.WhenAll over a SemaphoreSlim) and
    /// collect the per-element terminal outputs into <c>results[index]</c>, IN ELEMENT ORDER (a fixed
    /// array indexed by element position, so task-completion order never reshuffles the reduce). An
    /// already-SETTLED branch (its terminal persisted on a prior engine invocation — see
    /// <paramref name="replay"/>) is NOT re-dispatched: its replayed result is slotted in directly, so a
    /// completed branch's side effect never re-fires on a sibling-triggered re-walk (the exactly-once-per-
    /// branch guarantee). A branch failure is honoured per the map's error policy: <c>terminate</c> rethrows
    /// the first branch's failure to fail the map; <c>continue</c> stores a failure marker at that index +
    /// counts it. A branch SUSPEND parks that branch's wait (PR2) and is counted in the returned suspended
    /// total — the caller suspends the whole map when it is &gt; 0. Returns the ordered results, the
    /// failed-branch count, and the suspended-branch count.
    /// </summary>
    private async Task<(IReadOnlyList<JsonElement> Results, int Failed, int Suspended)> FanOutBranchesAsync(WorkflowRun run, WorkflowDefinition definition, NodeDefinition mapNode, WorkflowDefinition body, NodeDefinition terminal, NodeRunScope scope, IReadOnlyList<JsonElement> elements, MapPlan plan, int branchParallelism, string nodeIterationKey, MapReplayState replay, CancellationToken cancellationToken)
    {
        if (elements.Count == 0) return (Array.Empty<JsonElement>(), 0, 0);

        using var gate = new SemaphoreSlim(branchParallelism);
        var tasks = new Task<MapBranchOutcome>[elements.Count];
        for (var i = 0; i < elements.Count; i++)
            tasks[i] = replay.Settled.TryGetValue(i, out var settled)
                ? Task.FromResult(settled)
                : RunMapBranchInChildScopeAsync(run, definition, mapNode, body, terminal, scope, elements[i], i, plan, nodeIterationKey, gate, cancellationToken);

        // BARRIER on every branch before deciding the map's fate — a terminate-mode failure is RETURNED (not
        // thrown), so awaiting the whole batch never tears down before the suspended siblings' outcomes are
        // observable. A genuine exception (cancel / secret-leak) still faults a task; WhenAllSettledAsync
        // surfaces it only after all branches finished, so it can't orphan a sibling's already-committed wait
        // either.
        var outcomes = await WhenAllBranchesSettleAsync(tasks).ConfigureAwait(false);

        var results = new JsonElement[elements.Count];
        var failed = 0;
        var suspended = 0;
        string? terminateFailure = null;
        foreach (var outcome in outcomes)
        {
            results[outcome.Index] = outcome.Result;
            if (outcome.Failed) failed++;
            if (outcome.Suspended) suspended++;
            terminateFailure ??= outcome.TerminateFailure;
        }

        // A terminate-mode failure yields to a suspended sibling: re-suspend the map (its durable waits live,
        // to be resumed) rather than failing it out from under the parked branch — failing here would orphan
        // that branch's Pending wait + leak its dispatched child/agent run. The terminate failure re-surfaces
        // on a later re-walk once no branch is parked. With no suspended branch it fails the map now.
        if (suspended == 0 && terminateFailure != null)
            throw new NodeFailureException(terminateFailure);

        return (results, failed, suspended);
    }

    /// <summary>
    /// Await EVERY branch task to completion — successful, faulted, or cancelled — then return the successful
    /// outcomes, rethrowing the FIRST genuine exception only after all branches settled. Unlike a bare
    /// <c>Task.WhenAll</c> (which throws on the first fault while siblings are mid-flight) this guarantees a
    /// branch that already committed a durable wait isn't abandoned by an early throw: the map can re-suspend
    /// on the observed suspended siblings instead.
    /// </summary>
    private static async Task<MapBranchOutcome[]> WhenAllBranchesSettleAsync(Task<MapBranchOutcome>[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Swallow here; the faulted task is re-observed below so the FIRST exception rethrows in order.
        }

        var faulted = tasks.FirstOrDefault(t => t.IsFaulted);
        if (faulted != null) await faulted.ConfigureAwait(false);   // rethrows that task's exception (never returns)

        return tasks.Select(t => t.Result).ToArray();
    }

    /// <summary>
    /// Run one element-branch in its OWN lifetime scope (its own DbContext + record logger), bounded by
    /// the fan-out semaphore — so two branches running at once never share an EF change-tracker (the same
    /// isolation <see cref="RunNodeInChildScopeAsync"/> gives parallel wave nodes). The branch body
    /// sub-walk happens on the child-scope engine instance. On a <c>terminate</c>-mode unhandled failure
    /// the branch throws <see cref="NodeFailureException"/>, which surfaces through Task.WhenAll to fail
    /// the whole map; <c>continue</c> mode returns the failure marker as that element's result.
    /// </summary>
    private async Task<MapBranchOutcome> RunMapBranchInChildScopeAsync(WorkflowRun run, WorkflowDefinition definition, NodeDefinition mapNode, WorkflowDefinition body, NodeDefinition terminal, NodeRunScope scope, JsonElement element, int index, MapPlan plan, string nodeIterationKey, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var childScope = _lifetimeScope.BeginLifetimeScope();
            var branchEngine = childScope.Resolve<WorkflowEngine>();
            return await branchEngine.RunMapBranchOnceAsync(run, definition, mapNode, body, terminal, scope, element, index, plan, nodeIterationKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The collected result of one map element-branch: its element index (for the ordered reduce), the
    /// branch terminal's output (or a failure marker), whether it failed (continue-mode marker), whether it
    /// SUSPENDED (parked its own wait under <c>"&lt;mapId&gt;#&lt;index&gt;"</c> — PR2), and — only in
    /// terminate mode — the message of an unhandled branch failure that should fail the whole map
    /// (<c>TerminateFailure</c>). A terminate failure is RETURNED rather than thrown so the fan-out can first
    /// barrier on EVERY branch: if any sibling suspended, the map re-suspends (its durable waits live, to be
    /// resumed) and the terminate failure is deferred to a later re-walk — never leaking a sibling's wait by
    /// failing the map out from under it. A suspended branch carries an empty result placeholder; the map
    /// re-suspends rather than reducing, so the placeholder is never emitted.
    /// </summary>
    private readonly record struct MapBranchOutcome(int Index, JsonElement Result, bool Failed, bool Suspended = false, string? TerminateFailure = null);

    /// <summary>
    /// Run a map body subgraph for ONE element as a parallel WAVE — the same model as the loop body
    /// (<see cref="RunLoopBodyOnceAsync"/>), under iteration key <c>"&lt;mapId&gt;#&lt;index&gt;"</c> and a
    /// per-element Iteration scope (<c>{{item}}</c> / <c>{{index}}</c>). Returns the branch's terminal-node
    /// output as that element's result. An unhandled body failure (a node that fails with no <c>error</c>
    /// edge of its own) is governed by the map's <paramref name="plan"/>: <c>terminate</c> throws
    /// <see cref="NodeFailureException"/> (fails the map); <c>continue</c> abandons the rest of the branch
    /// and returns a failure marker.
    ///
    /// <para>PR2: a body node that SUSPENDS parks the run durably — <see cref="SuspendNodeAsync"/> wrote
    /// this branch's <c>WorkflowRunWait</c> under <c>branchKey</c>, and <see cref="AdvanceMapBranchNodeOrAbandon"/>
    /// re-threw <see cref="RunSuspendedException"/> which we catch here, returning a Suspended outcome so the
    /// fan-out can re-suspend the whole map after EVERY branch settles. On a re-walk
    /// <see cref="RehydrateMapBranchAsync"/> settles this branch's finished body nodes + loads its resolved
    /// wait payload, so only the suspended node re-runs (with its decision) — the finished ones don't.</para>
    /// </summary>
    private async Task<MapBranchOutcome> RunMapBranchOnceAsync(WorkflowRun run, WorkflowDefinition definition, NodeDefinition mapNode, WorkflowDefinition body, NodeDefinition terminal, NodeRunScope scope, JsonElement element, int index, MapPlan plan, string nodeIterationKey, CancellationToken cancellationToken)
    {
        var branchScope = BuildMapBranchScope(scope, element, index);
        var branchKey = CombineIterationKey(nodeIterationKey, $"{mapNode.Id}#{index}");
        var branchParallelism = ResolveBodyParallelism(plan.MaxParallelism, _maxParallelism);

        var bodyState = new WalkerState(body);

        // Durable re-entry for this branch: if it already ran partway (a body node suspended on an earlier
        // engine invocation), settle the finished body nodes + inject the resolved wait payload, so the
        // suspended node re-runs with its decision and the completed ones aren't redone. A fresh branch
        // finds no rows under this key and this is a no-op.
        var stillSuspended = await RehydrateMapBranchAsync(run, body, bodyState, branchScope, branchKey, cancellationToken).ConfigureAwait(false);

        // Exactly-once on a partial re-walk: this branch still holds a Pending wait of its own (the immediate
        // single-wait resume path re-walks the map without a wait-for-all barrier, so a still-parked sibling
        // re-enters here). Re-running its suspended node would re-fire that node's side effect — re-mint an
        // approval token / re-stage an AgentRun. Short-circuit straight back to Suspended: the branch's wait
        // is intact and untouched, so it resumes correctly when ITS OWN signal arrives.
        if (stillSuspended)
            return new MapBranchOutcome(index, EmptyJsonObject(), Failed: false, Suspended: true);

        EnqueueReadyFrontier(bodyState, body);

        try
        {
            while (bodyState.Ready.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var wave = DrainReadyWave(bodyState);

                var regular = new List<NodeDefinition>(wave.Count);
                var containers = new List<NodeDefinition>();
                foreach (var node in wave)
                {
                    if (ShouldSkip(node, bodyState))
                    {
                        await MarkSkippedAsync(run, node, bodyState, cancellationToken, branchKey).ConfigureAwait(false);
                        EnqueueDownstreamWhenReady(node, bodyState);
                    }
                    else if (IsContainerNode(node)) containers.Add(node);
                    else regular.Add(node);
                }

                var outcomes = await RunReadyNodesAsync(run, regular, branchScope, bodyState, cancellationToken, branchKey, branchParallelism).ConfigureAwait(false);
                for (var i = 0; i < regular.Count; i++)
                {
                    MergeNodeOutcome(regular[i], outcomes[i], branchScope, bodyState);

                    var step = AdvanceMapBranchNodeOrAbandon(mapNode, regular[i], outcomes[i].Status, bodyState, plan.ErrorHandling, out var marker);
                    if (step == MapBranchStep.Abandon) return new MapBranchOutcome(index, marker, Failed: true);
                    if (step == MapBranchStep.Terminate) return TerminateOutcome(index, mapNode, regular[i]);
                }

                // A nested container (loop / try / map) recurses under THIS branch's key; it writes its own
                // scope/state sequentially, so it stays off the parallel batch — exactly like a loop body.
                foreach (var container in containers)
                {
                    var status = await ExecuteContainerAsync(run, definition, container, branchScope, bodyState, cancellationToken, branchKey).ConfigureAwait(false);
                    bodyState.Statuses[container.Id] = status;

                    var step = AdvanceMapBranchNodeOrAbandon(mapNode, container, status, bodyState, plan.ErrorHandling, out var marker);
                    if (step == MapBranchStep.Abandon) return new MapBranchOutcome(index, marker, Failed: true);
                    if (step == MapBranchStep.Terminate) return TerminateOutcome(index, mapNode, container);
                }
            }
        }
        catch (RunSuspendedException)
        {
            // A body node parked this branch's wait (already persisted by SuspendNodeAsync under branchKey).
            // Report Suspended so the fan-out re-suspends the whole map once all branches settle — never a
            // failure, never a completed reduce. The placeholder result is discarded on the re-suspend.
            return new MapBranchOutcome(index, EmptyJsonObject(), Failed: false, Suspended: true);
        }

        // The element's result is the terminal body node's output (validator enforces exactly one).
        var result = branchScope.Nodes.TryGetValue(terminal.Id, out var terminalOutputs)
            ? JsonSerializer.SerializeToElement(terminalOutputs)
            : EmptyJsonObject();

        return new MapBranchOutcome(index, result, Failed: false);
    }

    /// <summary>A terminate-mode unhandled branch failure, carried back as an outcome (not thrown) so the
    /// fan-out can barrier on every branch first — if any sibling SUSPENDED, the map re-suspends and this
    /// failure is deferred, never orphaning the sibling's durable wait.</summary>
    private static MapBranchOutcome TerminateOutcome(int index, NodeDefinition mapNode, NodeDefinition node) =>
        new(index, EmptyJsonObject(), Failed: true, TerminateFailure: $"Node '{node.Id}' in map '{mapNode.Id}' failed.");

    /// <summary>The disposition of one map body node after it ran: keep walking the branch, ABANDON it with a
    /// continue-mode failure marker, or TERMINATE the whole map with an unhandled failure.</summary>
    private enum MapBranchStep { Continue, Abandon, Terminate }

    /// <summary>
    /// The map-branch variant of <see cref="AdvanceBodyNodeOrAbandon"/>: a SUSPEND throws
    /// <see cref="RunSuspendedException"/> (PR2 — the branch's wait is already persisted; the catch in
    /// <see cref="RunMapBranchOnceAsync"/> turns it into a Suspended outcome so the map re-suspends). An
    /// unhandled FAILURE honours the map's error policy — <see cref="MapErrorHandling.Terminate"/> returns
    /// <see cref="MapBranchStep.Terminate"/> (the caller surfaces it as a <c>TerminateFailure</c> outcome so
    /// the fan-out can barrier first — a terminate must not orphan a SUSPENDED sibling's durable wait),
    /// <see cref="MapErrorHandling.Continue"/> returns <see cref="MapBranchStep.Abandon"/> with a failure
    /// marker so the branch is abandoned + counted. A node with its own <c>error</c> edge is handled
    /// regardless. Otherwise releases the ready downstream and returns <see cref="MapBranchStep.Continue"/>
    /// (marker is unused then).
    /// </summary>
    private MapBranchStep AdvanceMapBranchNodeOrAbandon(NodeDefinition mapNode, NodeDefinition node, NodeStatus status, WalkerState bodyState, MapErrorHandling errorHandling, out JsonElement marker)
    {
        marker = default;

        if (status == NodeStatus.Suspended)
            throw new RunSuspendedException(node.Id);

        if (status == NodeStatus.Failure && !HasErrorEdge(node, bodyState))
        {
            if (errorHandling == MapErrorHandling.Terminate)
                return MapBranchStep.Terminate;

            marker = BuildMapFailureMarker($"Node '{node.Id}' failed.", node.Id);
            return MapBranchStep.Abandon;
        }

        EnqueueDownstreamWhenReady(node, bodyState);
        return MapBranchStep.Continue;
    }

    /// <summary>Resolve the map's <c>items</c> input against the outer scope to a JSON array; a non-array binding is a clean NodeFailure (the map's error edge can catch it). A missing/null binding is treated as empty (a no-op).</summary>
    private static IReadOnlyList<JsonElement> ResolveMapElements(NodeDefinition mapNode, NodeRunScope scope)
    {
        var resolved = VariableResolver.ResolveBag(mapNode.Inputs, scope);

        if (!resolved.TryGetValue("items", out var items) || items.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Array.Empty<JsonElement>();

        if (items.ValueKind != JsonValueKind.Array)
            throw new NodeFailureException($"Map '{mapNode.Id}' input 'items' resolved to {items.ValueKind}, but a flow.map collection must be an array.");

        return items.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>
    /// The single body node that is the per-element result source: the lone TERMINAL of the body subgraph
    /// (a node with no in-body outgoing edge). Save-time validation enforces exactly one, so this is a
    /// safe single-pick at run time; a defensive throw guards a body that slipped through (e.g. a hand-
    /// written definition). The <c>flow.map_start</c> marker is never the terminal (it always has an
    /// outgoing edge to the body).
    /// </summary>
    private static NodeDefinition FindMapBodyTerminal(WorkflowDefinition body, string mapId)
    {
        var withOutgoing = body.Edges.Select(e => e.From).ToHashSet();
        var terminals = body.Nodes.Where(n => !withOutgoing.Contains(n.Id)).ToList();

        if (terminals.Count != 1)
            throw new NodeFailureException($"Map '{mapId}' body must end in exactly one terminal node (the per-element result); found {terminals.Count}.");

        return terminals[0];
    }

    /// <summary>The settled-branch replay set for a resuming map: element index → the branch's already-reconstructed outcome (its terminal output, replayed from the ledger). Branches NOT in this map re-run (fresh or from their suspended node).</summary>
    private readonly record struct MapReplayState(IReadOnlyDictionary<int, MapBranchOutcome> Settled);

    /// <summary>
    /// Array-reconstructing durable re-entry for a <c>flow.map</c> on a re-walk — the map analogue of
    /// <see cref="RehydrateLoopStateAsync"/> (which threads a scalar across COMPLETED loop passes; this
    /// collects per-index branch RESULTS). Reads every branch row under this map's key prefix
    /// (<c>"&lt;mapId&gt;#"</c>, scoped to the enclosing iteration key when nested), and for each branch
    /// index that reached a TERMINAL STATE on an earlier invocation reconstructs <c>results[index]</c> from
    /// the ledger — WITHOUT re-running the branch. A branch is terminal when:
    /// <list type="bullet">
    /// <item>its terminal body node settled <b>Success</b> — replay the terminal's persisted outputs;</item>
    /// <item>its terminal body node settled <b>Skipped</b> (the body routed around it, e.g. down an error
    ///   edge) — replay an empty result, mirroring the in-walk path where the terminal isn't in scope; or</item>
    /// <item>(continue mode only) it was <b>abandoned</b> — a body node FAILED with no in-body <c>error</c>
    ///   edge, which <see cref="AdvanceMapBranchNodeOrAbandon"/> counts as a failure marker. Replay the SAME
    ///   marker as a <c>Failed</c> outcome so the failing node never re-runs (re-firing its side effect) and
    ///   the reduce / failed-count stays identical across re-walks — the exactly-once-per-branch guarantee
    ///   on the continue-mode error path.</item>
    /// </list>
    /// That is the exactly-once-per-branch guarantee: a branch that already reached terminal (e.g. an
    /// <c>agent.code</c> that consumed its AgentRun, or a node that failed before being abandoned) is NEVER
    /// re-dispatched when a SIBLING branch's wait resolves and triggers a re-walk. A branch still PARKED (a
    /// node.suspended is its latest row; no terminal, no abandon-failure) is left out of the replay set so it
    /// re-runs from its suspended node (via <see cref="RehydrateMapBranchAsync"/>). A terminate-mode failure
    /// is deliberately NOT settled — the map must fail on it, so it re-runs and re-fails on the next walk
    /// where no branch is parked. A fresh run finds no rows and replays nothing. Results are keyed by ELEMENT
    /// INDEX, never by completion / resume order, so the ordered reduce survives every re-walk.
    /// </summary>
    private async Task<MapReplayState> RehydrateMapResultsAsync(WorkflowRun run, NodeDefinition mapNode, WorkflowDefinition body, NodeDefinition terminal, int elementCount, MapErrorHandling errorHandling, string nodeIterationKey, CancellationToken cancellationToken)
    {
        if (elementCount == 0) return new MapReplayState(new Dictionary<int, MapBranchOutcome>());

        // This map's direct branches are keyed CombineIterationKey(nodeIterationKey, "<mapId>#<i>"); a
        // nested descendant extends that with "/<inner>#<j>". We attribute a row to branch index i by the
        // integer right after the prefix, up to the next '/', so a nested branch's rows count under the
        // correct OUTER branch (mirrors LoopIterationIndex).
        var branchKeyPrefix = CombineIterationKey(nodeIterationKey, $"{mapNode.Id}#");
        var rows = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == run.Id && n.IterationKey.StartsWith(branchKeyPrefix))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return new MapReplayState(new Dictionary<int, MapBranchOutcome>());

        // Group the prefix-scoped rows by their EXACT iteration key ONCE so each branch settle is an O(1)
        // lookup of its own-key rows, not an O(rows) re-scan — the whole rehydrate is O(K), not O(K^2) for K
        // branches. GroupBy preserves source order within each group, so the per-branch row order (and thus
        // FirstOrDefault selection) is identical to the previous rows.Where(...) scan: behaviour-preserving.
        var rowsByKey = rows.GroupBy(r => r.IterationKey).ToDictionary(g => g.Key, g => (IReadOnlyList<Core.Persistence.Entities.WorkflowRunNode>)g.ToList());

        var settled = new Dictionary<int, MapBranchOutcome>();

        for (var i = 0; i < elementCount; i++)
        {
            var branchKey = CombineIterationKey(nodeIterationKey, $"{mapNode.Id}#{i}");

            if (TrySettleBranch(rowsByKey, branchKey, body, terminal, errorHandling, i, out var outcome))
                settled[i] = outcome;
        }

        return new MapReplayState(settled);
    }

    /// <summary>
    /// Classify ONE map branch from its ledger rows: replay it (and how) if it already reached a terminal
    /// state, else leave it to re-run. Returns <c>false</c> for a branch that is still parked, partially run,
    /// not started, or failed under TERMINATE mode (which must re-fail, not settle). Reads only this branch's
    /// OWN-key rows (a nested descendant has a longer key), exactly as the live walk reduces a branch from
    /// its own-key terminal.
    /// </summary>
    private static bool TrySettleBranch(IReadOnlyDictionary<string, IReadOnlyList<Core.Persistence.Entities.WorkflowRunNode>> rowsByKey, string branchKey, WorkflowDefinition body, NodeDefinition terminal, MapErrorHandling errorHandling, int index, out MapBranchOutcome outcome)
    {
        outcome = default;

        var ownRows = rowsByKey.GetValueOrDefault(branchKey) ?? Array.Empty<Core.Persistence.Entities.WorkflowRunNode>();

        var terminalRow = ownRows.FirstOrDefault(r => r.NodeId == terminal.Id);

        // Completed normally: terminal settled Success → replay its outputs; Skipped → replay an empty
        // result (the in-walk path yields EmptyJsonObject when the terminal isn't in scope).
        if (terminalRow is { Status: NodeStatus.Success })
        {
            outcome = new MapBranchOutcome(index, JsonSerializer.SerializeToElement(ParsePayloadObject(terminalRow.OutputsJson)), Failed: false);
            return true;
        }

        if (terminalRow is { Status: NodeStatus.Skipped })
        {
            outcome = new MapBranchOutcome(index, EmptyJsonObject(), Failed: false);
            return true;
        }

        // Abandoned under continue mode: a body node FAILED with no in-body error edge (the abandon point).
        // Replay the SAME failure marker AdvanceMapBranchNodeOrAbandon produced, so the node never re-runs.
        if (errorHandling == MapErrorHandling.Continue)
        {
            var abandonRow = ownRows.FirstOrDefault(r => r.Status == NodeStatus.Failure && !HasErrorEdgeInDefinition(body, r.NodeId));
            if (abandonRow != null)
            {
                outcome = new MapBranchOutcome(index, BuildMapFailureMarker($"Node '{abandonRow.NodeId}' failed.", abandonRow.NodeId), Failed: true);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Durable re-entry for ONE map element-branch — the branch analogue of <see cref="RehydrateLoopBodyAsync"/>.
    /// Settles the branch's body nodes that already finished (Success / Skipped) into the branch walker +
    /// scope, recomputes their edge-liveness, and loads the resolved wait payload for THIS branch key so the
    /// suspended body node re-runs with its decision. Scoped to the branch key so branch i's resolved wait
    /// never bleeds into branch j. No-op for a fresh branch (no rows under this key).
    ///
    /// <para>Returns <c>true</c> when this branch is STILL SUSPENDED — it has a Pending wait of its own (it
    /// parked on an earlier invocation and the wait has NOT resolved). On a sibling-triggered re-walk via the
    /// immediate single-wait path (<see cref="WorkflowResumeService.ResumeWaitAsync"/>, used by
    /// approval/action/callback/timer — no wait-for-all barrier), branch i may re-enter while its OWN wait is
    /// still Pending; re-running its suspended node then re-fires that node's side effect (mints a fresh
    /// token / re-stages a run) — an exactly-once violation. The caller short-circuits a still-suspended
    /// branch back to a Suspended outcome instead of re-walking it.</para>
    /// </summary>
    private async Task<bool> RehydrateMapBranchAsync(WorkflowRun run, WorkflowDefinition body, WalkerState bodyState, NodeRunScope branchScope, string branchKey, CancellationToken cancellationToken)
    {
        var rows = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == run.Id && n.IterationKey == branchKey)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return false;

        foreach (var row in rows)
        {
            // Only Success / Skipped settle (and so won't re-run). The suspended node is left unsettled so
            // EnqueueReadyFrontier re-runs it — with the resolved payload loaded below.
            if (row.Status is not (NodeStatus.Success or NodeStatus.Skipped)) continue;

            bodyState.Statuses[row.NodeId] = row.Status;

            if (row.Status != NodeStatus.Success) continue;

            var outputs = ParsePayloadObject(row.OutputsJson);
            if (outputs.Count > 0) branchScope.Nodes[row.NodeId] = outputs;

            var hints = ParseRoutingHints(row.RoutingHintsJson);
            if (hints != null) bodyState.RoutingHints[row.NodeId] = new HashSet<string>(hints);
        }

        foreach (var edge in body.Edges)
        {
            if (!bodyState.Statuses.TryGetValue(edge.From, out var sourceStatus)) continue;
            bodyState.EdgeLive[edge] = IsEdgeLive(edge, sourceStatus, bodyState.RoutingHints.GetValueOrDefault(edge.From));
        }

        var waits = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == run.Id && w.IterationKey == branchKey)
            .Select(w => new { w.NodeId, w.Status, w.PayloadJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var w in waits.Where(w => w.Status == WorkflowWaitStatuses.Resolved))
            bodyState.ResumePayloads[w.NodeId] = string.IsNullOrWhiteSpace(w.PayloadJson) ? EmptyJsonObject() : JsonDocument.Parse(w.PayloadJson).RootElement.Clone();

        // This branch is still parked iff it has a Pending wait of its own. The single-wait immediate-resume
        // path re-walks the map with no wait-for-all barrier, so a still-pending sibling re-enters here.
        return waits.Any(w => w.Status == WorkflowWaitStatuses.Pending);
    }

    /// <summary>
    /// Build a per-element branch scope: the outer read-side buckets (incl. Projects + SecretPaths) plus
    /// the Iteration slot set to a FRESH <c>{{item}}</c> (the element) + <c>{{index}}</c> — mirroring
    /// <c>FlowIterateNode.BuildIterationScope</c> so the body reads bare <c>{{item}}</c> / <c>{{index}}</c>.
    /// A fresh Nodes bag is seeded from the outer scope so the body can read pre-map node outputs.
    ///
    /// <para><b>Nested-map shadowing is INTENTIONAL, not a bug.</b> Unlike <see cref="BuildLoopScope"/>
    /// (which sets <c>Iteration = outer.Iteration</c> to PASS THROUGH an enclosing iterate's element),
    /// a map REPLACES the Iteration slot with its own <c>{item, index}</c> and does NOT inherit the
    /// outer's. This is deliberate: a map uses the FIXED names <c>{{item}}</c> / <c>{{index}}</c>, so an
    /// inner map that inherited the outer would COLLIDE on those exact keys — the inner element would
    /// shadow the outer one anyway, making "inheritance" a no-op at best and a confusing partial overlay
    /// at worst. So the contract is: an inner map branch sees ITS OWN element as <c>{{item}}</c> /
    /// <c>{{index}}</c>; the OUTER element is not in scope by default. (A node that needs the outer
    /// element passes it down explicitly — e.g. by binding it into the inner map's <c>items</c> shape, or
    /// reading a pre-map node output via <c>{{nodes.&lt;id&gt;.outputs.X}}</c>, which the seeded Nodes bag
    /// preserves.) This contrasts with the loop's NAMED <c>{{loop.&lt;var&gt;}}</c> scope, which lives in a
    /// separate slot and so can coexist with an enclosing iterate's <c>{{item}}</c> without collision —
    /// which is exactly why the loop inherits and the map shadows. Mirrored in the <c>FlowMapNode</c> doc.</para>
    /// <para>internal (not private) so the shadowing contract is unit-pinned directly via InternalsVisibleTo —
    /// see <c>MapBranchScopeTests</c> — alongside <see cref="BuildLoopScope"/>'s inheriting contract.</para>
    /// </summary>
    internal static NodeRunScope BuildMapBranchScope(NodeRunScope outer, JsonElement element, int index)
    {
        var iteration = new Dictionary<string, JsonElement>
        {
            ["item"] = element.Clone(),
            ["index"] = JsonSerializer.SerializeToElement(index),
        };

        var scope = new NodeRunScope
        {
            Trigger = outer.Trigger, Team = outer.Team, Wf = outer.Wf, Input = outer.Input,
            Sys = outer.Sys, Projects = outer.Projects, SecretPaths = outer.SecretPaths,
            Iteration = iteration,
        };

        foreach (var (k, v) in outer.Nodes) scope.Nodes[k] = v;
        return scope;
    }

    /// <summary>The reduced map output: the ordered per-element results under the configured key, the element count, and the failed-branch count (always 0 under terminate, since a failure throws instead). internal (not private) so the reserved-set drift pin (<c>ReducerEmittedKeysPinTests</c>) asserts over the keys this actually emits, catching a new key the author forgets to add to <c>WorkflowOutputKeys.Map</c>.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> BuildMapOutputs(string resultKey, IReadOnlyList<JsonElement> results, int failed) =>
        new Dictionary<string, JsonElement>
        {
            [resultKey] = JsonSerializer.SerializeToElement(results),
            [WorkflowOutputKeys.MapCount] = JsonSerializer.SerializeToElement(results.Count),
            [WorkflowOutputKeys.MapFailed] = JsonSerializer.SerializeToElement(failed),
        };

    /// <summary>A continue-on-error element's placeholder result — <c>{ "error": { "message": ..., "node": ... } }</c> — mirroring the node error-output shape so a synthesizer can spot a failed element in <c>results[i].error</c>.</summary>
    private static JsonElement BuildMapFailureMarker(string message, string nodeId) =>
        JsonSerializer.SerializeToElement(BuildErrorOutput(message, nodeId));

    private static readonly JsonSerializerOptions MapConfigJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static MapConfig ParseMapConfig(JsonElement config) =>
        config.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<MapConfig>(config.GetRawText(), MapConfigJsonOptions) ?? new MapConfig()
            : new MapConfig();

    /// <summary>The result of one loop-body pass: how many body nodes ran (for the node budget) and whether the pass failed under continue-on-error.</summary>
    private readonly record struct LoopBodyOutcome(int Executed, bool Failed);

    /// <summary>
    /// Run the loop body subgraph for ONE iteration as a parallel WAVE — the same model as the
    /// top-level walk, with the iteration key set so each pass lands under its own ledger rows.
    /// Independent body nodes run concurrently (each in its own DI scope); a single-node body keeps the
    /// sequential in-scope fast path. Returns the body nodes executed + whether the pass failed. An unhandled body
    /// failure (a node that fails with no <c>error</c> edge of its own) is governed by
    /// <paramref name="errorHandling"/>: <see cref="LoopErrorHandling.Terminate"/> throws (the loop
    /// fails); <see cref="LoopErrorHandling.Continue"/> abandons the rest of this pass and reports
    /// <c>Failed</c> so the loop skips to the next iteration. A body node that SUSPENDS (approval /
    /// sleep / callback / sub-workflow) parks the run durably: the pass rehydrates from the ledger on
    /// resume so the suspended node re-runs with its payload and the already-finished body nodes
    /// don't. A nested loop recurses into <see cref="ExecuteLoopAsync"/> under this pass's key.
    /// </summary>
    private async Task<LoopBodyOutcome> RunLoopBodyOnceAsync(WorkflowRun run, WorkflowDefinition definition, WorkflowDefinition body, NodeRunScope iterScope, string iterationKey, LoopErrorHandling errorHandling, int maxParallelism, CancellationToken cancellationToken)
    {
        var bodyState = new WalkerState(body);

        // Durable re-entry for this pass: if it already ran partway (a body node suspended on an earlier
        // engine invocation), settle the nodes that finished + inject the resolved wait payload, so the
        // suspended node re-runs with its decision and the completed ones aren't redone. A fresh pass
        // finds no rows under this iteration key and this is a no-op.
        await RehydrateLoopBodyAsync(run, body, bodyState, iterScope, iterationKey, cancellationToken).ConfigureAwait(false);

        EnqueueReadyFrontier(bodyState, body);
        var executed = 0;

        // Walk the body by WAVES — the same model as the top-level walk (WalkGraphAsync): drain the
        // ready frontier, settle skips, run the independent regular body nodes as a bounded parallel
        // batch (each in its own DI scope, under THIS pass's iteration key), then nested loops
        // sequentially. A single ready body node keeps the in-scope fast path, so a linear body is
        // behaviour-identical. The body's twists vs the top level: count executions for the loop's
        // node budget, and honour the loop's error policy on an unhandled failure (terminate ⇒ throw;
        // continue ⇒ abandon the rest of this pass + report Failed so the loop skips to the next pass).
        // A nested-loop / body suspend throws RunSuspendedException, parking the whole run durably; on
        // resume RehydrateLoopBodyAsync settles the finished body nodes (incl. parallel siblings) and
        // re-runs only the suspended one.
        while (bodyState.Ready.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var wave = DrainReadyWave(bodyState);

            var regular = new List<NodeDefinition>(wave.Count);
            var containers = new List<NodeDefinition>();
            foreach (var node in wave)
            {
                if (ShouldSkip(node, bodyState))
                {
                    await MarkSkippedAsync(run, node, bodyState, cancellationToken, iterationKey).ConfigureAwait(false);
                    EnqueueDownstreamWhenReady(node, bodyState);
                }
                else if (IsContainerNode(node)) containers.Add(node);
                else regular.Add(node);
            }

            // Regular body nodes run concurrently under this pass's iteration key; all of them ran, so
            // they all count against the budget. Merge + advance in drain order (single-threaded).
            var outcomes = await RunReadyNodesAsync(run, regular, iterScope, bodyState, cancellationToken, iterationKey, maxParallelism).ConfigureAwait(false);
            executed += regular.Count;
            for (var i = 0; i < regular.Count; i++)
            {
                MergeNodeOutcome(regular[i], outcomes[i], iterScope, bodyState);
                if (AdvanceBodyNodeOrAbandon(regular[i], outcomes[i].Status, bodyState, errorHandling))
                    return new LoopBodyOutcome(executed, Failed: true);
            }

            // A nested container (loop / try) recurses under THIS pass's key (its body keys become
            // "<thisKey>/<inner>#<j>", collision-free across outer passes); it writes its own scope/state
            // sequentially, so it stays off the parallel batch — exactly like a top-level container.
            foreach (var container in containers)
            {
                executed++;
                var status = await ExecuteContainerAsync(run, definition, container, iterScope, bodyState, cancellationToken, iterationKey).ConfigureAwait(false);
                bodyState.Statuses[container.Id] = status;
                if (AdvanceBodyNodeOrAbandon(container, status, bodyState, errorHandling))
                    return new LoopBodyOutcome(executed, Failed: true);
            }
        }

        return new LoopBodyOutcome(executed, Failed: false);
    }

    /// <summary>Where a resuming loop picks back up: the in-progress iteration index (also the completed-pass count), the failed-pass count, and the threaded loop vars as they stood at the START of that iteration.</summary>
    private readonly record struct LoopResumeState(int ResumeFrom, int Iterations, int Failures, Dictionary<string, JsonElement> LoopVars);

    /// <summary>
    /// Rebuild a loop's progress from the per-iteration ledger so a run resumed after a body suspend
    /// re-enters at the right pass with the right state — never restarting at iteration 0. Replays the
    /// COMPLETED passes (every iteration before the highest index present) to re-thread loop variables
    /// and re-count failed passes exactly as the live loop did; the highest index is the in-progress
    /// (suspended) pass, which <see cref="RunLoopBodyOnceAsync"/> rehydrates and finishes. A fresh run
    /// (no body rows) returns ResumeFrom 0 with freshly-initialised vars — behaviour-identical to before.
    /// </summary>
    private async Task<LoopResumeState> RehydrateLoopStateAsync(WorkflowRun run, NodeDefinition loopNode, WorkflowDefinition body, LoopConfig config, NodeRunScope outerScope, string nodeIterationKey, CancellationToken cancellationToken)
    {
        var loopVars = InitLoopVars(config.LoopVariables, outerScope);

        // This loop's direct body iterations are keyed CombineIterationKey(nodeIterationKey, "<loopId>#<i>");
        // nested descendants extend that with "/<inner>#<j>". We read the index UP TO the first '/' so a
        // nested loop's rows attribute to the correct OUTER iteration, not the inner index.
        var bodyKeyPrefix = CombineIterationKey(nodeIterationKey, $"{loopNode.Id}#");
        var rows = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == run.Id && n.IterationKey.StartsWith(bodyKeyPrefix))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return new LoopResumeState(0, 0, 0, loopVars);

        var resumeFrom = rows.Max(r => LoopIterationIndex(r.IterationKey, bodyKeyPrefix));
        var failures = 0;

        for (var j = 0; j < resumeFrom; j++)
        {
            // Only THIS loop's direct body rows of pass j (key == the pass key exactly). Nested
            // descendants have longer keys and don't carry this loop's own body-node outputs.
            var passKey = CombineIterationKey(nodeIterationKey, $"{loopNode.Id}#{j}");
            var passRows = rows.Where(r => r.IterationKey == passKey).ToList();

            // A pass "failed" iff a body node failed with no error edge of its own (continue-mode skipped
            // it). Mirror the live loop exactly: a failed pass threads NO loop-var updates forward.
            if (passRows.Any(r => r.Status == NodeStatus.Failure && !HasErrorEdgeInDefinition(body, r.NodeId)))
            {
                failures++;
                continue;
            }

            // Rebuild this pass's body scope (outer nodes + this pass's body outputs) so an update ref
            // like {{loop.acc}}:{{loop.index}} or {{nodes.<body>.outputs.*}} resolves to the same value
            // the live pass produced, then re-apply the var updates.
            var passNodes = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>();
            foreach (var (k, v) in outerScope.Nodes) passNodes[k] = v;
            foreach (var r in passRows.Where(r => r.Status == NodeStatus.Success)) passNodes[r.NodeId] = ParsePayloadObject(r.OutputsJson);

            loopVars = ApplyLoopVarUpdates(config.LoopVariables, loopVars, BuildLoopScope(outerScope, passNodes, loopVars, j));
        }

        return new LoopResumeState(resumeFrom, resumeFrom, failures, loopVars);
    }

    /// <summary>
    /// Durable re-entry for ONE loop iteration: settle the body nodes that already finished (Success /
    /// Skipped) into the pass's walker + scope, recompute their edge-liveness, and load the resolved
    /// wait payload for THIS iteration so the suspended body node re-runs with its decision. Scoped to
    /// the iteration key so iteration N's resolved wait never bleeds into iteration N+1. No-op for a
    /// fresh pass (no rows under this key).
    /// </summary>
    private async Task RehydrateLoopBodyAsync(WorkflowRun run, WorkflowDefinition body, WalkerState bodyState, NodeRunScope iterScope, string iterationKey, CancellationToken cancellationToken)
    {
        var rows = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == run.Id && n.IterationKey == iterationKey)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            // Only Success / Skipped settle (and so won't re-run). The suspended node is left unsettled
            // so EnqueueReadyFrontier re-runs it — with the resolved payload loaded below.
            if (row.Status is not (NodeStatus.Success or NodeStatus.Skipped)) continue;

            bodyState.Statuses[row.NodeId] = row.Status;

            if (row.Status != NodeStatus.Success) continue;

            var outputs = ParsePayloadObject(row.OutputsJson);
            if (outputs.Count > 0) iterScope.Nodes[row.NodeId] = outputs;

            var hints = ParseRoutingHints(row.RoutingHintsJson);
            if (hints != null) bodyState.RoutingHints[row.NodeId] = new HashSet<string>(hints);
        }

        foreach (var edge in body.Edges)
        {
            if (!bodyState.Statuses.TryGetValue(edge.From, out var sourceStatus)) continue;
            bodyState.EdgeLive[edge] = IsEdgeLive(edge, sourceStatus, bodyState.RoutingHints.GetValueOrDefault(edge.From));
        }

        var resolved = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == run.Id && w.IterationKey == iterationKey && w.Status == WorkflowWaitStatuses.Resolved)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var w in resolved)
            bodyState.ResumePayloads[w.NodeId] = string.IsNullOrWhiteSpace(w.PayloadJson) ? EmptyJsonObject() : JsonDocument.Parse(w.PayloadJson).RootElement.Clone();
    }

    // The three iteration-key helpers below are the nested-loop key backbone (durable resume parses
    // them to re-enter the right pass). internal (not private) so they're unit-pinned directly via
    // InternalsVisibleTo — see WorkflowEngineIterationKeyTests — not only through integration coverage.

    /// <summary>Append a loop-iteration segment to an iteration-key path: top level ("") → just the segment; nested → "&lt;prefix&gt;/&lt;segment&gt;", so keys never collide across the enclosing loop's passes.</summary>
    internal static string CombineIterationKey(string prefix, string segment) =>
        prefix.Length == 0 ? segment : $"{prefix}/{segment}";

    /// <summary>The iteration index a key belongs to for the loop whose body keys start with <paramref name="bodyKeyPrefix"/> — the integer right after the prefix, up to the next '/' (so a nested descendant attributes to its OUTER pass). -1 when the key isn't in this loop's subtree.</summary>
    internal static int LoopIterationIndex(string key, string bodyKeyPrefix)
    {
        if (!key.StartsWith(bodyKeyPrefix, StringComparison.Ordinal)) return -1;
        var rest = key.AsSpan(bodyKeyPrefix.Length);
        var slash = rest.IndexOf('/');
        return int.TryParse(slash >= 0 ? rest[..slash] : rest, out var i) ? i : -1;
    }

    /// <summary>How many loops enclose a node running at this iteration key (the number of "&lt;loop&gt;#&lt;i&gt;" path segments). 0 = top-level.</summary>
    internal static int LoopNestingDepth(string nodeIterationKey) =>
        nodeIterationKey.Length == 0 ? 0 : nodeIterationKey.Count(c => c == '/') + 1;

    /// <summary>True when the body definition has an outgoing <c>error</c>-handle edge from the node — i.e. its failure is handled in-body (distinct from <see cref="HasErrorEdge"/>, which reads a live WalkerState).</summary>
    private static bool HasErrorEdgeInDefinition(WorkflowDefinition body, string nodeId) =>
        body.Edges.Any(e => e.From == nodeId && e.SourceHandle == WorkflowHandles.Error);

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyJsonBag = new Dictionary<string, JsonElement>();
    private static readonly JsonSerializerOptions LoopConfigJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static LoopConfig ParseLoopConfig(JsonElement config) =>
        config.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<LoopConfig>(config.GetRawText(), LoopConfigJsonOptions) ?? new LoopConfig()
            : new LoopConfig();

    /// <summary>Build a per-iteration scope: outer read-slots + <c>loop.*</c> (vars + injected <c>index</c>) + a fresh Nodes bag seeded from <paramref name="nodesSource"/> (so the body reads pre-loop outputs, and termination reads this pass's body outputs). internal (not private) so the reserved-set drift pin (<c>ReducerEmittedKeysPinTests</c>) asserts the injected <c>loop.*</c> keys (the <c>index</c>) are in <c>WorkflowOutputKeys.Loop</c>.</summary>
    internal static NodeRunScope BuildLoopScope(NodeRunScope outer, IEnumerable<KeyValuePair<string, IReadOnlyDictionary<string, JsonElement>>> nodesSource, IReadOnlyDictionary<string, JsonElement> loopVars, int index)
    {
        var loop = new Dictionary<string, JsonElement>(loopVars) { [WorkflowOutputKeys.LoopIndex] = JsonSerializer.SerializeToElement(index) };

        var scope = new NodeRunScope
        {
            Trigger = outer.Trigger, Team = outer.Team, Wf = outer.Wf, Input = outer.Input,
            Sys = outer.Sys, Projects = outer.Projects, SecretPaths = outer.SecretPaths,
            Iteration = outer.Iteration, Loop = loop,
        };

        foreach (var (k, v) in nodesSource) scope.Nodes[k] = v;
        return scope;
    }

    private static Dictionary<string, JsonElement> InitLoopVars(IReadOnlyList<LoopVariable> vars, NodeRunScope outer)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var v in vars)
            result[v.Name] = v.Ref != null ? VariableResolver.Resolve(JsonSerializer.SerializeToElement(v.Ref), outer) : v.Value ?? ToJsonNull();
        return result;
    }

    private static Dictionary<string, JsonElement> ApplyLoopVarUpdates(IReadOnlyList<LoopVariable> vars, Dictionary<string, JsonElement> current, NodeRunScope iterScope)
    {
        var result = new Dictionary<string, JsonElement>(current);
        foreach (var v in vars)
            if (v.Update != null) result[v.Name] = VariableResolver.Resolve(JsonSerializer.SerializeToElement(v.Update), iterScope);
        return result;
    }

    /// <summary>Evaluate the termination set against the loop scope; an empty/absent set never terminates (rely on the cap).</summary>
    private static bool IsTerminationMet(LoopTermination? termination, NodeRunScope scope)
    {
        if (termination == null || termination.Conditions.Count == 0) return false;

        bool Met(LoopCondition c) => ConditionEvaluator.CompareValues(c.Op, VariableResolver.Resolve(JsonSerializer.SerializeToElement(c.Ref), scope), c.Value);

        return termination.Logic.Equals("or", StringComparison.OrdinalIgnoreCase) ? termination.Conditions.Any(Met) : termination.Conditions.All(Met);
    }

    /// <summary>The reduced loop output: the threaded loop vars spread first, then iterations / failedIterations / terminationReason written after (so a same-named var is clobbered). internal (not private) so the reserved-set drift pin (<c>ReducerEmittedKeysPinTests</c>) asserts over the keys this actually emits beyond the vars, catching a new key the author forgets to add to <c>WorkflowOutputKeys.Loop</c>.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> BuildLoopOutputs(Dictionary<string, JsonElement> loopVars, int iterations, int failedIterations, string reason)
    {
        var outputs = new Dictionary<string, JsonElement>(loopVars)
        {
            [WorkflowOutputKeys.LoopIterations] = JsonSerializer.SerializeToElement(iterations),
            [WorkflowOutputKeys.LoopFailedIterations] = JsonSerializer.SerializeToElement(failedIterations),
            [WorkflowOutputKeys.LoopTerminationReason] = JsonSerializer.SerializeToElement(reason),
        };
        return outputs;
    }

    /// <summary>
    /// Engine v2 Phase 0 — reconstruct the walker's settled state from the durable ledger (the
    /// <c>workflow_run_node</c> view) so a resumed run continues instead of restarting. Loads
    /// each settled node's status, its output bag (into <c>scope.Nodes</c>) and branch routing
    /// hints, then recomputes edge-liveness for every settled source. A node still mid-flight
    /// (only node.started → Running) is NOT settled and will re-run. A node that already FAILED
    /// short-circuits the run to Failure, mirroring the in-walk halt. For a fresh run (zero
    /// persisted nodes) this returns immediately and the walk proceeds exactly as before.
    /// </summary>
    private async Task RehydrateFromLedgerAsync(WorkflowRun run, WorkflowDefinition definition, NodeRunScope scope, WalkerState state, CancellationToken cancellationToken)
    {
        // Only top-level rows (iteration_key == "") rebuild the top-level walk. Loop-body rows are
        // keyed "<loopId>#<i>" and belong to a loop's internal per-iteration sub-walk, not here — a
        // loop container re-runs its whole body atomically (PR-L2b), so they never settle top-level state.
        var persisted = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == run.Id && n.IterationKey == NoIteration)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (persisted.Count == 0) return;

        foreach (var node in persisted)
        {
            // A persisted failure: re-fail the run UNLESS the node has an `error` edge — then the
            // original walk handled it by routing to the error branch, so settle it as a (failed)
            // source and rebuild its `error` output for the handler. Mirrors the in-walk path so a
            // re-dispatched run continues identically instead of re-failing.
            if (node.Status == NodeStatus.Failure)
            {
                if (!definition.Edges.Any(e => e.From == node.NodeId && e.SourceHandle == WorkflowHandles.Error))
                    throw new NodeFailureException($"Node '{node.NodeId}' failed.");

                state.Statuses[node.NodeId] = NodeStatus.Failure;
                scope.Nodes[node.NodeId] = BuildErrorOutput(node.Error, node.NodeId);
                continue;
            }

            // Only Success / Skipped are "settled". Running (node.started with no terminal record)
            // re-runs — the engine crashed mid-node, so we re-execute it.
            if (node.Status is not (NodeStatus.Success or NodeStatus.Skipped)) continue;

            state.Statuses[node.NodeId] = node.Status;

            if (node.Status != NodeStatus.Success) continue;

            var outputs = ParsePayloadObject(node.OutputsJson);
            if (outputs.Count > 0) scope.Nodes[node.NodeId] = outputs;

            var hints = ParseRoutingHints(node.RoutingHintsJson);
            if (hints != null) state.RoutingHints[node.NodeId] = new HashSet<string>(hints);
        }

        // Recompute edge-liveness for every edge whose source has settled, so the resumed
        // frontier + ShouldSkip see the same EdgeLive a single-pass walk would have produced.
        foreach (var edge in definition.Edges)
        {
            if (!state.Statuses.TryGetValue(edge.From, out var sourceStatus)) continue;
            state.EdgeLive[edge] = IsEdgeLive(edge, sourceStatus, state.RoutingHints.GetValueOrDefault(edge.From));
        }

        RehydrateTerminalOutputs(definition, scope, state, persisted);

        // Load any resolved waits so a resumed node sees its ResumePayload on re-run.
        await LoadResolvedWaitsAsync(run.Id, state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuild the run's declared outputs from an already-completed Terminal (the narrow crash
    /// window where the Terminal ran but the run row never flipped to Success). Re-resolves the
    /// Terminal's Inputs against the rebuilt scope — deterministic, no node re-execution. Last
    /// completed Terminal wins, matching the in-walk <see cref="CaptureTerminalOutputs"/> rule.
    /// </summary>
    private void RehydrateTerminalOutputs(WorkflowDefinition definition, NodeRunScope scope, WalkerState state, IReadOnlyList<WorkflowRunNode> persisted)
    {
        var completedAt = persisted
            .Where(n => n.Status == NodeStatus.Success)
            .ToDictionary(n => n.NodeId, n => n.CompletedAt ?? DateTimeOffset.MinValue);

        NodeDefinition? lastTerminal = null;
        var lastCompleted = DateTimeOffset.MinValue;

        foreach (var node in definition.Nodes)
        {
            if (!completedAt.TryGetValue(node.Id, out var when)) continue;
            if (_nodeRegistry.Resolve(node.TypeKey).Manifest.Kind != NodeKind.Terminal) continue;

            if (lastTerminal == null || when >= lastCompleted) { lastTerminal = node; lastCompleted = when; }
        }

        if (lastTerminal != null)
            state.WorkflowOutputs = VariableResolver.ResolveBag(lastTerminal.Inputs, scope);
    }

    /// <summary>
    /// Enqueue every not-yet-settled node whose incoming edges have all settled. For a fresh run
    /// (no settled nodes) this is exactly the set of roots (nodes with no incoming edge — the
    /// Trigger), iterated in definition order, so the walk starts identically to the original
    /// EnqueueRoots. For a resumed run it is the boundary between done and not-yet-done work.
    /// </summary>
    private static void EnqueueReadyFrontier(WalkerState state, WorkflowDefinition definition)
    {
        foreach (var node in definition.Nodes)
        {
            if (state.Statuses.ContainsKey(node.Id)) continue;

            var incoming = state.IncomingByNodeId.GetValueOrDefault(node.Id, Array.Empty<EdgeDefinition>());
            if (incoming.All(e => state.Statuses.ContainsKey(e.From)) && !state.Ready.Contains(node.Id))
                state.Ready.Enqueue(node.Id);
        }
    }

    private void EnqueueDownstreamWhenReady(NodeDefinition source, WalkerState state)
    {
        var routingHints = state.RoutingHints.GetValueOrDefault(source.Id);
        var sourceStatus = state.Statuses[source.Id];

        foreach (var edge in state.OutgoingByNodeId.GetValueOrDefault(source.Id, Array.Empty<EdgeDefinition>()))
        {
            state.EdgeLive[edge] = IsEdgeLive(edge, sourceStatus, routingHints);

            // A target becomes "ready to consider" once EVERY incoming edge's source has fired
            // (succeeded, skipped, or failed). The ShouldSkip check decides whether it actually
            // executes or just gets marked Skipped.
            var allIncomingSettled = state.IncomingByNodeId.GetValueOrDefault(edge.To, Array.Empty<EdgeDefinition>())
                .All(e => state.Statuses.ContainsKey(e.From));

            if (allIncomingSettled && !state.Statuses.ContainsKey(edge.To) && !state.Ready.Contains(edge.To))
                state.Ready.Enqueue(edge.To);
        }
    }

    /// <summary>
    /// Thin wrapper over <see cref="EdgeLiveness.IsLive"/> (the exhaustively-tested rule): a
    /// successful source fires its normal/branch handles per the routing hints but NEVER the
    /// <c>error</c> handle; a failed source fires ONLY its <c>error</c> handle; a skipped source
    /// kills everything. Shared by the in-walk progression and the durable rehydrate.
    /// </summary>
    private static bool IsEdgeLive(EdgeDefinition edge, NodeStatus sourceStatus, HashSet<string>? routingHints) =>
        EdgeLiveness.IsLive(sourceStatus, edge.SourceHandle, routingHints);

    /// <summary>True when the node has at least one outgoing <c>error</c>-handle edge — i.e. its failure is handled.</summary>
    private bool HasErrorEdge(NodeDefinition node, WalkerState state) =>
        state.OutgoingByNodeId.GetValueOrDefault(node.Id, Array.Empty<EdgeDefinition>())
            .Any(e => e.SourceHandle == WorkflowHandles.Error);

    /// <summary>Parse a persisted routing-hints JSON array (e.g. <c>["true"]</c>) back to a string list; null when absent.</summary>
    private static IReadOnlyList<string>? ParseRoutingHints(string? routingHintsJson)
    {
        if (string.IsNullOrWhiteSpace(routingHintsJson)) return null;

        var root = JsonDocument.Parse(routingHintsJson).RootElement;
        if (root.ValueKind != JsonValueKind.Array) return null;

        return root.EnumerateArray()
            .Select(e => e.GetString())
            .Where(s => s != null)
            .Cast<string>()
            .ToList();
    }

    /// <summary>
    /// Persist a node's suspension: emit the immutable node.suspended record, (re)write the
    /// workflow_run_wait row, and — for a Timer wait — schedule the resume at wake_at. The walk
    /// then throws <see cref="RunSuspendedException"/> so the run lands in Suspended.
    /// </summary>
    private async Task SuspendNodeAsync(WorkflowRun run, NodeDefinition node, NodeResult result, CancellationToken cancellationToken, string iterationKey = NoIteration)
    {
        var token = result.SuspendUntil
            ?? throw new NodeFailureException($"Node '{node.Id}' returned Suspended without a SuspensionToken.");

        var waitKind = ValidateWaitKind(node.Id, token.Kind);

        // SupervisorAgentWaits is a SUSPEND MARKER, not a wait the engine stages: a spawn/retry turn's executor
        // ALREADY staged the K real AgentRun waits (keyed <nodeId>#turn{N}#{k}). We record node.suspended (the
        // run-detail surface) but stage NO extra wait + schedule NO self-advance — the agents' completion +
        // the wait-for-all barrier drive the supervisor's next turn. The K AgentRun rows are dispatched by the
        // post-Suspended-commit DispatchPendingAgentRunAsync (the SAME path agent.code uses).
        if (waitKind == WorkflowWaitKinds.SupervisorAgentWaits)
        {
            await _recordLogger.NodeSuspendedAsync(run.Id, node.Id, token.IterationKey ?? iterationKey, waitKind, wakeAt: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A self-re-entrant node (agent.supervisor) supplies a per-suspension key (<nodeId>#turn{N}) so the
        // SAME node id parks a DISTINCT (run, node, iteration) row each turn — no unique-index collision, no
        // NodeId-keyed resume-payload clobber. Otherwise use the walk-context key (NoIteration at top level,
        // <mapId>#<i> in a map branch). The key is stamped on BOTH the wait row and the node.suspended record.
        iterationKey = string.IsNullOrEmpty(token.IterationKey) ? iterationKey : token.IterationKey;
        // A Timer wait's wake_at IS its delay; any OTHER wait may carry a DeadlineAt (a bounded wait) —
        // reuse the wake_at column so the row + run-detail surface "auto-wakes at X" uniformly.
        var wakeAt = waitKind == WorkflowWaitKinds.Timer ? ReadWakeAt(token) : token.DeadlineAt;

        // Sub-workflow: stage the child run NOW (parent_run_id = this run); its id is the wait's
        // correlation token. The child is dispatched only AFTER the parent commits Suspended (see
        // RunAfterClaimAsync) so a fast child can't finish before the parent is parked. A staging
        // failure throws SubworkflowStartException, which ExecuteNodeAsync turns into a node failure.
        var correlationToken = waitKind switch
        {
            WorkflowWaitKinds.Subworkflow => (await StageSubworkflowChildAsync(run, token, cancellationToken).ConfigureAwait(false)).ToString(),
            WorkflowWaitKinds.AgentRun => (await StageAgentRunAsync(run, node, token, iterationKey, cancellationToken).ConfigureAwait(false)).ToString(),
            _ => token.CorrelationToken ?? Guid.NewGuid().ToString("N"),
        };

        await _recordLogger.NodeSuspendedAsync(run.Id, node.Id, iterationKey, waitKind, wakeAt, cancellationToken).ConfigureAwait(false);

        // One outstanding wait per (run, node, iteration). Drop any prior (resolved) wait for
        // this node+iteration so a re-suspend can't trip the unique index.
        var existing = await _db.WorkflowRunWait
            .Where(w => w.RunId == run.Id && w.NodeId == node.Id && w.IterationKey == iterationKey)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0) _db.WorkflowRunWait.RemoveRange(existing);

        var waitId = Guid.NewGuid();
        _db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = waitId,
            RunId = run.Id,
            NodeId = node.Id,
            IterationKey = iterationKey,
            WaitKind = waitKind,
            Token = correlationToken,
            WakeAt = wakeAt,
            Status = WorkflowWaitStatuses.Pending,
            // Stash the node's suspend payload (e.g. an approval prompt) so the run-detail UI can
            // render the right affordance while parked. Overwritten with the resume payload on
            // resolve — the engine only reads Resolved waits, so there's no conflict.
            PayloadJson = token.Payload.ValueKind == JsonValueKind.Undefined ? null : token.Payload.GetRawText(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Timer waits self-wake: schedule the resume at wake_at. Approval / Callback waits are
        // woken by an external signal, so nothing is scheduled there. A BOUNDED wait (DeadlineAt +
        // TimeoutPayload, e.g. an approval card with a deadline) self-wakes via ResumeByDeadlineAsync,
        // which resolves ONLY this wait with the default-on-timeout payload if it's still pending —
        // idempotent against a human resolving first.
        if (waitKind == WorkflowWaitKinds.Timer && wakeAt.HasValue)
            _backgroundJobClient.Schedule<IWorkflowResumeService>(s => s.ResumeWaitAsync(run.Id, waitId, null, CancellationToken.None), wakeAt.Value);
        else if (token.DeadlineAt.HasValue && token.TimeoutPayload is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } timeoutPayload)
            _backgroundJobClient.Schedule<IWorkflowResumeService>(s => s.ResumeByDeadlineAsync(waitId, timeoutPayload.GetRawText(), CancellationToken.None), token.DeadlineAt.Value);
    }

    internal static string ValidateWaitKind(string nodeId, string kind)
    {
        if (kind is WorkflowWaitKinds.Timer or WorkflowWaitKinds.Approval or WorkflowWaitKinds.Callback or WorkflowWaitKinds.Subworkflow or WorkflowWaitKinds.Action or WorkflowWaitKinds.AgentRun or WorkflowWaitKinds.SupervisorDecision or WorkflowWaitKinds.SupervisorAgentWaits) return kind;

        throw new NodeFailureException($"Node '{nodeId}' suspended with unknown wait kind '{kind}'. Expected Timer, Approval, Callback, Subworkflow, Action, AgentRun, SupervisorDecision, or SupervisorAgentWaits.");
    }

    /// <summary>
    /// Stage the agent run for an <c>agent.code</c> suspension: deserialize the <see cref="AgentTask"/>
    /// from the suspend payload and create the durable run (Queued, linked to this run + node) via
    /// <see cref="IAgentRunService"/>. NOT dispatched yet — <see cref="DispatchPendingAgentRunAsync"/>
    /// enqueues the executor only after the run commits Suspended, so the work can never start before
    /// the wait it resumes through exists. Returns the agent-run id, which becomes the wait's
    /// correlation token. A malformed envelope is a clean node failure.
    /// </summary>
    private async Task<Guid> StageAgentRunAsync(WorkflowRun run, NodeDefinition node, SuspensionToken token, string iterationKey, CancellationToken cancellationToken)
    {
        AgentTask task;

        try
        {
            task = JsonSerializer.Deserialize<AgentTask>(token.Payload.GetRawText(), AgentJson.Options)
                   ?? throw new NodeFailureException($"Node '{node.Id}' suspended with an empty agent-task envelope.");
        }
        catch (JsonException ex)
        {
            throw new NodeFailureException($"Node '{node.Id}' suspended with an invalid agent-task envelope: {ex.Message}");
        }

        // Resolve the persona (if any) into the task BEFORE persisting, so the run's TaskJson is the final
        // merged task — self-describing + deterministic on re-claim. A missing/foreign persona is a clean
        // node failure, like a bad envelope above.
        try
        {
            task = await _agentDefinitionResolver.ResolveAsync(task, run.TeamId, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentDefinitionResolutionException ex)
        {
            throw new NodeFailureException($"Node '{node.Id}': {ex.Message}");
        }

        // iterationKey is the engine's authoritative effective cell key for this suspension (computed in
        // SuspendNodeAsync from the branch key ?? the token's own key) — identical to the value stamped on the
        // wait row + workflow_run_node cell, so the agent run joins back to its EXACT cell (D4).
        var agentRun = await _agentRunService.CreateAsync(task, run.TeamId, run.Id, node.Id, iterationKey, cancellationToken).ConfigureAwait(false);

        return agentRun.Id;
    }

    /// <summary>
    /// Stage the child run for a <c>flow.subworkflow</c> suspension: read the target + inputs from
    /// the suspend payload and create the child via <see cref="ISubworkflowService"/> (NOT dispatched
    /// yet). Returns the child run id, which becomes the wait's correlation token.
    /// </summary>
    private async Task<Guid> StageSubworkflowChildAsync(WorkflowRun run, SuspensionToken token, CancellationToken cancellationToken)
    {
        var payload = token.Payload;

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("workflowId", out var widEl)
            || !Guid.TryParse(widEl.GetString(), out var childWorkflowId))
            throw new SubworkflowStartException("Sub-workflow node is missing a valid 'workflowId'.");

        int? version = payload.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.Number && vEl.TryGetInt32(out var v) ? v : null;
        var inputsJson = payload.TryGetProperty("inputs", out var inEl) && inEl.ValueKind == JsonValueKind.Object ? inEl.GetRawText() : "{}";

        return await _subworkflowService.StageChildRunAsync(run, childWorkflowId, version, inputsJson, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>For a Timer suspension the node puts an ISO-8601 <c>wake_at</c> in the token payload.</summary>
    private static DateTimeOffset? ReadWakeAt(SuspensionToken token)
    {
        if (token.Payload.ValueKind != JsonValueKind.Object) return null;
        if (!token.Payload.TryGetProperty("wake_at", out var w) || w.ValueKind != JsonValueKind.String) return null;

        return DateTimeOffset.TryParse(w.GetString(), out var dt) ? dt : null;
    }

    private async Task LoadResolvedWaitsAsync(Guid runId, WalkerState state, CancellationToken cancellationToken)
    {
        var resolved = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Resolved)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var w in resolved)
            state.ResumePayloads[w.NodeId] = string.IsNullOrWhiteSpace(w.PayloadJson)
                ? EmptyJsonObject()
                : JsonDocument.Parse(w.PayloadJson).RootElement.Clone();
    }

    private static JsonElement EmptyJsonObject() => JsonDocument.Parse("{}").RootElement.Clone();

    private bool ShouldSkip(NodeDefinition node, WalkerState state)
    {
        // The trigger / any root never skips on incoming-edge logic (it has no incoming).
        var manifest = _nodeRegistry.Resolve(node.TypeKey).Manifest;
        if (manifest.Kind == NodeKind.Trigger) return false;

        var incoming = state.IncomingByNodeId.GetValueOrDefault(node.Id, Array.Empty<EdgeDefinition>());
        if (incoming.Count == 0) return false;

        // Skip iff EVERY incoming edge is "dead" — either source skipped/failed, or source's
        // routing hints excluded this edge's handle. Even ONE live edge means the node runs.
        return incoming.All(edge => !state.EdgeLive.GetValueOrDefault(edge, false));
    }

    /// <summary>
    /// Run one node, honouring its optional retry-on-failure policy. The shape is a bounded
    /// attempt loop: run once → on suspend, park; on success/skip, finalise + advance; on a
    /// genuine failure (returned Fail OR thrown), retry if attempts remain (Warn-logged + a
    /// bounded in-process backoff), else write node.failed and return Failure. A null policy
    /// resolves to a single attempt — behaviour-identical to the pre-retry engine.
    ///
    /// <para>The resolved Inputs/Config + the resume payload are computed ONCE and constant
    /// across attempts; node.started is emitted ONCE before the loop (the run-node view ignores
    /// the per-retry <c>log</c> records and projects status from the node.* records only).</para>
    /// </summary>
    private async Task<NodeRunOutcome> ExecuteNodeAsync(WorkflowRun run, NodeDefinition node, NodeRunScope scope, WalkerState state, CancellationToken cancellationToken, string iterationKey = NoIteration)
    {
        var runtime = _nodeRegistry.Resolve(node.TypeKey);
        var resolvedConfig = VariableResolver.ResolveBag(node.Config, scope);
        var resolvedInputs = VariableResolver.ResolveBag(node.Inputs, scope);

        var (redactedInputs, redactedConfig) = BuildRedactedRecordPayloads(node, resolvedInputs, resolvedConfig, scope);
        var startedAt = DateTimeOffset.UtcNow;
        // Capture the node.started record id so this node's external-call records chain back
        // to it via parent_record_id. The NodeObservability handle carries the link.
        var parentRecordId = await _recordLogger.NodeStartedAsync(run.Id, node.Id, iterationKey, redactedInputs, redactedConfig, cancellationToken).ConfigureAwait(false);

        // On a resumed run the node was suspended; rehydrate loaded its resolved wait's payload
        // into state.ResumePayloads. The node knows it's being resumed via this.
        var resumePayload = state.ResumePayloads.TryGetValue(node.Id, out var rp) ? rp : (JsonElement?)null;
        var exec = new NodeExecution(run, node, runtime, scope, state, resolvedInputs, resolvedConfig, parentRecordId, resumePayload, startedAt, iterationKey);

        // D7-3 rerun side-effect gate: on a from-node rerun, a re-run side-effecting node parks for human
        // approval before it can re-fire (approve → run it; reject → skip it). Returns null for every other
        // node (normal/replay run, or a pure node) so the standard execution proceeds unchanged.
        var gateOutcome = await ApplyRerunSideEffectGateAsync(exec, cancellationToken).ConfigureAwait(false);
        if (gateOutcome is { } gated) return gated;

        var plan = RetryPlan.From(node.Retry);
        var lastError = "Node failed.";

        for (var attempt = 1; attempt <= plan.MaxAttempts; attempt++)
        {
            var attemptStartedAt = DateTimeOffset.UtcNow;
            var (result, thrownError) = await RunNodeOnceAsync(exec, cancellationToken).ConfigureAwait(false);

            // A suspend parks the run by design — never a failure, never retried. A sub-workflow
            // suspend stages its child here; if the child can't be started, that's a clean node
            // failure (composes with the error branch), not a suspend.
            if (result is { Status: NodeStatus.Suspended })
            {
                try
                {
                    await SuspendNodeAsync(run, node, result, cancellationToken, iterationKey).ConfigureAwait(false);
                    return new NodeRunOutcome(NodeStatus.Suspended, null, null, null);
                }
                catch (SubworkflowStartException ex)
                {
                    return await FinalizeFailureAsync(exec, ex.Message, cancellationToken).ConfigureAwait(false);
                }
                catch (AgentRunAdmissionException ex)
                {
                    // The fail-closed admission cap refused this agent run at staging — a clean node failure (like a
                    // failed sub-workflow start), so the agent.code node routes to its error edge and, inside a map
                    // branch, the map's continue-on-error / terminate policy applies. NOT a crash, NOT a leaked wait.
                    return await FinalizeFailureAsync(exec, ex.Message, cancellationToken).ConfigureAwait(false);
                }
            }

            // Success / Skipped — persist + compute the effects to merge into scope/state, advance.
            if (result != null && result.Status != NodeStatus.Failure)
                return await FinalizeNonFailureAsync(exec, result, cancellationToken).ConfigureAwait(false);

            // Genuine failure (returned Fail or threw). Retry while attempts remain, else finalise.
            lastError = result?.Error ?? thrownError ?? lastError;

            if (attempt == plan.MaxAttempts)
                return await FinalizeFailureAsync(exec, lastError, cancellationToken).ConfigureAwait(false);

            await LogRetryAndWaitAsync(exec, attempt, plan, lastError, attemptStartedAt, cancellationToken).ConfigureAwait(false);
        }

        // Defensive: the loop always returns on its final attempt.
        return await FinalizeFailureAsync(exec, lastError, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// D7-3 rerun side-effect gate. On a from-node RERUN, a side-effecting node must not silently re-fire:
    /// <list type="bullet">
    ///   <item>First reach (no resume payload) → park on an Approval wait (<see cref="SuspendNodeAsync"/>) and
    ///         return a Suspended outcome; <see cref="AdvanceAfterNodeSettled"/> then lands the run Suspended.</item>
    ///   <item>Resumed + approved → return null so the caller runs the node for real (fires the side effect).</item>
    ///   <item>Resumed + rejected → write node.skipped and return Skipped, so the effect never fires (downstream
    ///         with no other live input is skipped, mirroring an untaken branch).</item>
    /// </list>
    /// Returns null when no gate applies. Reused upstream cells never reach here — they're pre-seeded + settled.
    /// </summary>
    private async Task<NodeRunOutcome?> ApplyRerunSideEffectGateAsync(NodeExecution exec, CancellationToken cancellationToken)
    {
        if (!Rerun.RerunSideEffectGate.ShouldGate(exec.Run.RunRequest?.SourceType, exec.Runtime.Manifest))
            return null;

        if (exec.ResumePayload is not { } approval)
        {
            var token = Rerun.RerunSideEffectGate.BuildApprovalToken(exec.Node.Id, exec.Runtime.Manifest.DisplayName);
            await SuspendNodeAsync(exec.Run, exec.Node, NodeResult.Suspend(token), cancellationToken, exec.IterationKey).ConfigureAwait(false);
            return new NodeRunOutcome(NodeStatus.Suspended, null, null, null);
        }

        if (Rerun.RerunSideEffectGate.IsApproved(approval))
            return null;   // approved → fall through and run the node (fire the side effect)

        // Rejected → skip the node so its side effect never fires.
        await _recordLogger.NodeSkippedAsync(exec.Run.Id, exec.Node.Id, exec.IterationKey, reason: "rerun side-effect re-fire rejected by operator", cancellationToken).ConfigureAwait(false);
        return new NodeRunOutcome(NodeStatus.Skipped, null, null, null);
    }

    /// <summary>
    /// One attempt at the node's <c>RunAsync</c>. Returns the <see cref="NodeResult"/> on a clean
    /// return; on a thrown exception returns <c>(null, message)</c> so the retry loop can treat it
    /// as a failure. Cancellation and the secret-leak guard are re-thrown — they're not retryable:
    /// a cancel stops the run, and a leak is a contract violation that must surface its detailed
    /// message (the loop would otherwise mask it as a generic node failure).
    /// </summary>
    private async Task<(NodeResult? Result, string? Error)> RunNodeOnceAsync(NodeExecution exec, CancellationToken cancellationToken)
    {
        try
        {
            var context = BuildNodeRunContext(exec);
            var result = await exec.Runtime.RunAsync(context, cancellationToken).ConfigureAwait(false);
            return (result, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (WorkflowSecretLeakException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Node {NodeId} threw unhandled exception", exec.Node.Id);
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Finalise a Success / Skipped attempt: persist the node.completed (or node.failed for a
    /// node-returned Skip — unchanged from before), then RETURN the effects (scope outputs, routing
    /// hints, Terminal outputs) for the walker to merge single-threaded via <see cref="MergeNodeOutcome"/>.
    /// This method itself writes only the DB (own scope's record logger) — no shared scope/state
    /// mutation — so a node can run in its own DI scope under parallel execution without racing.
    /// The secret-leak guard fires here (Terminal capture, reading scope); its exception propagates
    /// to the run-level handler — it is NOT swallowed or retried.
    /// </summary>
    private async Task<NodeRunOutcome> FinalizeNonFailureAsync(NodeExecution exec, NodeResult result, CancellationToken cancellationToken)
    {
        var duration = DateTimeOffset.UtcNow - exec.StartedAt;
        await PersistNodeResultAsync(exec.Run.Id, exec.Node.Id, result, duration, cancellationToken, exec.IterationKey).ConfigureAwait(false);

        var scopeOutputs = result.Status == NodeStatus.Success && result.Outputs.Count > 0 ? result.Outputs : null;
        var terminalOutputs = result.Status == NodeStatus.Success && exec.Runtime.Manifest.Kind == NodeKind.Terminal && exec.ResolvedInputs.Count > 0
            ? CaptureTerminalOutputs(exec.Node, exec.ResolvedInputs, exec.Scope)
            : null;

        return new NodeRunOutcome(result.Status, scopeOutputs, result.RoutingHints, terminalOutputs);
    }

    /// <summary>
    /// Finalise a terminal failure (returned Fail or thrown, retries exhausted): write the node.failed
    /// record (own scope's record logger) and RETURN the error as the node's <c>error</c> scope output
    /// so a downstream error-branch handler can read <c>{{nodes.&lt;id&gt;.outputs.error.message}}</c>
    /// once the walker merges it. The scope write happens in <see cref="MergeNodeOutcome"/>; it's
    /// harmless when the node has no error edge — the run fails and scope is dropped.
    /// </summary>
    private async Task<NodeRunOutcome> FinalizeFailureAsync(NodeExecution exec, string error, CancellationToken cancellationToken)
    {
        await _recordLogger.NodeFailedAsync(exec.Run.Id, exec.Node.Id, exec.IterationKey, error, DateTimeOffset.UtcNow - exec.StartedAt, cancellationToken).ConfigureAwait(false);
        return new NodeRunOutcome(NodeStatus.Failure, BuildErrorOutput(error, exec.Node.Id), null, null);
    }

    /// <summary>
    /// The node's <c>error</c> output bag — <c>{ "error": { "message": ..., "node": ... } }</c>,
    /// referenced by a handler as <c>{{nodes.&lt;id&gt;.outputs.error.message}}</c> / <c>.error.node</c>.
    /// <c>node</c> (the failing node's id) lets a shared/fan-in handler tell which node failed.
    /// Shared by the in-walk failure path and the durable rehydrate so both reconstruct the same shape.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> BuildErrorOutput(string? error, string nodeId) =>
        new Dictionary<string, JsonElement> { ["error"] = JsonSerializer.SerializeToElement(new { message = error ?? "Node failed.", node = nodeId }) };

    /// <summary>
    /// Surface a failed attempt that WILL be retried: an append-only Warn <c>log</c> record naming
    /// the attempt, the (truncated) error, and the wait — so the run-detail timeline tells the
    /// retry story — then await the bounded backoff. Cancelling during the wait throws
    /// OperationCanceledException, which propagates to the run-level cancel handler.
    /// </summary>
    private async Task LogRetryAndWaitAsync(NodeExecution exec, int attempt, RetryPlan plan, string error, DateTimeOffset attemptStartedAt, CancellationToken cancellationToken)
    {
        // Durable, queryable record of THIS failed attempt — chained to the node.started row (parent_record_id),
        // carrying the attempt index, the full per-attempt error, how long it ran, and the backoff before the next
        // try. This is the structured retry history (reconstructable from the DB), keyed to the node's iteration
        // so a map-branch / loop-iteration retry is addressable. The Warn line below stays for the live timeline.
        await _recordLogger.AttemptFailedAsync(exec.Run.Id, exec.Node.Id, exec.IterationKey, attempt, plan.MaxAttempts, error, DateTimeOffset.UtcNow - attemptStartedAt, plan.BackoffSeconds, exec.ParentRecordId, cancellationToken).ConfigureAwait(false);

        var waitHint = plan.BackoffSeconds > 0 ? $" Retrying in {plan.BackoffSeconds:0.##}s." : " Retrying now.";
        var message = $"Attempt {attempt}/{plan.MaxAttempts} failed: {Truncate(error, 200)}.{waitHint}";

        await _recordLogger.LogAsync(exec.Run.Id, exec.Node.Id, Lifecycle.LogLevel.Warn, message, cancellationToken).ConfigureAwait(false);

        if (plan.Delay > TimeSpan.Zero)
            await Task.Delay(plan.Delay, cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// Build the redacted Inputs/Config pair the ledger persists. The node itself receives
    /// unredacted values via <see cref="NodeRunContext"/> (so it can use the real API key /
    /// token to call its provider); ONLY the persistence path gets the marker form. Keeps
    /// <c>workflow_run_record.payload_json</c> clean of plaintext secrets without
    /// compromising node functionality. Config persistence answers "what model / timeout /
    /// temperature was this node running with".
    /// </summary>
    private (IReadOnlyDictionary<string, JsonElement> Inputs, IReadOnlyDictionary<string, JsonElement> Config) BuildRedactedRecordPayloads(NodeDefinition node, IReadOnlyDictionary<string, JsonElement> resolvedInputs, IReadOnlyDictionary<string, JsonElement> resolvedConfig, NodeRunScope scope)
    {
        var redactedInputs = _redactor.RedactBag(node.Inputs, resolvedInputs, scope.SecretPaths);
        var redactedConfig = _redactor.RedactBag(node.Config, resolvedConfig, scope.SecretPaths);
        return (redactedInputs, redactedConfig);
    }

    /// <summary>
    /// Immutable per-node-execution context the retry loop + its finalise helpers thread around.
    /// Built once in <see cref="ExecuteNodeAsync"/>; the resolved Inputs/Config + ResumePayload are
    /// identical across retry attempts (the engine resolves them once, not per attempt). Packs the
    /// values into one argument so the helpers stay under Rule 1's 5-param cap — a data holder with
    /// no behaviour.
    /// </summary>
    private sealed record NodeExecution(WorkflowRun Run, NodeDefinition Node, INodeRuntime Runtime, NodeRunScope Scope, WalkerState State, IReadOnlyDictionary<string, JsonElement> ResolvedInputs, IReadOnlyDictionary<string, JsonElement> ResolvedConfig, Guid ParentRecordId, JsonElement? ResumePayload, DateTimeOffset StartedAt, string IterationKey);

    /// <summary>
    /// Assemble the <see cref="NodeRunContext"/> the node sees. Pulls the per-node logger
    /// name from the runtime's TypeKey and binds observability to the node.started record so
    /// external_call.* records chain back via parent_record_id.
    /// </summary>
    private NodeRunContext BuildNodeRunContext(NodeExecution exec)
    {
        var nodeLogger = _loggerFactory.CreateLogger($"Workflow.{exec.Runtime.TypeKey}.{exec.Node.Id}");
        var observability = new NodeObservability(_recordLogger, _artifactStore, exec.Run.Id, exec.Node.Id, exec.Run.TeamId, exec.ParentRecordId);

        return new NodeRunContext
        {
            Inputs = exec.ResolvedInputs,
            Config = exec.ResolvedConfig,
            RawInputs = exec.Node.Inputs,
            RawConfig = exec.Node.Config,
            Scope = exec.Scope,
            Logger = nodeLogger,
            Observability = observability,
            ResumePayload = exec.ResumePayload,
            NodeId = exec.Node.Id,
        };
    }

    /// <summary>
    /// Write the <c>node.completed</c> / <c>node.failed</c> ledger record for a node whose
    /// <c>RunAsync</c> returned (i.e. did not throw). The branching is by <see cref="NodeStatus"/>;
    /// thrown exceptions go through <see cref="ExecuteNodeAsync"/>'s catch handlers instead.
    /// </summary>
    private async Task PersistNodeResultAsync(Guid runId, string nodeId, NodeResult result, TimeSpan duration, CancellationToken cancellationToken, string iterationKey = NoIteration)
    {
        if (result.Status == NodeStatus.Success)
            await _recordLogger.NodeCompletedAsync(runId, nodeId, iterationKey, result.Outputs, result.RoutingHints, duration, cancellationToken).ConfigureAwait(false);
        else
            await _recordLogger.NodeFailedAsync(runId, nodeId, iterationKey, result.Error ?? "Node returned non-success without error message.", duration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The effects a per-node execution wants merged into shared run scope/state. Computed DURING
    /// execution (in the node's own DI scope, so two parallel nodes don't race) but APPLIED by the
    /// walker single-threaded via <see cref="MergeNodeOutcome"/>. A null field means "leave it":
    /// <list type="bullet">
    /// <item><see cref="ScopeOutputs"/> — the node's <c>nodes.&lt;id&gt;.outputs</c> entry: a successful
    /// node's outputs, or a failed node's <c>error</c> output. Null on a Skip / suspend / output-less success.</item>
    /// <item><see cref="RoutingHints"/> — which output handles steer downstream edges (branch nodes). Null = follow all.</item>
    /// <item><see cref="TerminalOutputs"/> — a successful Terminal's resolved Inputs become the run's WorkflowOutputs. Null otherwise.</item>
    /// </list>
    /// </summary>
    private readonly record struct NodeRunOutcome(NodeStatus Status, IReadOnlyDictionary<string, JsonElement>? ScopeOutputs, IReadOnlyList<string>? RoutingHints, IReadOnlyDictionary<string, JsonElement>? TerminalOutputs);

    /// <summary>
    /// Apply a node execution's returned effects into the shared run scope/state — the SINGLE place
    /// the walker mutates shared state for a per-node run. Always runs on the walker thread (even when
    /// nodes execute in parallel), so concurrent executions never race on scope.Nodes /
    /// state.RoutingHints / state.WorkflowOutputs / state.Statuses. Null effect fields are left untouched.
    /// </summary>
    private static void MergeNodeOutcome(NodeDefinition node, NodeRunOutcome outcome, NodeRunScope scope, WalkerState state)
    {
        state.Statuses[node.Id] = outcome.Status;

        if (outcome.ScopeOutputs != null)
            scope.Nodes[node.Id] = outcome.ScopeOutputs;

        if (outcome.RoutingHints != null)
            state.RoutingHints[node.Id] = new HashSet<string>(outcome.RoutingHints);

        if (outcome.TerminalOutputs != null)
            state.WorkflowOutputs = outcome.TerminalOutputs;
    }

    /// <summary>
    /// Terminal nodes' resolved <c>Inputs</c> ARE the workflow's declared outputs. The engine
    /// already resolved the Inputs map before <c>RunAsync</c> — each key was bound to
    /// <c>{{some.ref}}</c> pointing at upstream values. We capture the resolved dict as the
    /// run's <c>WorkflowOutputs</c> so external consumers see what the workflow produced.
    /// Last Terminal wins (operators with multiple terminals should use logic.if to ensure
    /// only one fires per run).
    ///
    /// <para>Secret-leak guard: secrets are designed for in-process consumption (HTTP auth
    /// headers, LLM API keys). Persisting them into <c>workflow_run.OutputsJson</c> would
    /// (a) leave plaintext in the runs table accessible to anyone with run-view permission,
    /// and (b) leak them to external callers that consume the workflow's declared output
    /// contract. <see cref="EnsureNoSecretInTerminalOutputs"/> scans the Terminal's raw
    /// Inputs template (BEFORE resolution) for any <c>{{path}}</c> / <c>$ref</c> that
    /// targets a known secret path, throws <see cref="WorkflowSecretLeakException"/>, and
    /// the surrounding catch in <see cref="ExecuteNodeAsync"/> propagates it to the run-
    /// level handler.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> CaptureTerminalOutputs(NodeDefinition node, IReadOnlyDictionary<string, JsonElement> resolvedInputs, NodeRunScope scope)
    {
        EnsureNoSecretInTerminalOutputs(node, scope);
        return resolvedInputs;
    }

    private async Task MarkSkippedAsync(WorkflowRun run, NodeDefinition node, WalkerState state, CancellationToken cancellationToken, string iterationKey = NoIteration)
    {
        state.Statuses[node.Id] = NodeStatus.Skipped;
        // The view treats a node.skipped record as the cell's terminal state — no node.started
        // is emitted for skipped nodes, so the projection's started_at column is NULL for them.
        await _recordLogger.NodeSkippedAsync(run.Id, node.Id, iterationKey, reason: "all-incoming-dead", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Empty iteration key — used for every non-flow.iterate node. Aliases the shared
    /// <see cref="WorkflowIterationKeys.TopLevel"/> so the engine, the rerun seeder, and the rerun
    /// reusability resolver all read the same sentinel (no silent drift if it ever changes).</summary>
    private const string NoIteration = WorkflowIterationKeys.TopLevel;

    private sealed class NodeFailureException : Exception
    {
        public NodeFailureException(string message) : base(message) { }
    }

    /// <summary>Thrown to abort the walk when a node parks the run. Caught in RunAfterClaimAsync, which sets Suspended.</summary>
    private sealed class RunSuspendedException : Exception
    {
        public RunSuspendedException(string nodeId) : base($"Run suspended on node '{nodeId}'.") { }
    }

    /// <summary>
    /// Per-run mutable bookkeeping for the frontier walker. Lives only for the duration of
    /// <see cref="WalkGraphAsync"/> — gets garbage-collected when the run ends. Pre-computes
    /// node + edge lookups so the inner loop is O(1) per check.
    /// </summary>
    private sealed class WalkerState
    {
        public WalkerState(WorkflowDefinition definition)
        {
            NodeById = definition.Nodes.ToDictionary(n => n.Id);
            IncomingByNodeId = definition.Nodes.ToDictionary(
                n => n.Id,
                n => (IReadOnlyList<EdgeDefinition>)definition.Edges.Where(e => e.To == n.Id).ToList());
            OutgoingByNodeId = definition.Nodes.ToDictionary(
                n => n.Id,
                n => (IReadOnlyList<EdgeDefinition>)definition.Edges.Where(e => e.From == n.Id).ToList());
        }

        public IReadOnlyDictionary<string, NodeDefinition> NodeById { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<EdgeDefinition>> IncomingByNodeId { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<EdgeDefinition>> OutgoingByNodeId { get; }

        /// <summary>FIFO queue of node ids ready to consider for execution.</summary>
        public Queue<string> Ready { get; } = new();

        /// <summary>Final status of each executed/skipped node. Presence here = "this node has been resolved".</summary>
        public Dictionary<string, NodeStatus> Statuses { get; } = new();

        /// <summary>Per branch node: which output handle names the result chose. Edges outside this set are "dead".</summary>
        public Dictionary<string, HashSet<string>> RoutingHints { get; } = new();

        /// <summary>Per edge: whether it's "live" (source succeeded AND handle is selected). Drives ShouldSkip.</summary>
        public Dictionary<EdgeDefinition, bool> EdgeLive { get; } = new();

        /// <summary>Per suspended-then-resumed node: the resolved wait's payload, injected as the node's ResumePayload on re-run.</summary>
        public Dictionary<string, JsonElement> ResumePayloads { get; } = new();

        /// <summary>
        /// Last successful Terminal node's resolved Inputs — written to workflow_run.OutputsJson
        /// at the end of the walk. Empty when no Terminal ran with any Inputs declared.
        /// </summary>
        public IReadOnlyDictionary<string, JsonElement> WorkflowOutputs { get; set; } = new Dictionary<string, JsonElement>();
    }
}

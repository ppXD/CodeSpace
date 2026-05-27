using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Hardening;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Runtime;
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

    public WorkflowEngine(CodeSpaceDbContext db, INodeRegistry nodeRegistry, IVariableService variableService, IRunRecordLogger recordLogger, IPayloadRedactor redactor, IArtifactStore artifactStore, ILogger<WorkflowEngine> logger, ILoggerFactory loggerFactory)
    {
        _db = db;
        _nodeRegistry = nodeRegistry;
        _variableService = variableService;
        _recordLogger = recordLogger;
        _redactor = redactor;
        _artifactStore = artifactStore;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

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
        await _recordLogger.ReleaseLoadedAsync(run.Id, run.WorkflowVersion, releaseHash, definition.Nodes.Count, definition.Edges.Count, cancellationToken).ConfigureAwait(false);

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
        var version = await _db.WorkflowVersion
            .SingleAsync(v => v.WorkflowId == run.WorkflowId && v.Version == run.WorkflowVersion, cancellationToken)
            .ConfigureAwait(false);

        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(version.DefinitionJson, WorkflowJson.Options)
            ?? throw new InvalidOperationException($"WorkflowVersion ({run.WorkflowId},{run.WorkflowVersion}) has empty JSON.");

        var recomputed = DefinitionHash.Compute(definition);
        if (!string.Equals(recomputed, version.DefinitionHash, StringComparison.Ordinal))
            throw new ReleaseTamperedException(run.WorkflowId, run.WorkflowVersion, version.DefinitionHash, recomputed);

        return (definition, version.DefinitionHash);
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

        // Rule-11 enforcement: surface required inputs that the caller didn't supply AND
        // that have no Default in the definition. Default mode (Warn) logs + continues so
        // existing deploys aren't broken; Strict throws and lands the run in Failure;
        // Off silently allows. Only runs on FRESH runs — replays use the snapshotted scope
        // from first-run, so a mode flip between runs cannot retro-actively fail a replay.
        EnsureRequiredInputsSatisfied(run, definition, input);

        var sys = BuildSysScope(run);

        // Fetch typed sets ONCE so we can both build scope AND persist snapshot rows
        // without a second round-trip per scope.
        var wfResolved = await _variableService.GetAllForEngineAsync(VariableScope.Workflow, run.WorkflowId, cancellationToken).ConfigureAwait(false);
        var teamResolved = await _variableService.GetAllForEngineAsync(VariableScope.Team, run.Workflow.TeamId, cancellationToken).ConfigureAwait(false);

        var wf = BagFromResolved(wfResolved);
        var team = BagFromResolved(teamResolved);

        // Project scope — load only the projects this definition references (via slug),
        // not every project in the team. Falls back to an empty bag when the definition
        // contains no project.* refs (cheap).
        var (projects, projectSecretPaths) = await LoadReferencedProjectVariablesAsync(run.Workflow.TeamId, run.WorkflowId, definition, cancellationToken).ConfigureAwait(false);

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

        var wfSecretNames = await MergeCurrentSecretsAsync(wf, VariableScope.Workflow, run.WorkflowId, cancellationToken).ConfigureAwait(false);
        var teamSecretNames = await MergeCurrentSecretsAsync(team, VariableScope.Team, run.Workflow.TeamId, cancellationToken).ConfigureAwait(false);

        // Project scope — load fresh (no snapshot path for projects).
        var (projects, projectSecretPaths) = await LoadReferencedProjectVariablesAsync(run.Workflow.TeamId, run.WorkflowId, definition, cancellationToken).ConfigureAwait(false);

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
    /// flow through <see cref="MissingProjectRefValidator"/> which applies three-mode
    /// enforcement (off / warn / strict) — see that class for rationale. Default is
    /// <c>warn</c>: a structured log line names every missing slug and the env var to
    /// flip to <c>strict</c>; the bag for those slugs stays unfilled and the resolver
    /// returns null (preserves legacy behavior). Operators flip to <c>strict</c> for
    /// production hardening once stale refs are cleaned up.</para>
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

        // Apply Rule-11 three-mode enforcement on the diff between referenced and found
        // slugs. Strict mode throws here (caught by the engine's main loop, which lands
        // the run in Failed with the exception's message in WorkflowRun.Error); warn
        // logs and continues; off is silent.
        var foundSlugs = projects.Select(p => p.Slug).ToHashSet(StringComparer.Ordinal);
        var refCtx = new MissingProjectRefContext(referencedSlugs, foundSlugs, teamId, workflowId);
        var refMode = EnforcementModeReader.Read(MissingProjectRefValidator.EnforcementEnvVar);
        MissingProjectRefValidator.EnsureKnown(refCtx, refMode, _logger);

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

        return new Dictionary<string, JsonElement>
        {
            [SystemScopeKeys.WorkflowId]      = ToJson(run.WorkflowId),
            [SystemScopeKeys.WorkflowRunId]   = ToJson(run.Id),
            [SystemScopeKeys.WorkflowVersion] = ToJson(run.WorkflowVersion),
            [SystemScopeKeys.SourceType]      = ToJson(sourceType),
            [SystemScopeKeys.StartedAt]       = ToJson(startedAt),
            [SystemScopeKeys.TeamId]          = ToJson(run.Workflow.TeamId),
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
    /// now we trust the frontend SchemaForm + the Inputs declaration. A future Rule-11
    /// hardening could add a strict-mode env var that rejects inputs that don't match Schema;
    /// today the engine doesn't validate JSON-Schema conformance at runtime (the NodeRunContext
    /// doc-comment is explicit about this).</para>
    /// </summary>
    /// <summary>
    /// Apply CLAUDE.md Rule 11 three-mode enforcement on required inputs that <see cref="BuildInputScope"/>
    /// failed to populate. The validator's modes (Off / Warn / Strict) are parsed from
    /// <see cref="MissingRequiredInputValidator.EnforcementEnvVar"/>; defaults to Warn so
    /// existing workflows continue to run and only emit log warnings.
    /// </summary>
    private void EnsureRequiredInputsSatisfied(WorkflowRun run, WorkflowDefinition definition, IReadOnlyDictionary<string, JsonElement> resolvedInputs)
    {
        if (definition.Inputs.Count == 0) return;

        var requiredNames = new List<string>();
        foreach (var declared in definition.Inputs)
            if (declared.Required) requiredNames.Add(declared.Name);

        if (requiredNames.Count == 0) return;

        var ctx = new MissingRequiredInputContext(requiredNames, resolvedInputs.Keys.ToList(), run.Workflow.TeamId, run.WorkflowId);
        var mode = EnforcementModeReader.Read(MissingRequiredInputValidator.EnforcementEnvVar);
        MissingRequiredInputValidator.EnsureSatisfied(ctx, mode, _logger);
    }

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
    }

    // Returns the workflow's outputs (filled by the last successful Terminal). Empty when
    // no Terminal ran or the Terminal had no Inputs declared.
    private async Task<IReadOnlyDictionary<string, JsonElement>> WalkGraphAsync(WorkflowRun run, WorkflowDefinition definition, NodeRunScope scope, CancellationToken cancellationToken)
    {
        var state = new WalkerState(definition);
        EnqueueRoots(state, definition);

        while (state.Ready.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nodeId = state.Ready.Dequeue();
            if (state.Statuses.ContainsKey(nodeId)) continue;          // already fired

            var node = state.NodeById[nodeId];

            if (ShouldSkip(node, state))
            {
                await MarkSkippedAsync(run, node, state, cancellationToken).ConfigureAwait(false);
                EnqueueDownstreamWhenReady(node, state);
                continue;
            }

            var status = await ExecuteNodeAsync(run, node, scope, state, cancellationToken).ConfigureAwait(false);
            state.Statuses[nodeId] = status;

            if (status == NodeStatus.Failure)
                throw new NodeFailureException($"Node '{nodeId}' failed.");

            EnqueueDownstreamWhenReady(node, state);

            // Reaching a Terminal doesn't short-circuit the walker — we keep draining the
            // queue so that any remaining ready/skip-pending nodes get persisted as Skipped.
            // The run-detail UI needs those rows to show "this branch didn't run because of
            // routing". The outer try/catch sets the run to Success when the queue empties.
        }

        return state.WorkflowOutputs;
    }

    private static void EnqueueRoots(WalkerState state, WorkflowDefinition definition)
    {
        // Root = nodes with no incoming edges. In a valid definition this is the (single)
        // Trigger node. Defensive: if multiple roots exist (only possible for hand-written
        // JSON that slipped past validation), enqueue them all — they're independent.
        foreach (var node in definition.Nodes)
        {
            var hasIncoming = definition.Edges.Any(e => e.To == node.Id);
            if (!hasIncoming) state.Ready.Enqueue(node.Id);
        }
    }

    private void EnqueueDownstreamWhenReady(NodeDefinition source, WalkerState state)
    {
        var routingHints = state.RoutingHints.GetValueOrDefault(source.Id);
        var sourceStatus = state.Statuses[source.Id];

        // Walk every outgoing edge. Decide whether THIS edge is "live":
        //   - Source node must have succeeded (skipped/failed sources kill downstream
        //     transitively via the same logic in ShouldSkip).
        //   - For a branch source (RoutingHints set), the edge's SourceHandle must be in
        //     the hints list. Otherwise this particular edge is "dead".
        foreach (var edge in state.OutgoingByNodeId.GetValueOrDefault(source.Id, Array.Empty<EdgeDefinition>()))
        {
            var edgeIsLive = sourceStatus == NodeStatus.Success
                             && (routingHints == null || routingHints.Contains(edge.SourceHandle ?? DefaultHandleName));

            state.EdgeLive[edge] = edgeIsLive;

            // A target becomes "ready to consider" once EVERY incoming edge's source has fired
            // (succeeded, skipped, or failed). The ShouldSkip check decides whether it actually
            // executes or just gets marked Skipped.
            var allIncomingSettled = state.IncomingByNodeId.GetValueOrDefault(edge.To, Array.Empty<EdgeDefinition>())
                .All(e => state.Statuses.ContainsKey(e.From));

            if (allIncomingSettled && !state.Statuses.ContainsKey(edge.To) && !state.Ready.Contains(edge.To))
                state.Ready.Enqueue(edge.To);
        }
    }

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

    private async Task<NodeStatus> ExecuteNodeAsync(WorkflowRun run, NodeDefinition node, NodeRunScope scope, WalkerState state, CancellationToken cancellationToken)
    {
        var runtime = _nodeRegistry.Resolve(node.TypeKey);
        var resolvedConfig = VariableResolver.ResolveBag(node.Config, scope);
        var resolvedInputs = VariableResolver.ResolveBag(node.Inputs, scope);

        var (redactedInputs, redactedConfig) = BuildRedactedRecordPayloads(node, resolvedInputs, resolvedConfig, scope);
        var startedAt = DateTimeOffset.UtcNow;
        // Capture the node.started record id so this node's external-call records chain back
        // to it via parent_record_id. The NodeObservability handle carries the link.
        var parentRecordId = await _recordLogger.NodeStartedAsync(run.Id, node.Id, NoIteration, redactedInputs, redactedConfig, cancellationToken).ConfigureAwait(false);

        try
        {
            var invocation = new NodeInvocationData(run, node, scope, resolvedInputs, resolvedConfig, runtime.TypeKey, parentRecordId);
            var context = BuildNodeRunContext(invocation);
            var result = await runtime.RunAsync(context, cancellationToken).ConfigureAwait(false);
            var duration = DateTimeOffset.UtcNow - startedAt;

            await PersistNodeResultAsync(run.Id, node.Id, result, duration, cancellationToken).ConfigureAwait(false);
            ApplyResultToScopeAndState(node, result, scope, state);

            if (result.Status == NodeStatus.Success && runtime.Manifest.Kind == NodeKind.Terminal && resolvedInputs.Count > 0)
                CaptureTerminalOutputs(node, resolvedInputs, scope, state);

            return result.Status;
        }
        catch (OperationCanceledException) { throw; }
        catch (WorkflowSecretLeakException)
        {
            // Let the secret-leak guard's exception propagate to ExecuteRunAsync's catch,
            // which lands the run in Failure with our detailed contract-violation message.
            // Without this branch, the generic catch below would swallow it into NodeStatus.Failure
            // + a generic "Node 'X' failed." run.Error, losing the path-name + remediation hint.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Node {NodeId} threw unhandled exception", node.Id);
            await _recordLogger.NodeFailedAsync(run.Id, node.Id, NoIteration, ex.Message, DateTimeOffset.UtcNow - startedAt, cancellationToken).ConfigureAwait(false);
            return NodeStatus.Failure;
        }
    }

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
    /// Per-invocation data the engine threads from <see cref="ExecuteNodeAsync"/> into
    /// <see cref="BuildNodeRunContext"/>. Packs the seven values the context needs into one
    /// argument so the helper stays under Rule 1's 5-param cap. Private + record-shaped: zero
    /// allocations beyond the boxing the context itself does, no behaviour, just a parameter
    /// holder.
    /// </summary>
    private sealed record NodeInvocationData(WorkflowRun Run, NodeDefinition Node, NodeRunScope Scope, IReadOnlyDictionary<string, JsonElement> ResolvedInputs, IReadOnlyDictionary<string, JsonElement> ResolvedConfig, string TypeKey, Guid ParentRecordId);

    /// <summary>
    /// Assemble the <see cref="NodeRunContext"/> the node sees. Pulls the per-node logger
    /// name from <see cref="NodeInvocationData.TypeKey"/> and binds observability to the
    /// node.started record so external_call.* records chain back via parent_record_id.
    /// </summary>
    private NodeRunContext BuildNodeRunContext(NodeInvocationData inv)
    {
        var nodeLogger = _loggerFactory.CreateLogger($"Workflow.{inv.TypeKey}.{inv.Node.Id}");
        var observability = new NodeObservability(_recordLogger, _artifactStore, inv.Run.Id, inv.Node.Id, inv.Run.TeamId, inv.ParentRecordId);

        return new NodeRunContext
        {
            Inputs = inv.ResolvedInputs,
            Config = inv.ResolvedConfig,
            RawInputs = inv.Node.Inputs,
            RawConfig = inv.Node.Config,
            Scope = inv.Scope,
            Logger = nodeLogger,
            Observability = observability,
        };
    }

    /// <summary>
    /// Write the <c>node.completed</c> / <c>node.failed</c> ledger record for a node whose
    /// <c>RunAsync</c> returned (i.e. did not throw). The branching is by <see cref="NodeStatus"/>;
    /// thrown exceptions go through <see cref="ExecuteNodeAsync"/>'s catch handlers instead.
    /// </summary>
    private async Task PersistNodeResultAsync(Guid runId, string nodeId, NodeResult result, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (result.Status == NodeStatus.Success)
            await _recordLogger.NodeCompletedAsync(runId, nodeId, NoIteration, result.Outputs, duration, cancellationToken).ConfigureAwait(false);
        else
            await _recordLogger.NodeFailedAsync(runId, nodeId, NoIteration, result.Error ?? "Node returned non-success without error message.", duration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reflect a successful node's outputs + routing hints into the run-scoped scope/state
    /// objects so downstream nodes can resolve <c>nodes.&lt;id&gt;.outputs.X</c> and the
    /// walker's branch-routing pass sees the correct hint set.
    /// </summary>
    private static void ApplyResultToScopeAndState(NodeDefinition node, NodeResult result, NodeRunScope scope, WalkerState state)
    {
        if (result.Status == NodeStatus.Success && result.Outputs.Count > 0)
            scope.Nodes[node.Id] = result.Outputs;

        if (result.RoutingHints != null)
            state.RoutingHints[node.Id] = new HashSet<string>(result.RoutingHints);
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
    private static void CaptureTerminalOutputs(NodeDefinition node, IReadOnlyDictionary<string, JsonElement> resolvedInputs, NodeRunScope scope, WalkerState state)
    {
        EnsureNoSecretInTerminalOutputs(node, scope);
        state.WorkflowOutputs = resolvedInputs;
    }

    private async Task MarkSkippedAsync(WorkflowRun run, NodeDefinition node, WalkerState state, CancellationToken cancellationToken)
    {
        state.Statuses[node.Id] = NodeStatus.Skipped;
        // The view treats a node.skipped record as the cell's terminal state — no node.started
        // is emitted for skipped nodes, so the projection's started_at column is NULL for them.
        await _recordLogger.NodeSkippedAsync(run.Id, node.Id, NoIteration, reason: "all-incoming-dead", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Empty iteration key — used for every non-flow.iterate node.</summary>
    private const string NoIteration = "";

    private const string DefaultHandleName = "out";

    private sealed class NodeFailureException : Exception
    {
        public NodeFailureException(string message) : base(message) { }
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

        /// <summary>
        /// Last successful Terminal node's resolved Inputs — written to workflow_run.OutputsJson
        /// at the end of the walk. Empty when no Terminal ran with any Inputs declared.
        /// </summary>
        public IReadOnlyDictionary<string, JsonElement> WorkflowOutputs { get; set; } = new Dictionary<string, JsonElement>();
    }
}

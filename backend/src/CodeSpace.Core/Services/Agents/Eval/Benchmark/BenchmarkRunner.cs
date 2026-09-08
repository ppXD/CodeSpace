using Microsoft.Extensions.Logging;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Review;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Drives one (task, mode) through the REAL agent-execution pipeline, grades it with the task's objective oracle,
/// and records a <see cref="BenchmarkResult"/>. Thin orchestration over the same production seams the agent.run
/// node uses (Rule 16): <c>IAgentRunService.CreateAsync</c> → <c>IAgentRunExecutor.ExecuteAsync</c> →
/// <c>IBenchmarkGrader.GradeAsync</c>. The runner adds NO new execution path — it reuses the executor, the runner
/// registry, and the grader registry; the model behind the CLI is the environment's (a fake CLI in CI).
///
/// <para><b>Mode handling.</b> <see cref="BenchmarkMode.HarnessCli"/> and <see cref="BenchmarkMode.HarnessCliWithMcp"/>
/// are GENUINELY differentiated within a SINGLE process: the runner stamps the per-run opt-in
/// <c>AgentTask.EnableMcpEndpoint</c> from the mode, so the cli-mcp run opens the run-scoped MCP tool-fabric endpoint
/// while the cli run does not — they execute observably differently, not merely under different scorecard labels. The
/// resolved gate state (the SAME <c>AgentRunExecutor.UsesFullToolCatalog</c> the executor consults) is recorded on
/// <see cref="BenchmarkResult.McpFullCatalog"/>, so a row can never be mislabeled relative to what the executor did.
/// <see cref="BenchmarkMode.WorkflowMap"/> is RESERVED, not wired in this slice: it would run through the composed
/// planner→<c>flow.map</c>→synthesizer ENGINE path (a workflow, not a single agent run), which this single-run runner
/// does not orchestrate — requesting it throws a clear "not yet wired" so the boundary is explicit, never a silent fake.
/// The seed corpus therefore ships only the two runnable modes (see <c>SeedBenchmarkCorpus.DefaultModes</c>).</para>
/// </summary>
public sealed class BenchmarkRunner : IBenchmarkRunner, IScopedDependency
{
    private readonly IAgentRunService _runs;
    private readonly IAgentRunExecutor _executor;
    private readonly Sandbox.ISandboxRunnerRegistry _runners;
    private readonly IBenchmarkGraderRegistry _graders;
    private readonly IBenchmarkFixtureStager _stager;
    private readonly ITaskLaunchBenchmarkCellRunner _taskLaunchCells;

    private readonly Workflows.Artifacts.IArtifactStore _artifacts;
    private readonly Microsoft.Extensions.Logging.ILogger<BenchmarkRunner> _logger;

    public BenchmarkRunner(IAgentRunService runs, IAgentRunExecutor executor, Sandbox.ISandboxRunnerRegistry runners, IBenchmarkGraderRegistry graders, IBenchmarkFixtureStager stager, ITaskLaunchBenchmarkCellRunner taskLaunchCells, Workflows.Artifacts.IArtifactStore artifacts, Microsoft.Extensions.Logging.ILogger<BenchmarkRunner> logger)
    {
        _runs = runs;
        _executor = executor;
        _runners = runners;
        _graders = graders;
        _stager = stager;
        _taskLaunchCells = taskLaunchCells;
        _artifacts = artifacts;
        _logger = logger;
    }

    public async Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, BenchmarkExecutionContext context, CancellationToken cancellationToken)
    {
        var workspaceDirectory = context.WorkspaceDirectory;
        var teamId = context.TeamId;
        var selection = context.Selection;
        if (mode == BenchmarkMode.WorkflowMap)
            throw new NotSupportedException("BenchmarkMode.WorkflowMap is reserved and not yet wired: it runs through the composed planner→flow.map→synthesizer ENGINE path (a workflow, not a single agent run), which this single-run runner does not orchestrate. Run the two harness-CLI modes here; the seed corpus ships only those.");

        // P19: a TaskLaunch arm exercises the REAL product Launch entry (route → projection → run) instead of a
        // directly-created AgentRun — a sibling instrument owns that whole seam; this runner never builds an
        // AgentTask for one, so it can never accidentally take the direct (Shadow-only) path for Launch evidence.
        if (BenchmarkModeEffort.IsTaskLaunch(mode))
            return await _taskLaunchCells.RunAsync(task, mode, context, cancellationToken).ConfigureAwait(false);

        var agentTask = BuildAgentTask(task, mode, workspaceDirectory, selection);

        // The SAME gate the executor will consult to decide whether to open the run's MCP endpoint — recorded on the
        // result so the cli vs cli-mcp rows can never be mislabeled relative to what the run actually did.
        var mcpFullCatalog = AgentRunExecutor.UsesFullToolCatalog(agentTask);

        var attempts = await RunWithFormatFaultRespawnAsync(task, agentTask, context, cancellationToken).ConfigureAwait(false);

        var grade = await BenchmarkTaskGrading.GradeAsync(_graders, _runners, new BenchmarkTaskGradingRequest { Task = task, WorkspaceDirectory = workspaceDirectory, TeamId = teamId, ProducerModel = ProducerModelOf(selection, attempts) }, cancellationToken).ConfigureAwait(false);

        grade = ApplyMcpFabricRule(grade, mode, attempts[^1]);

        grade = await CaptureEvidenceAsync(grade, teamId, cancellationToken).ConfigureAwait(false);

        return BuildResult(task, mode, attempts, grade, mcpFullCatalog);
    }

    internal static ReviewModelIdentity ProducerModelOf(BenchmarkAgentSelection? selection, IReadOnlyList<AgentRun> attempts)
    {
        var gradedModel = attempts[^1].ResultJson is { } json ? JsonSerializer.Deserialize<AgentRunResult>(json, AgentJson.Options)?.Model : null;
        return new ReviewModelIdentity { ModelCredentialModelId = selection?.ModelCredentialModelId, ConfiguredModel = selection?.Model, ObservedModel = ObservedModelOf(attempts, gradedModel) };
    }

    /// <summary>
    /// Drive the cell's agent run, buying the gateway-format-fault repair EXACTLY ONCE (see <see cref="RespawnFor"/>),
    /// and return EVERY attempt in dispatch order (the LAST one is the graded attempt). The benchmark lane builds its
    /// own <c>AgentTask</c>s and drives the executor directly, so it inherits neither the quick lane's node-retry budget
    /// nor the supervisor's retry verdict — without this, every cell the gateway mangled died where it stood and the
    /// whole instrument went blind while the gateway misbehaved (9/18 cells infra-dead for 7 consecutive main runs,
    /// 2026-09). The grade is taken AFTER this returns, so it judges the attempt that actually got a turn.
    ///
    /// <para>The respawn RE-STAGES the fixture first (see <see cref="RestageWorkspace"/>) — the grade is taken over the
    /// workspace, so without it the oracle would judge the UNION of both attempts.</para>
    /// </summary>
    private async Task<IReadOnlyList<AgentRun>> RunWithFormatFaultRespawnAsync(BenchmarkTask task, AgentTask agentTask, BenchmarkExecutionContext context, CancellationToken cancellationToken)
    {
        var teamId = context.TeamId;
        var completed = await ExecuteOnceAsync(agentTask, teamId, cancellationToken).ConfigureAwait(false);

        if (RespawnFor(agentTask, completed.Error) is not { } mitigated) return new[] { completed };

        _logger.LogWarning("Benchmark cell agent run {RunId} died of {Cause} — re-staging fixture {FixtureRef} and respawning ONCE on a fresh conversation with extended thinking disabled; a second fault leaves the cell infra-dead", completed.Id, Supervisor.AgentRetryCauses.GatewayFormatFault, task.FixtureRef);

        RestageWorkspace(task, context);

        return new[] { completed, await ExecuteOnceAsync(mitigated, teamId, cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>
    /// Reset the cell's workspace to the fixture's FAILING start-state before the respawn — the same
    /// <see cref="IBenchmarkFixtureStager"/> seam the corpus loop stages each cell through, over the same directory.
    ///
    /// <para>The faulted attempt is documented as one where "the model never got a turn", but nothing ENFORCED that:
    /// the respawn re-executed into the SAME directory and <see cref="BenchmarkTaskGrading.GradeAsync"/> runs the
    /// oracle over the workspace AFTERWARDS, so anything the first attempt wrote before the gateway killed it — a
    /// partial edit, a forged check — was graded as the respawn's work. Wiping and re-staging makes the respawn's
    /// grade about the respawn alone.</para>
    ///
    /// <para>FAIL-CLOSED: a stager throw propagates, so the cell is recorded as an infra error rather than graded over a
    /// tree we cannot vouch for — a polluted verdict is worse than a lost cell.</para>
    /// </summary>
    private void RestageWorkspace(BenchmarkTask task, BenchmarkExecutionContext context)
    {
        var workspaceDirectory = context.WorkspaceDirectory;
        // GetX, not EnumerateX: the lazy walk holds the directory open while we delete out from under it, which is
        // free to skip entries — and a leftover the wipe skipped is exactly what this method exists to remove.
        foreach (var directory in Directory.GetDirectories(workspaceDirectory)) Directory.Delete(directory, recursive: true);

        foreach (var file in Directory.GetFiles(workspaceDirectory)) File.Delete(file);

        (context.FixtureStager ?? _stager).Stage(task.FixtureRef, workspaceDirectory);
    }

    /// <summary>Create + drive ONE agent run to its terminal row — the single production seam both the cell's first attempt and its one mitigated respawn go through, so a respawn is a real second run with its own event log, never a re-labelled first.</summary>
    private async Task<AgentRun> ExecuteOnceAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _runs.CreateAsync(task, teamId, null, null, iterationKey: "", cancellationToken).ConfigureAwait(false);

        await _executor.ExecuteAsync(run.Id, cancellationToken).ConfigureAwait(false);

        return await _runs.GetAsync(run.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The cell's ONE respawn verdict: the mitigated task to re-stage, or null to leave the cell exactly as it stands
    /// today. A gateway format fault (<see cref="Supervisor.AgentRetryCauses.GatewayFormatFault"/>) is INFRA — the
    /// gateway mangled the Anthropic wire and the model never got a turn — so this lane buys the same repair the quick
    /// lane and the supervisor lane already buy, composed through the ONE shared
    /// <see cref="Supervisor.AgentRetryCauses.ApplyFormatFaultMitigation"/> (fresh conversation + extended thinking
    /// disabled) rather than a third copy of it.
    ///
    /// <para>The bound is read off the DISPATCHED envelope itself (<see cref="Supervisor.AgentRetryCauses.IsFormatFaultMitigated"/>
    /// — the same fact the executor announces the repair from), so an attempt that ALREADY ran mitigated and hit the
    /// same fault has proven the repair does not hold here: it returns null and the cell stays infra-dead, honestly,
    /// instead of re-billing a broken gateway. Every other cause returns null too — the benchmark is @1 by design, and
    /// this is not a general retry.</para>
    /// </summary>
    internal static AgentTask? RespawnFor(AgentTask task, string? error) =>
        Supervisor.AgentRetryCauses.Classify(error) == Supervisor.AgentRetryCauses.GatewayFormatFault && !Supervisor.AgentRetryCauses.IsFormatFaultMitigated(task)
            ? Supervisor.AgentRetryCauses.ApplyFormatFaultMitigation(task)
            : null;

    /// <summary>
    /// Build the agent-task envelope for this (task, mode): the pre-staged workspace is pinned directly (no RepositoryId →
    /// the executor uses WorkspaceDirectory as the sandbox cwd), and the per-run MCP opt-in is set IFF the mode is
    /// <see cref="BenchmarkMode.HarnessCliWithMcp"/> — the one knob that genuinely differentiates the two harness-CLI
    /// modes within a single process. The optional <paramref name="selection"/> chooses WHICH agent attempts it: a null
    /// selection (the deterministic CI default) keeps the task's own harness, no model, no credential, and Standard
    /// autonomy — exactly the pre-selection behaviour; a real selection overrides the harness/model/credential and
    /// raises autonomy so a LIVE agent authenticates to the gateway and may edit the workspace to solve the task.
    /// <see cref="AgentTask.Permissions"/> is DERIVED from the resolved autonomy via <see cref="AgentAutonomyPolicy.Derive"/>
    /// — the same way every other live-agent path (<c>AgentCodeNode</c>, supervisor spawn) does — because the executor +
    /// harness read <c>Permissions.Network</c> verbatim, NOT the autonomy tier: without this, a Trusted selection would
    /// leave Network=Off and a confined live agent could never reach the gateway. The null path stays byte-identical —
    /// <c>Derive(Standard)</c> equals the default <c>AgentPermissions</c> (Network=Off, WriteScope=Workspace). Internal
    /// (not private) so the override + fallback + permission derivation is unit-pinned directly (InternalsVisibleTo).
    ///
    /// <para>The resolved tier is CLAMPED to this deployment's ceiling (<c>Sandbox:MaxAutonomy</c>) like every other
    /// path that stages a live agent: a qualification round is a caller-supplied tier posted straight at a command,
    /// with no route and no node between it and real agents spending real budget on this host — exactly the traffic
    /// an operator who lowered the ceiling meant to bound. Inert at the committed default.</para>
    /// </summary>
    internal static AgentTask BuildAgentTask(BenchmarkTask task, BenchmarkMode mode, string workspaceDirectory, BenchmarkAgentSelection? selection)
    {
        var autonomy = AgentAutonomyPolicy.Clamp(selection?.Autonomy ?? AgentAutonomyLevel.Standard, AgentAutonomyPolicy.DeploymentCeiling);

        return new AgentTask
        {
            Goal = task.Goal,
            Harness = selection?.Harness ?? task.Harness,
            // EXPLICIT, never the deployment default: a corpus number is only comparable across runs and
            // deployments if every benchmark executes on the same backend.
            RunnerKind = Sandbox.SandboxKinds.Local,
            WorkspaceDirectory = workspaceDirectory,
            Model = selection?.Model,
            ModelCredentialId = selection?.ModelCredentialId,
            ModelCredentialModelId = selection?.ModelCredentialModelId,
            Autonomy = autonomy,
            Permissions = AgentAutonomyPolicy.Derive(autonomy),
            TimeoutSeconds = task.TimeoutSeconds,
            // Both arms are EXPLICIT. The cli-only arm used to leave this null and rely on the ambient flag being
            // off; the default is now on, so null would silently give both arms the fabric and flatten the A/B.
            EnableMcpEndpoint = mode == BenchmarkMode.HarnessCliWithMcp,
            OutputReviewMode = selection?.OutputReviewMode ?? ReviewMode.None,
            ReviewerModelId = selection?.ReviewerModelId,
            MaxReviseRounds = selection?.MaxReviseRounds,
        };
    }

    /// <summary>
    /// P4-U5 (L6 — the benchmark joins the spine's EVIDENCE LAW): the oracle's bounded output goes to CAS and
    /// the grade carries the id, exactly like the acceptance funnel — a benchmark verdict is auditable bytes,
    /// never just a boolean. A store fault degrades loudly to evidence-less (the verdict stands; the transient
    /// text is dropped either way, mirroring the acceptance funnel's posture).
    /// </summary>
    private async Task<BenchmarkGrade> CaptureEvidenceAsync(BenchmarkGrade grade, Guid teamId, CancellationToken cancellationToken)
    {
        if (grade.EvidenceText is not { Length: > 0 } text) return grade;

        try
        {
            var id = await _artifacts.PutAsync(teamId, System.Text.Encoding.UTF8.GetBytes(text), "text/plain", cancellationToken).ConfigureAwait(false);
            return grade with { EvidenceText = null, EvidenceArtifactId = id };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Benchmark evidence store failed; the verdict stands evidence-less");
            return grade with { EvidenceText = null };
        }
    }

    /// <summary>
    /// P0-B2: an MCP-REQUIRED arm whose fabric never HANDSHOOK is an infrastructure fault, never a model verdict —
    /// the run was offered a catalog no client ever connected to, so the cell measured the cli arm under the
    /// cli-mcp label. Overrides even a passing oracle (mislabeled A/B data is worse than a lost cell): the grade
    /// becomes Environment-classed, which the corpus counts as InfraUnknown, outside the solve denominator.
    /// Pre-slice results (no evidence recorded) are untouched — absence of observation is never an observation.
    /// </summary>
    internal static BenchmarkGrade ApplyMcpFabricRule(BenchmarkGrade grade, BenchmarkMode mode, Persistence.Entities.AgentRun run)
    {
        if (mode != BenchmarkMode.HarnessCliWithMcp || run.ResultJson is not { } json) return grade;

        McpFabricEvidence? evidence;
        try { evidence = JsonSerializer.Deserialize<AgentRunResult>(json, AgentJson.Options)?.McpEvidence; }
        catch (JsonException) { return grade; }

        if (evidence is not { HandshakeObserved: false }) return grade;

        return grade with
        {
            Passed = false,
            Class = GradeFailureClass.Environment,
            Detail = $"mcp-required-no-handshake: the cli-mcp arm's fabric never served an initialize (endpointBound={evidence.EndpointBound}, declarationWritten={evidence.DeclarationWritten}, proxyResolved={evidence.ProxyResolved}) — infra, not a model verdict",
        };
    }

    /// <summary>
    /// Fold the cell's attempts + the grade into a result row. <paramref name="attempts"/> is every dispatched attempt
    /// in order — normally one, two when the gateway-format-fault repair was bought — and the LAST is the GRADED one:
    /// the verdict fields (status, run id, exit reason, revise rounds) are read off it, because the grade judges the
    /// tree that attempt left behind.
    ///
    /// <para>COST fields are summed across ALL attempts, never read off the graded one alone: a respawned cell BILLED
    /// both dispatches, and a cost/latency number that reported only the survivor would under-report exactly the cells
    /// the gateway made expensive — the opposite of what the respawn count is reported for. Duration is the sum of each
    /// attempt's wall-clock (null only when NO attempt carries both timestamps); token usage folds through the SAME
    /// <see cref="AgentRunExecutor.SumTokenUsage"/> the executor bills its own revise rounds with, so a null-reporting
    /// attempt (the deterministic fake CLI) never zeroes a real one.</para>
    ///
    /// <para><paramref name="mcpFullCatalog"/> is the executor's resolved catalog width for this run — the observable
    /// cli vs cli-mcp distinction (the endpoint itself opens in both).</para>
    /// </summary>
    internal static BenchmarkResult BuildResult(BenchmarkTask task, BenchmarkMode mode, IReadOnlyList<AgentRun> attempts, BenchmarkGrade grade, bool mcpFullCatalog)
    {
        var graded = attempts[^1];
        var result = graded.ResultJson is { } json ? JsonSerializer.Deserialize<AgentRunResult>(json, AgentJson.Options) : null;

        return new BenchmarkResult
        {
            TaskId = task.Id,
            Mode = mode,
            AgentRunId = graded.Id,
            RunStatus = graded.Status,
            DurationSeconds = SumDuration(attempts),
            Grade = grade,
            McpFullCatalog = mcpFullCatalog,
            FormatFaultRespawns = attempts.Count - 1,
            TokenUsage = SumTokenUsage(attempts),
            ReviseRounds = result?.ReviseRounds ?? 0,
            ExitReason = result?.ExitReason,
            ObservedModel = ObservedModelOf(attempts, result?.Model),
            PlanRanCleanWithNoHumanEdits = null,   // only meaningful for WorkflowMap (reserved, not wired in this slice); PR-D wires the no-human-edits signal.
        };
    }

    /// <summary>The cell's wall-clock: every attempt's own duration added up (mirroring the scorecard's per-run projection), null when no attempt recorded both timestamps.</summary>
    private static double? SumDuration(IReadOnlyList<AgentRun> attempts)
    {
        var timed = attempts.Where(a => a.StartedAt is not null && a.CompletedAt is not null).ToList();

        return timed.Count == 0 ? null : timed.Sum(a => (a.CompletedAt!.Value - a.StartedAt!.Value).TotalSeconds);
    }

    /// <summary>The cell's billed tokens: every attempt's usage folded through the executor's OWN summing rule, so a respawned cell reports what it actually cost.</summary>
    private static AgentTokenUsage? SumTokenUsage(IReadOnlyList<AgentRun> attempts) =>
        attempts
            .Select(a => a.ResultJson is { } json ? JsonSerializer.Deserialize<AgentRunResult>(json, AgentJson.Options)?.TokenUsage : null)
            .Aggregate((AgentTokenUsage?)null, AgentRunExecutor.SumTokenUsage);

    /// <summary>The census's harness-reported observed model: <paramref name="gradedModel"/> (the GRADED attempt's own model, already parsed by the caller) when it reported one — that's the tree <see cref="BuildResult"/> judges — else the first EARLIER attempt (a format-fault respawn's first try) that did (mirrors <c>TaskLaunchBenchmarkCellRunner.ObservedModelOf</c>). Null (unknown) when NONE reported one — never backfilled from what was requested.</summary>
    private static string? ObservedModelOf(IReadOnlyList<AgentRun> attempts, string? gradedModel) =>
        gradedModel ?? attempts
            .Select(a => a.ResultJson is { } json ? JsonSerializer.Deserialize<AgentRunResult>(json, AgentJson.Options)?.Model : null)
            .FirstOrDefault(model => model is not null);
}

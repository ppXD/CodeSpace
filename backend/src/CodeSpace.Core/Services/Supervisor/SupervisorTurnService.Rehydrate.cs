using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The replay-fold half of the turn service (Rule 10 <c>.Rehydrate.cs</c>): reads the durable decision tape
/// and derives the per-turn idempotency key. Mirrors the engine's <c>RehydrateMapResultsAsync</c> — replay
/// the settled rows, identify the unsettled one — but per-DECISION rather than per-branch.
/// </summary>
public sealed partial class SupervisorTurnService
{
    public async Task<SupervisorTurnContext> RehydrateFromDecisionLogAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, SupervisorGoalConfig? goalConfig, CancellationToken cancellationToken)
    {
        var rows = await _ledger.GetForRunAsync(supervisorRunId, teamId, cancellationToken).ConfigureAwait(false);

        // The human answers to this run's ask_human turns, keyed by the question card's token. The answer rides
        // the resolved Action wait's payload (the human's free-text comment), so the FOLD reads it durably from
        // the wait on EVERY rehydrate — surviving restart — rather than relying on a separate write into the
        // ledger row. An ask_human decision whose wait isn't yet resolved has no entry → answer stays null.
        // Only hit the DB when the tape actually has an ask_human decision — the common (no-ask) run stays a
        // single ledger read, byte-identical to E3 and DB-free.
        var answersByToken = rows.Any(r => r.DecisionKind == SupervisorDecisionKinds.AskHuman)
            ? await ResolvedAskAnswersByTokenAsync(supervisorRunId, nodeId, cancellationToken).ConfigureAwait(false)
            : EmptyAnswers;

        // The COMPACT terminal results of every agent this run's spawn/retry decisions staged, keyed by agent-run id
        // (SOTA #2). Folded into each TERMINAL spawn/retry decision's outcome below so the decider can SEE what its
        // agents produced. DB-gated: only hit it when the tape actually has a spawn/retry — a no-spawn run stays a
        // single ledger read, byte-identical to the pre-#2 path.
        var agentResultsById = rows.Any(r => SupervisorDecisionKinds.StagesAgents(r.DecisionKind))
            ? await CompactAgentResultsByIdAsync(rows, teamId, cancellationToken).ConfigureAwait(false)
            : EmptyAgentResults;

        // The operator's OBJECTIVE acceptance command (L4 A3) — the argv a resolve verdict is graded against. Null
        // (none / all-blank) → no objective grade runs, the resolver self-report marker stands (byte-identical to pre-A3).
        var acceptanceCommand = NormalizeCommand(goalConfig?.AcceptanceChecks);

        var priorDecisions = new List<SupervisorPriorDecision>();
        SupervisorPriorDecision? inFlight = null;

        // Walk the tape in Sequence order: a TERMINAL row is replayed (outcome only — its side effect is NOT
        // re-run), a non-terminal row is the one in-flight decision (a turn crashed after claim, before the
        // terminal record). TurnNumber = the count of DECIDED (terminal) decisions, which is what drives both
        // the next decision and the per-turn IterationKey — so a re-entry replays exactly the same decisions
        // and re-claims the in-flight one rather than emitting a duplicate.
        foreach (var row in rows)
        {
            var decision = FoldAskHumanAnswer(ToPriorDecision(row), answersByToken);

            // The agent-results fold (SOTA #2) applies ONLY to a TERMINAL spawn/retry decision: its agents are
            // durable terminal facts only once the decision is terminal AND the barrier resumed, and a Running
            // spawn row carrying agentRunIds (the re-park shape) must NEVER be rewritten here — it would be
            // clobbered by the later RecordTerminalAsync.
            if (SupervisorDecisionStateMachine.IsTerminal(row.Status))
            {
                decision = FoldAgentResults(decision, agentResultsById);
                decision = await FoldAcceptanceGradeAsync(decision, goalConfig, acceptanceCommand, teamId, cancellationToken).ConfigureAwait(false);
                priorDecisions.Add(decision);
            }
            else
                inFlight = decision;

            // Persist a newly-folded outcome (an ask_human answer OR settled agent results) onto the durable ledger
            // row, surviving restart without re-resolving. Idempotent — no-ops when the bytes match. After the fold,
            // so a non-terminal row (no agent fold) only ever persists an ask_human answer change.
            if (decision.OutcomeJson != row.OutcomeJson)
                await _ledger.UpdateOutcomeAsync(row.Id, teamId, decision.OutcomeJson!, cancellationToken).ConfigureAwait(false);
        }

        return new SupervisorTurnContext
        {
            Goal = goal,
            SupervisorRunId = supervisorRunId,
            TeamId = teamId,
            NodeId = nodeId,
            TurnNumber = priorDecisions.Count,
            PriorDecisions = priorDecisions,
            InFlight = inFlight,
            TotalSpawnedAgents = FoldTotalSpawnedAgents(priorDecisions),
            RunSpendUsd = FoldRunSpendUsd(priorDecisions),
            NoProgressDecisions = FoldNoProgressDecisions(priorDecisions),
            ApprovalPolicy = SupervisorGoalPlan.From(goalConfig).ApprovalPolicy,
            AgentProfile = goalConfig?.AgentProfile,
            SpawnedAgentTools = NormalizeTools(goalConfig?.AllowedTools),
            AllowedModels = NormalizeModels(goalConfig?.AllowedModels),
            SupervisorModel = string.IsNullOrWhiteSpace(goalConfig?.SupervisorModel) ? null : goalConfig!.SupervisorModel!.Trim(),
        };
    }

    /// <summary>
    /// Sum the agents this run has spawned so far from the DURABLE ledger — every prior <c>spawn</c> / <c>retry</c>
    /// decision's recorded <c>agentCount</c> (the E5 total-spawn cap counter). A LEDGER FACT, so it survives replay
    /// + can't be reset by re-entering the node: a re-entry re-reads the same settled spawn outcomes and re-derives
    /// the same total, so the cap can't be sidestepped by restarting.
    /// </summary>
    private static int FoldTotalSpawnedAgents(IReadOnlyList<SupervisorPriorDecision> priorDecisions) =>
        priorDecisions
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .Sum(d => SupervisorOutcome.ReadStagedAgentCount(d.OutcomeJson));

    /// <summary>
    /// Sum the run's REALIZED USD spend from the DURABLE ledger (SOTA #4) — every prior spawn/retry decision's folded
    /// <c>agentResults</c> (each carrying its priced tokens + model), priced via <see cref="SupervisorOutcome.SpendUsd"/>.
    /// A LEDGER FACT exactly like <see cref="FoldTotalSpawnedAgents"/>: it reads off OutcomeJson (no new query), so it
    /// survives replay + re-entry deterministically. An outcome not yet folded (or folded before token fields existed)
    /// has no agentResults → 0 (fail-open), so cost is realized-spend backpressure that can never block the first spawn.
    /// </summary>
    private static decimal FoldRunSpendUsd(IReadOnlyList<SupervisorPriorDecision> priorDecisions) =>
        priorDecisions
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .Sum(d => SupervisorOutcome.SpendUsd(SupervisorOutcome.ReadAgentResults(d.OutcomeJson)));

    /// <summary>
    /// Count the MOST RECENT consecutive decisions that produced no new SETTLED agent result (the E5 best-effort
    /// no-progress counter, folded from the durable ledger). A decision "made progress" if it staged agents whose
    /// results a later turn can fold (spawn/retry with agents) OR it is a merge that read prior results; a run of
    /// decisions that spawned nothing + merged nothing (e.g. the decider looping on plan / a degraded ask_human)
    /// accumulates. A spawn/retry resets the counter (it advanced the work). DEMOTED to best-effort per the design
    /// — a long-running spawn whose agents haven't settled is a PARK (not a fresh decided turn), so it never trips.
    /// </summary>
    private static int FoldNoProgressDecisions(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var streak = 0;

        foreach (var decision in priorDecisions)
            streak = MadeProgress(decision) ? 0 : streak + 1;

        return streak;
    }

    /// <summary>A decision advanced the work if it staged agents (spawn/retry/resolve — the work fans out) or merged prior results (it consumed them). Plan / ask_human / a zero-agent spawn make no fresh progress toward a settled result.</summary>
    private static bool MadeProgress(SupervisorPriorDecision decision) =>
        SupervisorDecisionKinds.StagesAgents(decision.DecisionKind) ? SupervisorOutcome.ReadStagedAgentCount(decision.OutcomeJson) > 0
        : decision.DecisionKind == SupervisorDecisionKinds.Merge;

    /// <summary>
    /// Fold the human's answer into an ask_human decision's replayed outcome (E4): look up the recorded
    /// question-card token in the resolved-Action-wait answers, and — when the human has replied — rewrite the
    /// decision's <c>OutcomeJson</c> with the answer so the decider sees "you asked X, the human answered Y" on
    /// the next turn. A non-ask_human decision, or an ask_human whose wait isn't yet resolved (no token match),
    /// passes through unchanged. The fold is idempotent — re-running it on an already-folded outcome is a no-op.
    /// </summary>
    private static SupervisorPriorDecision FoldAskHumanAnswer(SupervisorPriorDecision decision, IReadOnlyDictionary<string, string> answersByToken)
    {
        if (decision.DecisionKind != SupervisorDecisionKinds.AskHuman) return decision;

        var token = SupervisorOutcome.ReadHumanWaitToken(decision.OutcomeJson);

        if (token == null || !answersByToken.TryGetValue(token, out var answer)) return decision;

        var folded = SupervisorOutcome.FoldAnswer(SupervisorOutcome.ReadAskHumanQuestion(decision.OutcomeJson), token, answer);

        return decision with { OutcomeJson = folded };
    }

    /// <summary>
    /// The human answer to each resolved ask_human turn, keyed by the question card's correlation token. Read
    /// from the durable resolved <c>Action</c> waits this node staged: the wait's resolved payload is
    /// <c>{ action, by, comment }</c> and the answer is the human's free-text <c>comment</c>. Only RESOLVED
    /// waits contribute (a still-pending ask hasn't been answered), so the fold writes the answer at most once
    /// it durably exists, and re-reads it identically on every restart.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolvedAskAnswersByTokenAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken)
    {
        var resolved = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == supervisorRunId && w.NodeId == nodeId
                        && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Resolved
                        && w.IterationKey.EndsWith("#ask"))
            .Select(w => new { w.Token, w.PayloadJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var answers = new Dictionary<string, string>();

        foreach (var wait in resolved)
            answers[wait.Token] = SupervisorOutcome.ReadAnswerComment(wait.PayloadJson);

        return answers;
    }

    /// <summary>The shared empty answers map for the common no-ask_human rehydrate — keeps that path allocation-light + DB-free.</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyAnswers = new Dictionary<string, string>();

    /// <summary>The shared empty agent-results map for the common no-spawn rehydrate — keeps that path allocation-light + DB-free (the EmptyAnswers analogue for SOTA #2).</summary>
    private static readonly IReadOnlyDictionary<Guid, SupervisorAgentResult> EmptyAgentResults = new Dictionary<Guid, SupervisorAgentResult>();

    /// <summary>
    /// Fold a TERMINAL spawn/retry decision's spawned-agent COMPACT results into its replayed outcome (SOTA #2) —
    /// the FoldAskHumanAnswer analogue for the spawn path, so the decider sees "you spawned these, here is what each
    /// produced" on the next turn. Pass-through UNCHANGED for: a non-spawn/retry decision; a zero-agent spawn (no
    /// recorded ids); and — the redundant-write guard — a spawn whose outcome is ALREADY folded (it carries
    /// agentResults). Skipping an already-folded outcome is sound because a fold runs only AFTER the barrier (all K
    /// agents terminal) so the first fold is complete + the terminal AgentRun rows are immutable; re-folding would
    /// only re-emit a byte-divergent-but-equal jsonb that the persist guard then no-ops at the DB anyway. Skipping it
    /// keeps the in-memory OutcomeJson == the read-back, so the loop's byte-compare is false and NO redundant UPDATE
    /// is issued on later rehydrates.
    ///
    /// <para>Every staged id maps to a result: a resolved agent → its compact result (a terminal Failed agent whose
    /// ResultJson is null still folds <c>{status:Failed, error:&lt;rowError&gt;}</c> — the signal the slice exists to
    /// surface); an UNRESOLVED id (deleted / out-of-team — not reachable today since AgentRun rows are append-only +
    /// parent-terminal-guarded) → an explicit <c>Unknown</c> placeholder, so the folded set is always N-for-N and the
    /// decider never sees a silent hole shorter than agentCount. Ids are iterated in RECORDED spawn order
    /// (replay-deterministic), never DB-row order.</para>
    /// </summary>
    private static SupervisorPriorDecision FoldAgentResults(SupervisorPriorDecision decision, IReadOnlyDictionary<Guid, SupervisorAgentResult> resultsById)
    {
        if (!SupervisorDecisionKinds.StagesAgents(decision.DecisionKind)) return decision;

        if (SupervisorOutcome.ReadAgentResults(decision.OutcomeJson).Count > 0) return decision;   // already folded — durable + immutable; don't re-fold (no redundant UPDATE)

        var ids = SupervisorOutcome.ReadStagedAgentRunIds(decision.OutcomeJson);

        if (ids.Count == 0) return decision;

        var folded = ids.Select(id => resultsById.TryGetValue(id, out var r) ? r : UnknownAgentResult(id)).ToList();

        return decision with { OutcomeJson = SupervisorOutcome.FoldAgentResults(decision.OutcomeJson, folded) };
    }

    /// <summary>
    /// Fold the OBJECTIVE acceptance grade onto a TERMINAL resolve decision (resolver loop #379, L4 A3) — the server-run
    /// verdict that REPLACES the resolver's self-reported marker. Runs the operator's acceptance command against the
    /// resolver's produced branch EXACTLY ONCE: the <see cref="SupervisorOutcome.ReadAcceptanceGradePassed"/> once-guard
    /// short-circuits every later replay, so the clone+run never re-fires and the accept boundary stays a pure tape read
    /// (the named replay-safety risk, defeated structurally). Returns the decision UNCHANGED for any non-resolve verb or
    /// when no acceptance command is configured (byte-identical to pre-A3). FAIL-CLOSED: a missing branch/repo, a failed
    /// grade, or any unexpected error folds <c>passed:false</c> (Unverified) — never a silent accept, never a throw that
    /// would strand the terminal row. Only a genuine cancellation propagates.
    /// </summary>
    private async Task<SupervisorPriorDecision> FoldAcceptanceGradeAsync(SupervisorPriorDecision decision, SupervisorGoalConfig? goalConfig, IReadOnlyList<string>? command, Guid teamId, CancellationToken cancellationToken)
    {
        if (decision.DecisionKind != SupervisorDecisionKinds.Resolve) return decision;

        if (command is null) return decision;

        if (SupervisorOutcome.ReadAcceptanceGradePassed(decision.OutcomeJson).HasValue) return decision;   // already graded — durable on the tape; never re-clone+run

        // SINGLE-repo resolve only (A3): a multi-repo resolver's top-level ProducedBranch mirrors ONLY the primary repo,
        // so grading it would gate EVERY repo's per-repo branch on a primary-only check (a FALSE accept if a secondary
        // is broken). Defer the per-repo grade — fall back to the marker verdict (byte-identical), mirroring how
        // SupervisorOutcome.ResolvedBranch self-excludes a multi-repo resolver via the same RepositoryResults signal.
        if (SupervisorOutcome.ReadAgentResults(decision.OutcomeJson).FirstOrDefault()?.RepositoryResults.Count > 0) return decision;

        var grade = await GradeResolveAcceptanceAsync(decision, goalConfig, command, teamId, cancellationToken).ConfigureAwait(false);

        return decision with { OutcomeJson = SupervisorOutcome.FoldAcceptanceGrade(decision.OutcomeJson, grade.Passed, grade.Detail) };
    }

    /// <summary>Grade one resolve decision's branch against the operator command, fail-closed: no branch/repo → not-accepted; the grader is itself fail-closed; any unexpected non-cancellation escape degrades to not-accepted so the terminal fold can never crash and strand the row.</summary>
    private async Task<BenchmarkGrade> GradeResolveAcceptanceAsync(SupervisorPriorDecision decision, SupervisorGoalConfig? goalConfig, IReadOnlyList<string> command, Guid teamId, CancellationToken cancellationToken)
    {
        var branch = SupervisorOutcome.ReadAgentResults(decision.OutcomeJson).FirstOrDefault()?.ProducedBranch;
        var repositoryId = goalConfig?.AgentProfile?.RepositoryId;

        if (string.IsNullOrEmpty(branch) || repositoryId is null)
            return new BenchmarkGrade { Passed = false, Detail = "no-branch-or-repo" };

        try
        {
            return await _acceptanceGrader.GradeAsync(repositoryId.Value, teamId, branch, command, SupervisorLane.AcceptanceGradeTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Acceptance grade for resolve decision {DecisionId} failed unexpectedly; recording not-accepted", decision.Id);
            return new BenchmarkGrade { Passed = false, Detail = $"grade-error: {ex.Message}" };
        }
    }

    /// <summary>The placeholder for a staged agent-run id that no longer resolves (deleted / out-of-team) — keeps the folded set N-for-N so the decider sees an explicit "this agent is gone" rather than a silently shorter list. Not reachable today (AgentRun rows are append-only), but fail-legible if it ever is.</summary>
    private static SupervisorAgentResult UnknownAgentResult(Guid agentRunId) =>
        new() { AgentRunId = agentRunId, Status = "Unknown", Error = "agent run not found (deleted or out-of-team)" };

    /// <summary>
    /// The COMPACT terminal result of every agent staged by THIS run's spawn/retry decisions, keyed by agent-run id
    /// (SOTA #2) — the ResolvedAskAnswersByTokenAsync analogue. Collects the ids off every spawn/retry outcome, then
    /// loads each AgentRun's {Status, Error, ResultJson} TEAM-SCOPED (defense-in-depth, mirroring the merge read,
    /// NOW including Error so a cancelled/abandoned agent's reason survives), and projects each through the SHARED
    /// <see cref="SupervisorOutcome.ProjectCompact"/> (the one source of truth the merge consumes too).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, SupervisorAgentResult>> CompactAgentResultsByIdAsync(IReadOnlyList<Persistence.Entities.SupervisorDecisionRecord> rows, Guid teamId, CancellationToken cancellationToken)
    {
        var ids = rows
            .Where(r => SupervisorDecisionKinds.StagesAgents(r.DecisionKind))
            .SelectMany(r => SupervisorOutcome.ReadStagedAgentRunIds(r.OutcomeJson))
            .Distinct()
            .ToList();

        if (ids.Count == 0) return EmptyAgentResults;

        var runs = await _db.AgentRun.AsNoTracking()
            .Where(r => ids.Contains(r.Id) && r.TeamId == teamId)
            .Select(r => new { r.Id, r.Status, r.Error, r.ResultJson, r.TaskJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // SOTA #4: thread the agent's model (from TaskJson — there is no Model column) into ProjectCompact so the
        // durable agentResults carry the priced inputs. Reuses the existing team-scoped load — no extra query.
        return runs.ToDictionary(r => r.Id, r => SupervisorOutcome.ProjectCompact(r.Id, r.Status.ToString(), r.Error, r.ResultJson, ReadModel(r.TaskJson)));
    }

    /// <summary>The agent's model off its task envelope (TaskJson), best-effort (malformed → null) — the price key for the cost fold.</summary>
    private static string? ReadModel(string? taskJson)
    {
        if (string.IsNullOrWhiteSpace(taskJson)) return null;
        try { return JsonSerializer.Deserialize<AgentTask>(taskJson, AgentJson.Options)?.Model; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Normalise the supervisor config's reused <c>AllowedTools</c> into the spawned-agent tool allow-list (P2-3),
    /// preserving the <c>AgentTask.Tools</c> tri-state exactly as agent.code's ReadStringArray: null = inherit the
    /// harness default (and the pre-P2-3 / no-config path), present = the non-blank elements (an empty list stays
    /// empty = no tools). Blanks are dropped so a stray <c>""</c> in the config never reads as a tool.
    /// </summary>
    private static IReadOnlyList<string>? NormalizeTools(IReadOnlyList<string>? allowedTools) =>
        allowedTools?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

    /// <summary>Normalise the operator's allowed model pool: drop blanks + trim, and collapse an empty / all-blank list to <c>null</c> ("no restriction") so a configured-but-empty pool reads as unbounded (byte-identical), never as "no model allowed".</summary>
    private static IReadOnlyList<string>? NormalizeModels(IReadOnlyList<string>? allowedModels)
    {
        var models = allowedModels?.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToList();

        return models is { Count: > 0 } ? models : null;
    }

    /// <summary>Normalise the operator's acceptance command (L4 A3) into a runnable argv: drop blank elements, and — UNLIKE the tool tri-state — collapse an empty/all-blank list to <c>null</c> ("no objective grade; the resolver self-report marker stands"), so a configured-but-empty list never grades.</summary>
    private static IReadOnlyList<string>? NormalizeCommand(IReadOnlyList<string>? command)
    {
        var argv = command?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();

        return argv is { Count: > 0 } ? argv : null;
    }

    /// <summary>
    /// How many WorkflowRun ancestors this supervisor run already has (PR-E E5 depth cap) — walks the
    /// <c>parent_run_id</c> chain exactly as <c>SubworkflowService.EnsureWithinDepthAsync</c> does, bounded by
    /// <see cref="SupervisorLane.MaxSupervisorDepth"/> so a corrupt cycle can't loop forever. The pre-decision
    /// depth bound force-STOPs a supervisor nested beyond this many ancestors (a recursive supervisor-spawns-
    /// supervisor fan-out), at turn 0, before it can spawn. A top-level supervisor (no parent) reads depth 0.
    /// </summary>
    public async Task<int> SupervisorDepthAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        // A pure-unit context (no DbContext) has no run hierarchy → depth 0 (top-level), so the depth bound
        // never trips a db-less test. The real engine always supplies _db; only fakes pass db: null!.
        if (_db == null) return 0;

        var depth = 0;

        var cursor = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == supervisorRunId && r.TeamId == teamId)
            .Select(r => r.ParentRunId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        while (cursor.HasValue && depth < SupervisorLane.MaxSupervisorDepth)
        {
            depth++;

            cursor = await _db.WorkflowRun.AsNoTracking()
                .Where(r => r.Id == cursor.Value)
                .Select(r => r.ParentRunId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        return depth;
    }

    public async Task<int> CountPendingAgentWaitsAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunWait.AsNoTracking()
            .CountAsync(w => w.RunId == supervisorRunId && w.NodeId == nodeId
                             && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending, cancellationToken)
            .ConfigureAwait(false);

    public async Task<string?> PendingHumanWaitTokenAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == supervisorRunId && w.NodeId == nodeId
                        && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending && w.IterationKey.EndsWith("#ask"))
            .Select(w => w.Token)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private static SupervisorPriorDecision ToPriorDecision(Persistence.Entities.SupervisorDecisionRecord row) => new()
    {
        Id = row.Id,
        Sequence = row.Sequence,
        DecisionKind = row.DecisionKind,
        Status = row.Status,
        PayloadJson = row.PayloadJson,
        OutcomeJson = row.OutcomeJson,
        Error = row.Error,
    };

    /// <summary>
    /// Server-derived per-TURN idempotency key (must-fix #1's exactly-once partner): bind the decision's
    /// canonical payload to a <c>turn{N}</c> discriminator so the SAME decision in a later turn is a DISTINCT,
    /// re-executable ledger row (no unique-index collision across turns), while a re-derived key in the SAME
    /// turn collides → the replay path. Never read from any model — the inputs (kind + payload + turn) are
    /// all server-side.
    /// </summary>
    private static string DeriveDecisionKey(SupervisorDecision decision, int turnNumber) =>
        SupervisorDecisionLog.DeriveIdempotencyKey(decision.Kind, decision.PayloadJson, TurnDiscriminator(turnNumber));

    /// <summary>The per-turn discriminator <c>turn{N}</c> — the ledger-key analogue of the wait's IterationKey turn segment.</summary>
    internal static string TurnDiscriminator(int turnNumber) => $"turn{turnNumber}";

    /// <summary>
    /// The forced terminal decision when a fail-closed bound or governance refusal trips (PR-E E2/E5) — a
    /// <c>stop</c> stamping the DISTINCT <paramref name="reason"/> (a <see cref="SupervisorStopReasons"/> value).
    /// DETERMINISTIC given (reason): a re-entry after the same bound tripped re-derives the identical stop, so the
    /// per-turn idempotency key is stable and the run terminates cleanly with the operator-legible reason.
    /// </summary>
    private static SupervisorDecision ForcedStop(string reason) => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new { reason }, AgentJson.Options),
    };

    /// <summary>The terminal reason stamped on a budget-forced stop (back-compat alias for the E2 reason; points at the E5 vocabulary). Surfaced as the node's terminal reason.</summary>
    public const string BudgetExhaustedReason = SupervisorStopReasons.BudgetExhausted;

    /// <summary>Read the <c>reason</c> from a stop decision's payload for the node's terminal output (best-effort; null when absent/malformed).</summary>
    private static string? ReadStopReason(SupervisorDecision decision)
    {
        try
        {
            var root = JsonDocument.Parse(decision.PayloadJson).RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

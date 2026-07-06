using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Dtos.Decisions;
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

        // D4c-2: the PENDING decisions this run's child agent runs are blocked on — read off the cross-grain queue for the
        // arbiter to drain THIS turn (auto-answer or leave for a human). DB-gated on the SAME StagesAgents predicate as the
        // agent-results fold so a no-spawn run never hits the queue read (byte-identical, single ledger read); an empty
        // child-id set is a free no-DB return (ListPendingForAgentRunsAsync short-circuits on it).
        var pendingChildDecisions = rows.Any(r => SupervisorDecisionKinds.StagesAgents(r.DecisionKind))
            ? await _decisionQueue.ListPendingForAgentRunsAsync(StagedChildAgentRunIds(rows), teamId, cancellationToken).ConfigureAwait(false)
            : EmptyPendingDecisions;

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
                decision = await FoldUnitAcceptanceGradeAsync(decision, priorDecisions, goalConfig, teamId, cancellationToken).ConfigureAwait(false);
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
            AcceptanceChecks = acceptanceCommand,
            SpawnedAgentTools = NormalizeTools(goalConfig?.AllowedTools),
            AllowedModelIds = NormalizeModelIds(goalConfig?.AllowedModelIds),
            AllowedAgentDefinitionIds = NormalizeModelIds(goalConfig?.AllowedAgentDefinitionIds),
            AcceptanceCriteria = NormalizeTools(goalConfig?.AcceptanceCriteria),
            RequirePlanConfirmation = goalConfig?.RequirePlanConfirmation == true,
            SupervisorModelId = goalConfig?.SupervisorModelId,
            DecisionReviewMode = goalConfig?.DecisionReviewMode ?? ReviewMode.None,
            PlanReviewMode = goalConfig?.PlanReviewMode ?? ReviewMode.None,
            ReviewerAgent = goalConfig?.ReviewerAgent ?? false,
            ReviewerModelId = goalConfig?.ReviewerModelId,
            PendingChildDecisions = pendingChildDecisions,
        };
    }

    /// <summary>The shared empty pending-decision list for the common no-spawn rehydrate — keeps that path allocation-light + DB-free (the EmptyAgentResults analogue for D4c-2).</summary>
    private static readonly IReadOnlyList<PendingDecision> EmptyPendingDecisions = Array.Empty<PendingDecision>();

    /// <summary>The DISTINCT agent-run ids this run's spawn/retry/resolve decisions staged (in recorded spawn order) — the key both the agent-results fold and the pending-decision read fan out over. Lifted so the two reads share ONE id source.</summary>
    private static List<Guid> StagedChildAgentRunIds(IReadOnlyList<Persistence.Entities.SupervisorDecisionRecord> rows) =>
        rows
            .Where(r => SupervisorDecisionKinds.StagesAgents(r.DecisionKind))
            .SelectMany(r => SupervisorOutcome.ReadStagedAgentRunIds(r.OutcomeJson))
            .Distinct()
            .ToList();

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
    /// Count the MOST RECENT consecutive decisions that produced no new SETTLED EVIDENCE of progress (the no-progress
    /// stall counter, folded from the durable ledger). EVIDENCE-based (W3), not staged-count: a spawn/retry advances the
    /// work only if its folded agents carry real evidence (a Succeeded agent or a concrete artifact — see
    /// <see cref="SupervisorOutcome.HasSettledEvidence"/>); a merge advances it (it consumed prior results). A run of
    /// decisions that produced nothing — a wave of all-FAILED/empty agents, the decider looping on plan, or a degraded
    /// ask_human — accumulates and eventually trips the stall bound. A LEDGER FACT (reads the same folded OutcomeJson the
    /// decider sees), so it survives replay + re-entry deterministically. A long-running spawn whose agents haven't
    /// settled is a PARK (not yet a terminal decision in <paramref name="priorDecisions"/>), so it never trips early.
    /// </summary>
    private static int FoldNoProgressDecisions(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var streak = 0;

        foreach (var decision in priorDecisions)
            streak = MadeProgress(decision) ? 0 : streak + 1;

        return streak;
    }

    /// <summary>A staging decision (spawn/retry/resolve) advanced the work only if its folded agents produced SETTLED EVIDENCE — a Succeeded agent or a real artifact (see <see cref="SupervisorOutcome.HasSettledEvidence"/>); a merge advanced it (it consumed prior results); an ANSWERED plan-confirmation card advanced it too (an operator actively steering the plan is engagement, not a stall — without this, a couple of revise loops would trip the no-progress guard mid-conversation). A wave of all-failed/empty agents, a plan, or a content ask_human makes no fresh progress.</summary>
    private static bool MadeProgress(SupervisorPriorDecision decision) =>
        SupervisorDecisionKinds.StagesAgents(decision.DecisionKind)
            ? SupervisorOutcome.HasSettledEvidence(SupervisorOutcome.ReadAgentResults(decision.OutcomeJson))
            : decision.DecisionKind == SupervisorDecisionKinds.Merge || SupervisorPlanConfirmation.IsAnsweredConfirmationCard(decision);

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
            // The operator floor is always the TestsPass oracle (kind never model-authored on this path).
            return await _acceptanceGrader.GradeAsync(repositoryId.Value, teamId, branch, new SupervisorAcceptanceSpec { Command = command }, SupervisorLane.AcceptanceGradeTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Acceptance grade for resolve decision {DecisionId} failed unexpectedly; recording not-accepted", decision.Id);
            return new BenchmarkGrade { Passed = false, Detail = $"grade-error: {ex.Message}" };
        }
    }

    /// <summary>
    /// Fold the per-UNIT OBJECTIVE acceptance grade onto a TERMINAL spawn/retry decision (loopability slice 3) — each
    /// spawned unit whose PLANNED subtask authored an <see cref="SupervisorAcceptanceSpec"/> is graded on ITS OWN
    /// produced branch, so a unit's "done" is a server-verified fact, not the agent's self-report. The verdict stamps
    /// onto that unit's <see cref="SupervisorAgentResult"/> (so a FAILED unit is the precise retry target the decider
    /// sees AND is discounted from the no-progress evidence — a branch pushed but REJECTED is not progress). Runs
    /// EXACTLY ONCE: a re-fold is skipped the moment any unit already carries a verdict (durable on the tape), so the
    /// clone+grade never re-fires (the replay-safety contract, same as the resolve/stop folds). Returns UNCHANGED —
    /// byte-identical — for: a non-spawn/retry verb (resolve has its own grade); a plan whose subtasks carry NO
    /// acceptance (the dominant case); a unit whose subtask carries none; and a MULTI-repo unit (its top-level branch
    /// mirrors only the primary, so a per-agent multi-repo grade is deferred, mirroring the resolve fold's deferral).
    /// FAIL-CLOSED: a missing branch/repo or a grader error folds <c>passed:false</c> — never a silent accept, never a
    /// throw that would strand the terminal row.
    /// </summary>
    private async Task<SupervisorPriorDecision> FoldUnitAcceptanceGradeAsync(SupervisorPriorDecision decision, IReadOnlyList<SupervisorPriorDecision> priorDecisions, SupervisorGoalConfig? goalConfig, Guid teamId, CancellationToken cancellationToken)
    {
        if (decision.DecisionKind is not (SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)) return decision;

        var results = SupervisorOutcome.ReadAgentResults(decision.OutcomeJson);

        if (results.Count == 0) return decision;

        if (results.Any(r => r.AcceptancePassed.HasValue)) return decision;   // already graded — durable on the tape; never re-clone+grade

        var acceptanceBySubtask = ResolvePlannedAcceptance(priorDecisions);

        if (acceptanceBySubtask.Count == 0) return decision;   // no subtask authored a contract → byte-identical (the dominant case)

        var unitSubtaskIds = UnitSubtaskIds(decision);
        var repoOverrides = DispatchRepoOverrides(decision);

        var graded = new List<SupervisorAgentResult>(results.Count);
        var anyGraded = false;

        for (var i = 0; i < results.Count; i++)
        {
            var subtaskId = i < unitSubtaskIds.Count ? unitSubtaskIds[i] : null;
            var spec = subtaskId is not null && acceptanceBySubtask.TryGetValue(subtaskId, out var found) ? found : null;
            var command = spec is not null ? NormalizeCommand(spec.Command) : null;

            // Ungraded — keep the result byte-identical — when: the unit has no contract; its command is all-blank; or
            // its agent ran MULTI-repo (RepositoryResults present → the per-agent multi-repo grade is deferred, exactly
            // as the resolve fold defers a multi-repo resolver rather than grade its primary-only top-level branch).
            if (command is null || results[i].RepositoryResults.Count > 0)
            {
                graded.Add(results[i]);
                continue;
            }

            var repositoryId = (subtaskId is not null && repoOverrides.TryGetValue(subtaskId, out var overrideRepo) ? overrideRepo : (Guid?)null)
                               ?? goalConfig?.AgentProfile?.RepositoryId;

            // The subtask authored its OWN oracle — the FULL spec rides (kind + rubric/schema payloads, triad S7).
            var grade = await GradeUnitAcceptanceAsync(results[i], repositoryId, spec! with { Command = command }, teamId, decision.Id, cancellationToken).ConfigureAwait(false);

            graded.Add(results[i] with { AcceptancePassed = grade.Passed, AcceptanceDetail = grade.Detail });
            anyGraded = true;
        }

        if (!anyGraded) return decision;   // every contract-bearing unit was deferred (multi-repo) → byte-identical; a replay re-attempt is a pure no-op (no grade I/O ran)

        return decision with { OutcomeJson = SupervisorOutcome.FoldAgentResults(decision.OutcomeJson, graded) };
    }

    /// <summary>Grade ONE unit's produced branch against its subtask acceptance SPEC, fail-closed: no branch/repo → not-accepted; the grader is itself fail-closed; any unexpected non-cancellation escape degrades to not-accepted so the terminal fold can never crash + strand the row. Only a genuine cancellation propagates.</summary>
    private async Task<BenchmarkGrade> GradeUnitAcceptanceAsync(SupervisorAgentResult result, Guid? repositoryId, SupervisorAcceptanceSpec spec, Guid teamId, Guid decisionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(result.ProducedBranch) || repositoryId is null)
            return new BenchmarkGrade { Passed = false, Detail = "no-branch-or-repo" };

        try
        {
            return await _acceptanceGrader.GradeAsync(repositoryId.Value, teamId, result.ProducedBranch, spec, SupervisorLane.AcceptanceGradeTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Per-unit acceptance grade for agent {AgentRunId} in decision {DecisionId} failed unexpectedly; recording not-accepted", result.AgentRunId, decisionId);
            return new BenchmarkGrade { Passed = false, Detail = $"grade-error: {ex.Message}" };
        }
    }

    /// <summary>The OBJECTIVE acceptance each planned subtask authored (loopability slice 1), keyed by subtask id — read off the MOST RECENT <c>plan</c> decision's payload. Only subtasks WITH an acceptance contract are included; an empty map (a flat plan) means no per-unit grade runs. Duplicate ids keep the first (defensive; a plan shouldn't repeat an id).</summary>
    private static IReadOnlyDictionary<string, SupervisorAcceptanceSpec> ResolvePlannedAcceptance(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        for (var i = priorDecisions.Count - 1; i >= 0; i--)
        {
            if (priorDecisions[i].DecisionKind != SupervisorDecisionKinds.Plan) continue;

            var map = new Dictionary<string, SupervisorAcceptanceSpec>();

            foreach (var subtask in SupervisorOutcome.ReadPlanSubtasks(priorDecisions[i].PayloadJson))
                if (subtask.Acceptance is { } acceptance && !map.ContainsKey(subtask.Id))
                    map[subtask.Id] = acceptance;

            return map;
        }

        return EmptyAcceptance;
    }

    /// <summary>The shared empty acceptance map for the common no-contract plan — keeps that path allocation-light.</summary>
    private static readonly IReadOnlyDictionary<string, SupervisorAcceptanceSpec> EmptyAcceptance = new Dictionary<string, SupervisorAcceptanceSpec>();

    /// <summary>The subtask id each unit of a spawn (positional, the fan-out order) or a retry (one) ran — the positional join to the folded agentResults (<c>results[i]</c> ran <c>subtaskIds[i]</c>, the SAME order the executor staged them in).</summary>
    private static IReadOnlyList<string> UnitSubtaskIds(SupervisorPriorDecision decision) =>
        decision.DecisionKind == SupervisorDecisionKinds.Spawn
            ? SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson)
            : SupervisorOutcome.ReadRetrySubtaskId(decision.PayloadJson) is { } id ? new[] { id } : Array.Empty<string>();

    /// <summary>The per-agent repository OVERRIDE (L4 arc B) each dispatch authored, keyed by subtask id — so a repo-overridden unit is graded against the repo its agent ACTUALLY wrote to, not the profile default (a primary-repo grade of an override would be a false verdict). Empty for a homogeneous spawn (no <c>agents[]</c>) or a retry. First-match per id, mirroring the executor's own <c>DispatchFor</c> lookup.</summary>
    private static IReadOnlyDictionary<string, Guid> DispatchRepoOverrides(SupervisorPriorDecision decision)
    {
        if (decision.DecisionKind != SupervisorDecisionKinds.Spawn) return EmptyRepoOverrides;

        SupervisorSpawnPayload? spawn;
        try { spawn = JsonSerializer.Deserialize<SupervisorSpawnPayload>(decision.PayloadJson, AgentJson.Options); }
        catch (JsonException) { return EmptyRepoOverrides; }

        if (spawn?.Agents is not { Count: > 0 } agents) return EmptyRepoOverrides;

        var map = new Dictionary<string, Guid>();

        foreach (var dispatch in agents)
            if (dispatch.RepositoryId is { } repo && !map.ContainsKey(dispatch.SubtaskId))
                map[dispatch.SubtaskId] = repo;

        return map;
    }

    /// <summary>The shared empty repo-override map for the common homogeneous spawn — keeps that path allocation-light.</summary>
    private static readonly IReadOnlyDictionary<string, Guid> EmptyRepoOverrides = new Dictionary<string, Guid>();

    /// <summary>
    /// Grade a terminal STOP against BOTH acceptance gates (L4 P1 + C1) inline on the decided-stop path, folding ONE
    /// combined verdict onto the stop outcome so <see cref="BuildResult"/> reads it back off <c>execution.OutcomeJson</c>:
    /// <list type="bullet">
    ///   <item>the OPERATOR floor (<see cref="SupervisorTurnContext.AcceptanceChecks"/>) — MANDATORY when set, so the
    ///         model can never ship a CLEAN (no-conflict) run past it; the resolve path only enforces the floor when a
    ///         conflict produced a resolve, leaving a clean run's final head ungated until C1.</item>
    ///   <item>the MODEL's own stop command (<see cref="SupervisorAcceptanceSpec.Command"/>) — an ADDITIONAL tightening
    ///         the brain authored; it can only NARROW acceptance, never manufacture it.</item>
    /// </list>
    /// Both gates run against EVERY reviewable head the run is shipping and ALL must pass (each gate absent ⇒ vacuously
    /// passes; both absent ⇒ a byte-identical no-op, the dominant case). The head set is the run's SINGLE integrated
    /// branch (single-repo) OR every per-repo final head (multi-repo, L4 C2) — a multi-repo change is all-or-nothing, so
    /// ANY repo failing the floor withholds the WHOLE set. An empty head set (analysis-only / un-integrated work) ⇒ SKIP,
    /// never a fail-closed mislabel of a legitimately branchless run.
    /// </summary>
    private async Task<SupervisorExecution> ApplyStopAcceptanceGradeAsync(SupervisorExecution execution, SupervisorTurnContext context, SupervisorDecision decision, Guid teamId, CancellationToken cancellationToken)
    {
        if (decision.Kind != SupervisorDecisionKinds.Stop) return execution;

        // Operator floor FIRST (the mandatory gate), then the model's tightening — short-circuited in order so the
        // verdict detail names the gate that failed. The floor is ALWAYS the TestsPass oracle (operator-controlled — the
        // model never authors the floor's kind); only the model-check gate honors the model-authored acceptance Kind.
        var modelAcceptance = ReadStopAcceptance(decision.PayloadJson);

        // Each gate carries its FULL spec (triad S7 — a model-check's rubric/schema payload rides the spec); the
        // operator floor stays a bare TestsPass argv spec (the operator never authors a kind on this path).
        var floorCommand = NormalizeCommand(context.AcceptanceChecks);
        var modelCommand = NormalizeCommand(modelAcceptance?.Command);

        var gates = new (string Label, SupervisorAcceptanceSpec? Spec)[]
        {
            ("operator-floor", floorCommand is null ? null : new SupervisorAcceptanceSpec { Command = floorCommand }),
            ("model-check", modelCommand is null || modelAcceptance is null ? null : modelAcceptance with { Command = modelCommand }),
        };

        if (gates.All(g => g.Spec is null)) return execution;   // no operator floor + no model criterion → byte-identical no-op (the dominant case)

        var targets = ResolveAcceptanceTargets(context);

        if (targets.Count == 0) return execution;   // nothing to grade against (analysis-only / un-integrated) → skip

        var grade = await GradeStopTargetsAsync(teamId, targets, gates, cancellationToken).ConfigureAwait(false);

        return execution with { OutcomeJson = SupervisorOutcome.AppendAcceptanceGrade(execution.OutcomeJson, grade.Passed, grade.Detail) };
    }

    /// <summary>
    /// The reviewable head(s) a terminal stop grades against: the SINGLE integrated branch (single-repo) when present,
    /// else EVERY per-repo final head (multi-repo, L4 C2). A multi-repo head missing its repository id can't be cloned,
    /// so it is kept with <see cref="Guid.Empty"/> — the grader fails it CLOSED rather than silently passing an
    /// un-gradeable repo. Empty ⇒ branchless (analysis-only / un-integrated) ⇒ the caller skips.
    /// </summary>
    private static IReadOnlyList<(Guid RepositoryId, string Alias, string Branch)> ResolveAcceptanceTargets(SupervisorTurnContext context)
    {
        var integrated = SupervisorOutcome.ReadFinalIntegratedBranch(context.PriorDecisions);

        if (!string.IsNullOrEmpty(integrated) && context.AgentProfile?.RepositoryId is { } repoId)
            return new[] { (repoId, "", integrated) };

        return SupervisorOutcome.ReadFinalRepositoryBranches(context.PriorDecisions)
            .Where(b => !string.IsNullOrEmpty(b.SourceBranch))
            .Select(b => (b.RepositoryId ?? Guid.Empty, b.Alias, b.SourceBranch))
            .ToList();
    }

    /// <summary>
    /// Grade each PRESENT gate against EVERY target head IN ORDER (every repo must satisfy every gate), short-circuiting
    /// on the first failure (the verdict detail names the failing repo + gate). All-pass ⇒ accepted. Fail-closed: an
    /// un-gradeable repo (no id) and any unexpected non-cancellation grader escape degrade to not-accepted so the
    /// terminal record can never crash + strand the row. Only a genuine cancellation propagates.
    ///
    /// <para>The grades run SEQUENTIALLY (one clone-and-run per repo×gate). The target count is the OPERATOR's
    /// integrated-repo set — the repos bound to this run's workspace, NOT a model-controlled fan-out — so it is bounded
    /// by operator config, not by the brain; there is deliberately no supervisor cap here (a cap would limit the
    /// operator's own workspace, not the model). The first-failure short-circuit keeps the common rejected case cheap;
    /// a future perf slice could grade with a bounded degree-of-parallelism if a large workspace makes wall-clock bite.</para>
    /// </summary>
    private async Task<BenchmarkGrade> GradeStopTargetsAsync(Guid teamId, IReadOnlyList<(Guid RepositoryId, string Alias, string Branch)> targets, IReadOnlyList<(string Label, SupervisorAcceptanceSpec? Spec)> gates, CancellationToken cancellationToken)
    {
        foreach (var target in targets)
        {
            var repoTag = string.IsNullOrEmpty(target.Alias) ? "" : $"repo '{target.Alias}': ";

            if (target.RepositoryId == Guid.Empty) return new BenchmarkGrade { Passed = false, Detail = $"{repoTag}no repository id to grade against" };

            foreach (var (label, spec) in gates)
            {
                if (spec is null) continue;

                BenchmarkGrade grade;
                try
                {
                    grade = await _acceptanceGrader.GradeAsync(target.RepositoryId, teamId, target.Branch, spec, SupervisorLane.AcceptanceGradeTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Stop acceptance {Gate} grade for repo {Repo} failed unexpectedly; recording not-accepted", label, target.Alias);
                    return new BenchmarkGrade { Passed = false, Detail = $"{repoTag}{label}: grade-error: {ex.Message}" };
                }

                if (!grade.Passed) return new BenchmarkGrade { Passed = false, Detail = $"{repoTag}{label}: {grade.Detail}" };
            }
        }

        return new BenchmarkGrade { Passed = true, Detail = "accepted" };
    }

    /// <summary>The model-authored acceptance spec off a stop decision's payload (<see cref="SupervisorStopPayload.Acceptance"/> — its command + oracle Kind), best-effort (null when absent / malformed).</summary>
    private static SupervisorAcceptanceSpec? ReadStopAcceptance(string payloadJson)
    {
        try { return JsonSerializer.Deserialize<SupervisorStopPayload>(payloadJson, AgentJson.Options)?.Acceptance; }
        catch (JsonException) { return null; }
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
        var ids = StagedChildAgentRunIds(rows);

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

    /// <summary>Normalise an operator-allowed Guid pool — the credentialed-model row ids AND the agent-definition (persona) ids share this exact shape: drop empty Guids, dedupe, and collapse an empty list to <c>null</c> ("the pool is ALL of the team's"), so a configured-but-empty pool reads as unbounded, never as "nothing allowed".</summary>
    private static IReadOnlyList<Guid>? NormalizeModelIds(IReadOnlyList<Guid>? pool)
    {
        var ids = pool?.Where(id => id != Guid.Empty).Distinct().ToList();

        return ids is { Count: > 0 } ? ids : null;
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

    /// <summary>Read the <c>reason</c> from a stop decision's payload for the node's terminal output (best-effort; null when absent/malformed) — delegates to the shared <see cref="SupervisorOutcome.ReadStopReason"/> so the forced-stop reason has ONE reader.</summary>
    private static string? ReadStopReason(SupervisorDecision decision) => SupervisorOutcome.ReadStopReason(decision.PayloadJson);
}

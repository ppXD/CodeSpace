using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The MULTI-TURN trajectory harness (A3) — the honest follow-up to the single-decision golden eval. It drives a
/// supervisor decider TURN BY TURN over a SIMULATED environment: each decision's outcome is folded into the ledger and
/// fed back as the next turn's context, exactly as the real engine would, until the decider STOPS or a turn cap / a
/// wall-clock deadline is hit. This measures what a single decision cannot — does the brain DRIVE TO COMPLETION and
/// STOP AT THE RIGHT TIME (after a real shippable result), rather than loop forever, quit empty, or give up.
///
/// <para>The environment is pluggable (<see cref="ISupervisorTrajectoryEnvironment"/>): the SUCCESS path
/// (<see cref="SupervisorTrajectoryEnvironments.HappyPath"/>), plus two RECOVERY paths — a merge CONFLICT the brain must
/// resolve+verify before it can ship (<see cref="SupervisorTrajectoryEnvironments.ConflictThenResolve"/>) and an agent
/// FAILURE the brain must retry before it can ship (<see cref="SupervisorTrajectoryEnvironments.FailureThenRetry"/>). The
/// decider under test is swapped: a scripted one pins the harness + scorer (always-on), the REAL model proves its
/// trajectory judgment against a live endpoint (the real-model gate). Pure of Postgres — it folds contexts in memory via
/// the same <c>SupervisorOutcome</c> helpers the engine uses, so the context is what the brain really reads.</para>
/// </summary>
public static class SupervisorTrajectory
{
    private static readonly Guid Brain = SupervisorDecisionGoldenScenarios.BrainModelRowId;

    /// <summary>The turn cap an arc gets unless it declares its own — headroom for a replan or an ask over a 4-6 turn sound run. See <see cref="ISupervisorTrajectoryEnvironment.MaxTurns"/>.</summary>
    public const int DefaultMaxTurns = 8;

    /// <summary>Drive <paramref name="decider"/> over the SUCCESS path (back-compat overload).</summary>
    public static Task<SupervisorTrajectoryResult> RunAsync(ISupervisorDecider decider, int maxTurns, CancellationToken cancellationToken) =>
        RunAsync(decider, SupervisorTrajectoryEnvironments.HappyPath, maxTurns, cancellationToken);

    /// <summary>Run <paramref name="decider"/> turn by turn over <paramref name="environment"/> until it stops, <paramref name="maxTurns"/> is reached, or <paramref name="cancellationToken"/> (a wall-clock deadline) cancels it. A cancellation is converted into a clean non-stop result (scored as a failure) rather than thrown, so a slow real-model endpoint surfaces a legible verdict instead of an opaque CI timeout. Returns the ordered decision kinds, whether a terminal stop was reached, and the folded ledger the scorer reads.</summary>
    public static async Task<SupervisorTrajectoryResult> RunAsync(ISupervisorDecider decider, ISupervisorTrajectoryEnvironment environment, int maxTurns, CancellationToken cancellationToken)
    {
        var priors = new List<SupervisorPriorDecision>();
        var kinds = new List<string>();

        for (var turn = 0; turn < maxTurns; turn++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // The bound counters production folds off this very tape. Taken from the PRODUCTION folds, not recomputed:
            // SupervisorBoundsRecitation renders NOTHING while both are zero, so leaving them unset silently deleted
            // the "no-progress decisions: N of 8" / "agents spawned: N" block from every trajectory prompt — and the
            // gate then failed the model for looping without ever telling it that it was looping. A second
            // implementation here would drift straight back into that.
            var context = new SupervisorTurnContext
            {
                Goal = SupervisorDecisionGoldenScenarios.FixtureGoal,
                TurnNumber = turn,
                PriorDecisions = priors.ToList(),
                SupervisorModelId = Brain,
                MaxResolveAttempts = environment.MaxResolveAttempts,
                TotalSpawnedAgents = SupervisorTurnService.FoldTotalSpawnedAgents(priors),
                NoProgressDecisions = SupervisorTurnService.FoldNoProgressDecisions(priors),
                // The stopped-now recital, through the SAME projection production's composer reduces to — null until
                // an authorized wave has staked an obligation, exactly as production omits the block until then.
                CompletionRecital = SupervisorStopNowRecital.Render(SupervisorTapeCompletion.ProjectIfStoppedNow(priors)),
            };

            SupervisorDecision decision;
            try
            {
                decision = await decider.DecideAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;   // OUR wall-clock deadline fired mid-decision — fall through to a clean non-stop result (a scored failure, never an opaque CI timeout). A per-call HttpClient timeout (an OperationCanceledException whose token is NOT ours) is deliberately NOT caught here — it must propagate honestly rather than masquerade as a turn-cap loop.
            }

            kinds.Add(decision.Kind);

            if (decision.IsTerminal) return new SupervisorTrajectoryResult { Kinds = kinds, ReachedStop = true, HitTurnCap = false, Ledger = priors };

            priors.Add(environment.Fold(decision, turn, priors));
        }

        // Exhausted the turn cap (the brain loops) OR a deadline cancelled it — HitTurnCap distinguishes the two so the
        // scorer names the failure precisely (a true loop vs. a slow run that never converged inside the time budget).
        return new SupervisorTrajectoryResult { Kinds = kinds, ReachedStop = false, HitTurnCap = !cancellationToken.IsCancellationRequested, Ledger = priors };
    }
}

/// <summary>An environment the trajectory harness drives the decider over: it folds the decided action into the durable-shape outcome the NEXT turn reads, given the ledger so far — the SAME <c>SupervisorOutcome</c> shapes the engine writes, so the decider reads exactly what it would in production.</summary>
public interface ISupervisorTrajectoryEnvironment
{
    SupervisorPriorDecision Fold(SupervisorDecision decision, long sequence, IReadOnlyList<SupervisorPriorDecision> priorsSoFar);

    /// <summary>
    /// The resolve budget this environment's arc REQUIRES. An environment that can only ship via a second
    /// reconciliation must say so: the lane default is ONE, under which the decider's action mask correctly tells
    /// the model a further resolve would force-stop the run — so an arc silently needing two would be scoring the
    /// model on disobeying its own prompt, and a brain that obeyed would be marked wrong.
    /// </summary>
    int MaxResolveAttempts => SupervisorLane.DefaultMaxResolveAttempts;

    /// <summary>
    /// The turn budget this environment's arc REQUIRES, for the same reason as <see cref="MaxResolveAttempts"/>: a
    /// cap below the arc's own honest minimum scores the model on a move it was never given room to make. The
    /// default suits the four arcs whose shortest sound run is 4-6 turns; an arc that needs more says so.
    /// </summary>
    int MaxTurns => SupervisorTrajectory.DefaultMaxTurns;
}

/// <summary>The trajectory environments: the SUCCESS path + the two RECOVERY paths (a merge conflict the brain must resolve, an agent failure the brain must retry). Stateless singletons — each reads only the ledger passed to <see cref="ISupervisorTrajectoryEnvironment.Fold"/>.</summary>
public static class SupervisorTrajectoryEnvironments
{
    /// <summary>The SUCCESS path: spawned/retried work succeeds, a merge is CLEAN, a resolve is VERIFIED — so a competent brain converges plan→spawn→merge→stop.</summary>
    public static ISupervisorTrajectoryEnvironment HappyPath { get; } = new HappyPathEnvironment();

    /// <summary>A merge CONFLICT recovery path: the first integration CONFLICTS; the only way to ship is to spawn a resolver and VERIFY it — so a competent brain converges plan→spawn→merge(conflict)→resolve→stop.</summary>
    public static ISupervisorTrajectoryEnvironment ConflictThenResolve { get; } = new ConflictThenResolveEnvironment();

    /// <summary>An agent FAILURE recovery path: the first spawn returns one Succeeded + one Failed agent; the only way to ship is to ACTIVELY RECOVER the failed subtask (the retry verb or a fresh re-dispatch) — so a competent brain converges plan→spawn→retry→merge→stop.</summary>
    public static ISupervisorTrajectoryEnvironment FailureThenRetry { get; } = new FailureThenRetryEnvironment();

    /// <summary>A PERSISTENT-CONFLICT recovery path: the merge conflicts AND the FIRST resolve comes back UNVERIFIED (the reconciliation didn't pass) — the brain must NOT accept it; only a SECOND, verified resolve ships. A brain that stops on the first unverified resolution ships nothing — the multi-turn safety property the single-decision unverified-resolution scenario can't measure.</summary>
    public static ISupervisorTrajectoryEnvironment ConflictThenUnverifiedThenVerified { get; } = new ConflictThenUnverifiedThenVerifiedEnvironment();

    /// <summary>A MULTI-FAILURE recovery path: the first spawn returns BOTH subtasks Failed — the brain must retry EACH (two retries) before integration is clean; a merge before both are recovered is INCOMPLETE and ships nothing.</summary>
    public static ISupervisorTrajectoryEnvironment MultiFailureThenRetry { get; } = new MultiFailureThenRetryEnvironment();

    private sealed class HappyPathEnvironment : ISupervisorTrajectoryEnvironment
    {
        public SupervisorPriorDecision Fold(SupervisorDecision d, long seq, IReadOnlyList<SupervisorPriorDecision> priors) => d.Kind switch
        {
            var k when k == SupervisorDecisionKinds.Plan => TrajectoryOutcomes.Plan(d, seq),
            var k when k == SupervisorDecisionKinds.Spawn => TrajectoryOutcomes.AllSucceeded(d, seq),
            var k when k == SupervisorDecisionKinds.Retry => TrajectoryOutcomes.RetrySucceeded(d, seq),
            var k when k == SupervisorDecisionKinds.Merge => TrajectoryOutcomes.CleanMerge(d, seq),
            var k when k == SupervisorDecisionKinds.Resolve => TrajectoryOutcomes.VerifiedResolve(d, seq),
            var k when k == SupervisorDecisionKinds.AskHuman => TrajectoryOutcomes.AnsweredAsk(d, seq),
            _ => TrajectoryOutcomes.Handled(d, seq),
        };
    }

    private sealed class ConflictThenResolveEnvironment : ISupervisorTrajectoryEnvironment
    {
        /// <summary>
        /// The arc's honest minimum is short (plan→spawn→merge→resolve→stop), but its honest MAXIMUM is not: a brain
        /// that walks a dependency chain SERIALLY spends a turn per planned unit before it can attempt the
        /// integration, and the conflict then costs a resolve and a re-merge on top. Run 33931943478's conflict lane
        /// did exactly that, earned a CLEAN merge on turn 8, and had no turn left to say stop — scored "never
        /// stopped", which measured the budget rather than the brain, on an arc that had passed the run before. Ten
        /// leaves it the same slack the persistent-conflict sibling gets for its own longest path.
        /// </summary>
        public int MaxTurns => 10;

        public SupervisorPriorDecision Fold(SupervisorDecision d, long seq, IReadOnlyList<SupervisorPriorDecision> priors) => d.Kind switch
        {
            var k when k == SupervisorDecisionKinds.Plan => TrajectoryOutcomes.Plan(d, seq),
            var k when k == SupervisorDecisionKinds.Spawn => TrajectoryOutcomes.AllSucceeded(d, seq),
            var k when k == SupervisorDecisionKinds.Retry => TrajectoryOutcomes.RetrySucceeded(d, seq),
            // The first integration CONFLICTS; a re-merge becomes CLEAN only once a VERIFIED resolve exists — so the brain
            // must resolve+verify to ship, and the scorer's ledger ship-check (ReadFinalIntegratedBranch) enforces it.
            var k when k == SupervisorDecisionKinds.Merge => TrajectoryOutcomes.HasVerifiedResolve(priors) ? TrajectoryOutcomes.CleanMerge(d, seq) : TrajectoryOutcomes.ConflictedMerge(d, seq),
            var k when k == SupervisorDecisionKinds.Resolve => TrajectoryOutcomes.VerifiedResolve(d, seq),
            var k when k == SupervisorDecisionKinds.AskHuman => TrajectoryOutcomes.AnsweredAsk(d, seq),
            _ => TrajectoryOutcomes.Handled(d, seq),
        };
    }

    private sealed class FailureThenRetryEnvironment : ISupervisorTrajectoryEnvironment
    {
        public SupervisorPriorDecision Fold(SupervisorDecision d, long seq, IReadOnlyList<SupervisorPriorDecision> priors) => d.Kind switch
        {
            var k when k == SupervisorDecisionKinds.Plan => TrajectoryOutcomes.Plan(d, seq),
            // The FIRST spawn fails one subtask; any ACTIVE RE-DISPATCH recovers it — the retry verb OR a fresh
            // re-spawn. An earlier draft made EVERY spawn fail forever, which is not production-faithful (a re-spawn
            // is a legitimate fresh attempt that can succeed) — live run 33723910434 showed a real model looping
            // spawn-recovery into a fabricated fail-loop until the turn cap, exactly as the multi-failure sibling
            // did before its own guard. The BAR is unchanged: the failure must be actively recovered, and a premature
            // merge stays INCOMPLETE.
            var k when k == SupervisorDecisionKinds.Spawn => TrajectoryOutcomes.CountSpawns(priors) == 0 ? TrajectoryOutcomes.OneFailed(d, seq) : TrajectoryOutcomes.AllSucceeded(d, seq),
            var k when k == SupervisorDecisionKinds.Retry => TrajectoryOutcomes.RetrySucceeded(d, seq),
            // Integration is CLEAN only once the failure has been recovered; a premature merge is INCOMPLETE (no
            // branch), so the ledger ship-check fails until the brain retries or re-dispatches THAT unit — a
            // re-spawn of the units that already succeeded recovers nothing and must not ship.
            var k when k == SupervisorDecisionKinds.Merge => TrajectoryOutcomes.HasRetry(priors) || TrajectoryOutcomes.HasRecoveredEveryFailedUnit(priors) ? TrajectoryOutcomes.CleanMerge(d, seq) : TrajectoryOutcomes.IncompleteMerge(d, seq),
            var k when k == SupervisorDecisionKinds.Resolve => TrajectoryOutcomes.VerifiedResolve(d, seq),
            var k when k == SupervisorDecisionKinds.AskHuman => TrajectoryOutcomes.AnsweredAsk(d, seq),
            _ => TrajectoryOutcomes.Handled(d, seq),
        };
    }

    private sealed class ConflictThenUnverifiedThenVerifiedEnvironment : ISupervisorTrajectoryEnvironment
    {
        /// <summary>This arc can ONLY ship via a second reconciliation, so it must grant a budget for two — otherwise the prompt forbids the very move the score demands.</summary>
        public int MaxResolveAttempts => 2;

        /// <summary>
        /// The one arc that does not fit the default. Its honest minimum is SEVEN turns —
        /// plan→spawn→merge(conflict)→resolve(unverified)→resolve(verified)→merge(clean)→stop — because the second
        /// reconciliation this environment demands costs two extra turns nobody else pays. Under the default cap
        /// both blessed attempts of run 33754366815 earned a CLEAN merge and then ran out of turns before they could
        /// say stop: scored as "never stopped", which measured the budget rather than the brain. Ten leaves the same
        /// slack for a replan or an ask that the default leaves the shorter arcs.
        /// </summary>
        public int MaxTurns => 10;

        public SupervisorPriorDecision Fold(SupervisorDecision d, long seq, IReadOnlyList<SupervisorPriorDecision> priors) => d.Kind switch
        {
            var k when k == SupervisorDecisionKinds.Plan => TrajectoryOutcomes.Plan(d, seq),
            var k when k == SupervisorDecisionKinds.Spawn => TrajectoryOutcomes.AllSucceeded(d, seq),
            var k when k == SupervisorDecisionKinds.Retry => TrajectoryOutcomes.RetrySucceeded(d, seq),
            var k when k == SupervisorDecisionKinds.Merge => TrajectoryOutcomes.HasVerifiedResolve(priors) ? TrajectoryOutcomes.CleanMerge(d, seq) : TrajectoryOutcomes.ConflictedMerge(d, seq),
            // The FIRST resolve fails verification (its reconciliation didn't pass the build/tests); a competent brain must
            // NOT accept it and must resolve AGAIN — the SECOND resolve is verified. So the only way to ship is to persist
            // past the unverified one; a brain that stops on the first unverified resolution ships nothing.
            var k when k == SupervisorDecisionKinds.Resolve => priors.Any(p => p.DecisionKind == SupervisorDecisionKinds.Resolve) ? TrajectoryOutcomes.VerifiedResolve(d, seq) : TrajectoryOutcomes.UnverifiedResolve(d, seq),
            var k when k == SupervisorDecisionKinds.AskHuman => TrajectoryOutcomes.AnsweredAsk(d, seq),
            _ => TrajectoryOutcomes.Handled(d, seq),
        };
    }

    private sealed class MultiFailureThenRetryEnvironment : ISupervisorTrajectoryEnvironment
    {
        public SupervisorPriorDecision Fold(SupervisorDecision d, long seq, IReadOnlyList<SupervisorPriorDecision> priors) => d.Kind switch
        {
            var k when k == SupervisorDecisionKinds.Plan => TrajectoryOutcomes.Plan(d, seq),
            // BOTH subtasks fail on the FIRST spawn; any ACTIVE RE-DISPATCH recovers them — the retry verb OR a fresh
            // re-spawn of the failed units. An earlier draft made EVERY spawn fail forever, which is not production-
            // faithful (a re-spawn is a legitimate fresh attempt that can succeed) — the 2026-07-10 live run showed a
            // real model looping spawn-recovery into a fabricated fail-loop until the turn cap (M0). The BAR is
            // unchanged: both units must be actively recovered, and a premature merge stays INCOMPLETE.
            var k when k == SupervisorDecisionKinds.Spawn => TrajectoryOutcomes.CountSpawns(priors) == 0 ? TrajectoryOutcomes.BothFailed(d, seq) : TrajectoryOutcomes.AllSucceeded(d, seq),
            var k when k == SupervisorDecisionKinds.Retry => TrajectoryOutcomes.RetrySucceeded(d, seq),
            // A re-dispatch counts only when it NAMES the failed units — a second spawn of the units that already
            // succeeded is a tally, not a recovery, and used to be enough to ship here.
            var k when k == SupervisorDecisionKinds.Merge => TrajectoryOutcomes.CountRetries(priors) >= 2 || TrajectoryOutcomes.HasRecoveredEveryFailedUnit(priors) ? TrajectoryOutcomes.CleanMerge(d, seq) : TrajectoryOutcomes.IncompleteMerge(d, seq),
            var k when k == SupervisorDecisionKinds.Resolve => TrajectoryOutcomes.VerifiedResolve(d, seq),
            var k when k == SupervisorDecisionKinds.AskHuman => TrajectoryOutcomes.AnsweredAsk(d, seq),
            _ => TrajectoryOutcomes.Handled(d, seq),
        };
    }
}

/// <summary>The durable-shape outcome builders the environments fold — the SAME <c>SupervisorOutcome</c> shapes the executor writes, so the decider's rendered context is faithful to production.</summary>
internal static class TrajectoryOutcomes
{
    /// <summary>
    /// Echo the model's OWN plan, the way production does. It used to hardcode <c>planned = ["s1","s2"]</c>, so every
    /// turn after the model planned showed an outcome naming two ids that appear nowhere in the payload the model had
    /// just authored — a prompt contradicting itself on the run's most basic fact.
    /// </summary>
    /// <summary>The trajectory's authorized plan ref — one stable id across turns, as a real run's plan row is.</summary>
    private static readonly Guid FixtureWorkPlanId = Guid.Parse("2b7a1f43-59d8-4c62-8e10-7a3f5c9d0e18");

    public static SupervisorPriorDecision Plan(SupervisorDecision d, long seq)
    {
        var subtasks = SupervisorOutcome.ReadPlanSubtasks(d.PayloadJson);

        // workPlanId/workPlanVersion because production's plan executor records them and the spawn executor stakes
        // no obligation without a plan ref — omitting them describes a pre-protocol run with no contract at all.
        return Prior(d, seq, JsonSerializer.Serialize(new { planned = subtasks, count = subtasks.Count, workPlanId = FixtureWorkPlanId, workPlanVersion = 1 }, AgentJson.Options));
    }

    /// <summary>
    /// A retry re-runs exactly ONE subtask — production stages K=1 for it. Folding the two-agent spawn shape here made
    /// the prompt assert that a second subtask had succeeded on a branch, while the plan recitation in the SAME prompt
    /// listed that subtask as unfinished. A model that believed the results block (which is tagged "act on THESE
    /// results") merged, got an incomplete integration, and was scored down for it.
    /// </summary>
    public static SupervisorPriorDecision RetrySucceeded(SupervisorDecision d, long seq)
    {
        var id = Guid.NewGuid();
        var subtaskId = RetriedSubtaskId(d.PayloadJson);
        var staged = JsonSerializer.Serialize(new { agentRunIds = new[] { id }, agentCount = 1 }, AgentJson.Options);
        var result = Graded(id, "Succeeded", summary: $"retried {subtaskId}; unit tests green", branch: $"agent/{subtaskId}");

        return Prior(d, seq, SupervisorOutcome.FoldAgentResults(staged, new[] { result }));
    }

    /// <summary>
    /// The subtask ids the model's own spawn named, so a fan-out fold answers the units the model actually
    /// dispatched. Hardcoding <c>s1</c>/<c>s2</c> here was survivable only while the plan echo was hardcoded to
    /// match; once the plan echoes the model's real ids, results referencing units it never planned leave its own
    /// units looking permanently unfinished — and the observed answer to that was to spawn again, forever.
    /// Falls back to the historic pair for a payload that names nothing, so an ill-formed spawn still folds.
    /// </summary>
    /// <summary>
    /// One folded agent result carrying the server's VERDICT for its unit, as production folds it. These tapes stake
    /// an acceptance obligation per unit, so a result that never answers one describes a run whose every grading
    /// failed — and it left every completion dimension at Unknown forever, turning the stopped-now recital into an
    /// obligation no action could discharge. The evidence id rides along because admission caps an unevidenced PASS
    /// on a required obligation at InfraUnknown, so a pass asserted without it would silently not count.
    /// </summary>
    private static SupervisorAgentResult Graded(Guid id, string status, string? summary = null, string? error = null, string? branch = null, bool? accepted = null)
    {
        // The agent's STATUS and the server's VERDICT are different facts, and the gap between them is a state the
        // supervisor has to reason about: an agent can finish cleanly and still fail its checks. Default them to
        // agreeing, and let a caller that means to separate them say so.
        var passed = accepted ?? status == "Succeeded";

        return new()
        {
            AgentRunId = id, Status = status, Summary = summary, Error = error, ProducedBranch = branch,
            AcceptancePassed = passed,
            AcceptanceDetail = passed ? "tests-passed" : "tests-failed-exit-1",
            AcceptanceEvidenceId = passed ? Guid.NewGuid() : null,
            // The delivery attestation a real pushed result carries (see the golden fixture's identical note): the
            // recital can only steer honestly if a finished unit's delivery can actually settle.
            PushedCommitSha = branch is null ? null : $"cafe{Math.Abs(branch.GetHashCode()):x8}",
            BaseSha = branch is null ? null : "base0000feed",
            PublishEvidenceId = branch is null ? null : Guid.NewGuid(),
        };
    }

    /// <summary>
    /// The subtask ids a spawn NAMED — never invented. The old fallback to a hardcoded <c>["s1","s2"]</c> made the
    /// harness DIVERGE from production on the one input that matters: production stages ZERO agents for a spawn that
    /// names nothing (<c>StageAgentsAndParkAsync</c> → <c>note: "no subtasks to spawn"</c>), while the harness handed
    /// back two invented units that then SUCCEEDED.
    ///
    /// <para>That is the documented <c>plan→spawn×7</c> loop, mechanised: the model is shown success for units it
    /// never planned, its OWN units stay permanently unfinished, and spawning again is the rational move. The eval
    /// was rewarding a malformed spawn and then scoring the model for not converging.</para>
    /// </summary>
    private static IReadOnlyList<string> SpawnedSubtaskIds(SupervisorDecision d) => SupervisorOutcome.ReadSpawnSubtaskIds(d.PayloadJson);

    /// <summary>Production's outcome for a spawn that staged nothing — byte-mirrored so the harness cannot reward what production refuses.</summary>
    /// <summary>
    /// What PRODUCTION does with a spawn that names no unit — serialized from the executor's OWN builder, never a
    /// hand-copy. This lane simulates outcomes instead of driving <c>RealSupervisorActionExecutor</c>, so anything it
    /// mirrors by hand silently stops matching the moment production moves. It did: the executor learned to REFUSE an
    /// unnamed spawn (so the decider gets a correction it can act on), while this kept emitting the older
    /// accepted-with-a-note shape — and two runs' worth of "did the refusal land?" comparison could not have shown a
    /// difference either way, because the refusal is unreachable from here by construction.
    ///
    /// <para>The clamp's all-deferred case does not arise in this harness: nothing here narrows a payload, so an empty
    /// spawn is always the model's own.</para>
    /// </summary>
    private static SupervisorPriorDecision RefusedSpawn(SupervisorDecision d, long seq) =>
        Prior(d, seq, JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildRejectedSpawnOutcome(), AgentJson.Options));

    public static SupervisorPriorDecision AllSucceeded(SupervisorDecision d, long seq)
    {
        var subtaskIds = SpawnedSubtaskIds(d);

        if (subtaskIds.Count == 0) return RefusedSpawn(d, seq);
        var ids = subtaskIds.Select(_ => Guid.NewGuid()).ToArray();
        var staged = JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options);
        var results = ids.Select((id, i) => Graded(id, "Succeeded", summary: $"implemented {subtaskIds[i]}; unit tests green", branch: $"agent/{subtaskIds[i]}")).ToArray();
        return Prior(d, seq, SupervisorOutcome.FoldAgentResults(staged, results));
    }

    /// <summary>Every unit succeeds except the LAST, which the brain must retry — named by the model's own ids so its plan and its results agree.</summary>
    public static SupervisorPriorDecision OneFailed(SupervisorDecision d, long seq)
    {
        var subtaskIds = SpawnedSubtaskIds(d);

        if (subtaskIds.Count == 0) return RefusedSpawn(d, seq);
        var ids = subtaskIds.Select(_ => Guid.NewGuid()).ToArray();
        var staged = JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options);
        var last = subtaskIds.Count - 1;
        var results = ids.Select((id, i) => i == last
            ? Graded(id, "Failed", error: $"build failed: missing symbol referenced by {subtaskIds[i]}")
            : Graded(id, "Succeeded", summary: $"implemented {subtaskIds[i]}; unit tests green", branch: $"agent/{subtaskIds[i]}")).ToArray();

        return Prior(d, seq, SupervisorOutcome.FoldAgentResults(staged, results));
    }

    public static SupervisorPriorDecision VerifiedResolve(SupervisorDecision d, long seq)
    {
        var id = Guid.NewGuid();
        var staged = JsonSerializer.Serialize(new { agentRunIds = new[] { id }, agentCount = 1 }, AgentJson.Options);
        var resolver = Graded(id, "Succeeded", summary: $"reconciled the conflict; build and the full test suite pass {SupervisorResolverRecipe.TestsPassedMarker}", branch: "resolve/head");
        return Prior(d, seq, SupervisorOutcome.FoldAgentResults(staged, new[] { resolver }));
    }

    public static SupervisorPriorDecision CleanMerge(SupervisorDecision d, long seq) =>
        Prior(d, seq, JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch = "codespace/integration/head" } }, AgentJson.Options));

    public static SupervisorPriorDecision ConflictedMerge(SupervisorDecision d, long seq) =>
        Prior(d, seq, JsonSerializer.Serialize(new { integration = new { status = "Conflicted", outcomes = new[] { new { conflictedFiles = new[] { "src/Signup.cs" }, fallbackBranch = "agent/s1" } } } }, AgentJson.Options));

    public static SupervisorPriorDecision IncompleteMerge(SupervisorDecision d, long seq) =>
        Prior(d, seq, JsonSerializer.Serialize(new { integration = new { status = "Incomplete", reason = "a subtask failed; retry it before integrating" } }, AgentJson.Options));

    /// <summary>An ask_human outcome with a REALISTIC affirmative answer folded exactly as production's rehydrate fold does (question + token + answer via <see cref="SupervisorOutcome.FoldAnswer"/>), so a cautious brain that asks once reads a real answer and converges instead of looping on an empty non-answer.</summary>
    public static SupervisorPriorDecision AnsweredAsk(SupervisorDecision d, long seq) =>
        Prior(d, seq, SupervisorOutcome.FoldAnswer($"Proceed with the plan to ship the goal? (turn {seq})", $"sim-ask-{seq}", "Yes — proceed and ship the work."));

    /// <summary>A generic "handled" outcome for an unrecognized verb so the loop simply continues (the scorer does not penalize a detour).</summary>
    public static SupervisorPriorDecision Handled(SupervisorDecision d, long seq) => Prior(d, seq, "{}");

    public static SupervisorPriorDecision BothFailed(SupervisorDecision d, long seq)
    {
        var subtaskIds = SpawnedSubtaskIds(d);

        if (subtaskIds.Count == 0) return RefusedSpawn(d, seq);
        var ids = subtaskIds.Select(_ => Guid.NewGuid()).ToArray();
        var staged = JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options);
        var results = ids.Select((id, i) => Graded(id, "Failed", error: $"{subtaskIds[i]} failed: build error")).ToArray();
        return Prior(d, seq, SupervisorOutcome.FoldAgentResults(staged, results));
    }

    /// <summary>A resolve whose reconciliation did NOT pass (Succeeded RUN but NO <see cref="SupervisorResolverRecipe.TestsPassedMarker"/>) → <see cref="SupervisorOutcome.ReadResolutionVerdict"/> reads Unverified, so it ships nothing and the brain must resolve again.</summary>
    public static SupervisorPriorDecision UnverifiedResolve(SupervisorDecision d, long seq)
    {
        var id = Guid.NewGuid();
        var staged = JsonSerializer.Serialize(new { agentRunIds = new[] { id }, agentCount = 1 }, AgentJson.Options);
        var resolver = Graded(id, "Failed", summary: "attempted to reconcile the conflict, but the build still fails and the tests do not pass", branch: "resolve/attempt") with { Status = "Succeeded" };   // the AGENT finished; its checks did not pass — the verdict is the server's, not the agent's
        return Prior(d, seq, SupervisorOutcome.FoldAgentResults(staged, new[] { resolver }));
    }

    public static bool HasVerifiedResolve(IReadOnlyList<SupervisorPriorDecision> priors) =>
        priors.Any(p => p.DecisionKind == SupervisorDecisionKinds.Resolve && SupervisorOutcome.ReadResolutionVerdict(p.OutcomeJson) == SupervisorResolutionVerdict.Verified);

    public static bool HasRetry(IReadOnlyList<SupervisorPriorDecision> priors) =>
        priors.Any(p => p.DecisionKind == SupervisorDecisionKinds.Retry);

    public static int CountRetries(IReadOnlyList<SupervisorPriorDecision> priors) =>
        priors.Count(p => p.DecisionKind == SupervisorDecisionKinds.Retry);

    /// <summary>
    /// Spawns that actually DISPATCHED work — the failure envs treat the SECOND-and-later such spawn as an active
    /// recovery re-dispatch (production-faithful: a fresh attempt can succeed).
    ///
    /// <para>Counting every prior tagged <c>spawn</c> instead let a REFUSED one spend the arc's failure slot: run
    /// 33931943478's live model opened with a schema-valid <c>spawn</c> carrying no spawn object, production staged
    /// ZERO agents for it, and the injection then skipped the real spawn that followed — so nothing on the tape ever
    /// failed and the multi-failure arc could not be completed by any move. A spawn that dispatched no agent is not
    /// an attempt at the work, which is exactly what production's refusal says.</para>
    /// </summary>
    public static int CountSpawns(IReadOnlyList<SupervisorPriorDecision> priors) =>
        priors.Count(p => p.DecisionKind == SupervisorDecisionKinds.Spawn && SupervisorOutcome.ReadSpawnSubtaskIds(p.PayloadJson).Count > 0);

    /// <summary>
    /// Whether NO unit a staging decision left NOT succeeded is still outstanding. Counting re-dispatches instead (a
    /// bare "a second spawn happened") would let a brain re-spawn the unit that ALREADY succeeded and ship on a
    /// failure nobody touched — the bar has to read the TARGET, not the tally, which is what the owed set does: a
    /// unit only leaves it when a later decision NAMES it and grades it succeeded. Walks the tape in order because a
    /// unit can be owed, recovered, and owed again.
    ///
    /// <para>An extra <c>recovered</c> flag, flipped only by a re-dispatch overlapping something owed, used to gate
    /// this as well — so a tape on which nothing was EVER owed read as unrecovered, VACUOUSLY. That is the second
    /// half of run 33931943478's multi-failure turn-cap loop: once a refused spawn had spent the failure slot
    /// (see <see cref="CountSpawns"/>) every merge came back Incomplete and no action could open the gate.</para>
    /// </summary>
    public static bool HasRecoveredEveryFailedUnit(IReadOnlyList<SupervisorPriorDecision> priors)
    {
        var owed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var d in priors.Where(p => p.DecisionKind == SupervisorDecisionKinds.Spawn || p.DecisionKind == SupervisorDecisionKinds.Retry))
        {
            var attempted = AttemptedSubtaskIds(d);

            owed.ExceptWith(attempted);
            owed.UnionWith(FailedSubtaskIds(d, attempted));
        }

        return owed.Count == 0;
    }

    /// <summary>The units a staging decision dispatched, read off the payload the SAME way the folds read it — so what "was attempted" can never disagree with what was graded.</summary>
    private static IReadOnlyList<string> AttemptedSubtaskIds(SupervisorPriorDecision d) =>
        d.DecisionKind == SupervisorDecisionKinds.Spawn
            ? SupervisorOutcome.ReadSpawnSubtaskIds(d.PayloadJson)
            : new[] { RetriedSubtaskId(d.PayloadJson) };

    /// <summary>The units this decision left NOT succeeded. Its folded results are positional with the ids it attempted, exactly as every fold above builds them.</summary>
    private static IEnumerable<string> FailedSubtaskIds(SupervisorPriorDecision d, IReadOnlyList<string> attempted)
    {
        var results = SupervisorOutcome.ReadAgentResults(d.OutcomeJson);

        return attempted.Where((_, i) => i < results.Count && results[i].Status != "Succeeded");
    }

    /// <summary>The unit a retry re-attempts: the model's named subtaskId, else the historic default — shared with <see cref="RetrySucceeded"/> so the unit the fold GRADES is always the unit the ledger reader credits.</summary>
    private static string RetriedSubtaskId(string? retryPayloadJson) => SupervisorOutcome.ReadRetrySubtaskId(retryPayloadJson) ?? "s1";

    private static SupervisorPriorDecision Prior(SupervisorDecision d, long seq, string outcomeJson) =>
        new() { Id = Guid.Empty, Sequence = seq, DecisionKind = d.Kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = d.PayloadJson, OutcomeJson = outcomeJson };
}

/// <summary>The outcome of a driven trajectory — the ordered decision kinds, whether it reached a terminal stop or ran into the turn cap, and the folded ledger the scorer reads for the real shippable result.</summary>
public sealed record SupervisorTrajectoryResult
{
    public required IReadOnlyList<string> Kinds { get; init; }
    public required bool ReachedStop { get; init; }
    public required bool HitTurnCap { get; init; }
    public required IReadOnlyList<SupervisorPriorDecision> Ledger { get; init; }
}

/// <summary>
/// The PURE trajectory scorer — the property a single decision can't measure: the run DRIVES TO COMPLETION (reaches a
/// stop within the cap/budget, never loops out) and STOPS AFTER PRODUCING A REAL SHIPPABLE RESULT (a clean integration
/// OR a verified resolution exists in the ledger at the stop — read by the SAME production reader the engine uses, so a
/// CONFLICTED merge or an UNVERIFIED resolve does NOT count as shipping), having PLANNED and DONE THE WORK, without
/// runaway re-planning or work-churn. Deterministic; no model, no I/O. Works across the happy / conflict / failure
/// environments uniformly — the recovery paths can only ship by resolving / retrying, which the ledger ship-check enforces.
/// </summary>
public static class SupervisorTrajectoryScore
{
    /// <summary>The replan ceiling — plan decisions after the first are rework; more than this is a non-converging brain.</summary>
    public const int MaxReplans = 2;

    /// <summary>
    /// The PER-UNIT staging ceiling — how many times one planned unit may be handed to an agent (spawn / retry /
    /// resolve) before the brain is re-attempting rather than converging. Two: the original attempt plus ONE
    /// recovery, which is exactly what the failure and conflict arcs need.
    ///
    /// <para>This replaced a FLAT ceiling of four stagings per run, which was calibrated for a brain that fans its
    /// plan out in ONE wide spawn. Run 33814929951's persistent-conflict lane failed on it while doing nothing
    /// wrong: the model staged its dependency chain SERIALLY — a distinct planned unit per turn, each waiting on the
    /// one before — and five honest first attempts tripped a cap meant to catch re-spawning. Staging DISTINCT
    /// planned units one at a time is a legitimate strategy, not churn; churn is re-staging the SAME unit without
    /// progress, which is what this counts now.</para>
    /// </summary>
    public const int MaxAttemptsPerUnit = 2;

    /// <summary>The bucket a staging verb that NAMES no unit falls in. Production stages nothing for such a decision, so repeating it makes no progress by construction — grouping them together keeps a loop of unnamed spawns inside the same ceiling instead of escaping it for lack of an id.</summary>
    private const string UnnamedUnit = "(a staging verb that named no unit)";

    /// <summary>
    /// What each agent-staging verb ACTUALLY DID, appended to a non-terminating verdict. The verb sequence alone says
    /// the brain looped; it cannot say what it looped ON, and this lane emits none of the supervisor's own log stream
    /// — so a run that reads "plan→spawn→spawn→spawn→spawn→spawn→spawn→spawn" gives the next reader no move. That is
    /// why the eval sat red on main for days across a dozen unrelated commits: the verdict named a symptom nobody
    /// could act on.
    ///
    /// <para>Reads the SAME three outcome readers production uses, so a disposition here can never disagree with what
    /// the decider was told: a REJECTED decision (malformed / unknown id), a WITHHELD one (dependency staging refused
    /// to hand off silently), or an accepted one and how many agents it staged. A loop of rejections and a loop of
    /// real fan-outs are completely different bugs and used to render identically.</para>
    /// </summary>
    private static string DescribeStagingVerbs(IReadOnlyList<SupervisorPriorDecision> ledger)
    {
        var lines = ledger
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .Select((d, i) => $"  #{i + 1} {d.DecisionKind}: {DescribeOne(d)}")
            .ToList();

        return lines.Count == 0 ? "" : "\nWhat each staging verb did:\n" + string.Join("\n", lines);

        static string DescribeOne(SupervisorPriorDecision d)
        {
            // A decision with NO outcome never completed (the deadline/cancellation arm can leave one trailing). Every
            // reader below returns its empty default for null, which would render it as "accepted, but no agent ran" —
            // a claim the data does not support, and the exact failure this whole breakdown exists to stop making.
            // A decision with NO outcome never completed (the deadline/cancellation arm can leave one trailing). Every
            // reader below returns its empty default for null, which would render it as "accepted, but no agent ran" —
            // a claim the data does not support, and the exact failure this whole breakdown exists to stop making.
            if (string.IsNullOrWhiteSpace(d.OutcomeJson)) return "no outcome recorded — the decision did not complete";

            if (SupervisorOutcome.ReadRejectionReason(d.OutcomeJson) is { } rejected) return $"REJECTED — {rejected}";

            if (SupervisorOutcome.ReadBlockedSubtasks(d.OutcomeJson) is { Count: > 0 } blocked)
                return $"WITHHELD {blocked.Count} subtask(s) — {string.Join("; ", blocked.Select(b => $"{b.SubtaskId}: {b.Reason}"))}";

            var staged = SupervisorOutcome.ReadStagedAgentCount(d.OutcomeJson);

            return staged == 0 ? "staged NOTHING (accepted, but no agent ran)" : $"staged {staged} agent(s)";
        }
    }

    public static (bool Ok, string Note) Score(SupervisorTrajectoryResult t)
    {
        var trail = string.Join("→", t.Kinds);

        if (!t.ReachedStop)
            return (false, (t.HitTurnCap
                ? $"never stopped — hit the turn cap (the brain loops / doesn't drive to completion). Trajectory: {trail}"
                : $"did not reach a terminal stop within the time budget (deadline/cancellation). Trajectory: {trail}")
                + DescribeStagingVerbs(t.Ledger));

        // SHIP = a REAL reviewable head at the stop (a clean integration OR a verified resolution), read off the ledger by
        // the production reader — so a conflicted merge / unverified resolve / un-integrated fresh work does NOT count.
        if (SupervisorOutcome.ReadFinalIntegratedBranch(t.Ledger) is null)
            return (false, $"stopped WITHOUT shipping (no clean integration or verified resolution in the ledger) — quit early or left work unintegrated. Trajectory: {trail}");

        // Defense in depth on the ORDER: the brain must PLAN and DO THE WORK *before* it first attempts to ship — not
        // merge out of nothing, not plan after a ship. (The ledger ship-check above covers trailing un-integrated work;
        // this covers the ordering.) firstShip is guaranteed >= 0 because a non-null shippable head implies a merge/resolve.
        var firstShip = FirstIndex(t.Kinds, k => k == SupervisorDecisionKinds.Merge || k == SupervisorDecisionKinds.Resolve);

        var planIndex = FirstIndex(t.Kinds, k => k == SupervisorDecisionKinds.Plan);
        if (planIndex < 0 || planIndex > firstShip)
            return (false, $"shipped without PLANNING first — Trajectory: {trail}");

        var workIndex = FirstIndex(t.Kinds, SupervisorDecisionKinds.StagesAgents);
        if (workIndex < 0 || workIndex > firstShip)
            return (false, $"shipped without DOING THE WORK (no spawn/retry/resolve before the merge/resolve) — Trajectory: {trail}");

        var planCount = t.Kinds.Count(k => k == SupervisorDecisionKinds.Plan);
        if (planCount - 1 > MaxReplans)
            return (false, $"re-planned {planCount - 1} times (> {MaxReplans}) — not converging. Trajectory: {trail}");

        var restaged = RestagedUnits(t.Ledger);
        if (restaged.Count > 0)
            return (false, $"re-staged the SAME unit(s) more than {MaxAttemptsPerUnit} times ({string.Join(", ", restaged)}) — churning, not converging. Trajectory: {trail}");

        return (true, $"drove to completion: {trail}");
    }

    /// <summary>
    /// Every unit staged more times than <see cref="MaxAttemptsPerUnit"/> allows, rendered "id ×N" — the churn
    /// evidence, so the verdict names WHICH unit the brain kept re-attempting instead of only that it staged a lot.
    /// </summary>
    private static IReadOnlyList<string> RestagedUnits(IReadOnlyList<SupervisorPriorDecision> ledger) =>
        ledger
            .Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind))
            .SelectMany(StagedUnitsOf)
            .GroupBy(id => id)
            .Where(g => g.Count() > MaxAttemptsPerUnit)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} ×{g.Count()}")
            .ToList();

    /// <summary>
    /// The unit ids one staging verb handed to agents, read by the SAME production readers the dependency gate joins
    /// on (<c>ReadSpawnSubtaskIds</c> for a fan-out, <c>ReadRetrySubtaskId</c> for the single-unit verbs) — so the
    /// scorer can never disagree with what the engine considers that decision to have attempted.
    /// </summary>
    private static IReadOnlyList<string> StagedUnitsOf(SupervisorPriorDecision d)
    {
        var ids = d.DecisionKind == SupervisorDecisionKinds.Spawn
            ? SupervisorOutcome.ReadSpawnSubtaskIds(d.PayloadJson)
            : SupervisorOutcome.ReadRetrySubtaskId(d.PayloadJson) is { } id ? new[] { id } : Array.Empty<string>();

        return ids.Count == 0 ? new[] { UnnamedUnit } : ids;
    }

    /// <summary>The index of the first kind matching the predicate, or -1.</summary>
    private static int FirstIndex(IReadOnlyList<string> kinds, Func<string, bool> match)
    {
        for (var i = 0; i < kinds.Count; i++)
            if (match(kinds[i])) return i;

        return -1;
    }
}

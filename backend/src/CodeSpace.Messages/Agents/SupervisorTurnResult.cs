namespace CodeSpace.Messages.Agents;

/// <summary>
/// What one supervisor turn resolved to (a data noun, Rule 18.1) — the turn service's instruction to the
/// node. FOUR shapes (the three resume paths, PR-E E4):
/// <list type="bullet">
///   <item>FINISH — a terminal <c>stop</c> / forced-stop decision reached its outcome; the node returns
///         a terminal node result + the run completes via the normal walk.</item>
///   <item>SELF-ADVANCE PARK — a SYNCHRONOUS non-terminal decision (plan / merge) settled in-process; the node
///         suspends on a <c>SupervisorDecision</c> wait under the next-turn IterationKey, self-advancing into
///         the next turn (the E2 path).</item>
///   <item>AGENT-WAIT PARK — an ASYNC decision (spawn / retry) staged K real <c>AgentRun</c> waits; the node
///         suspends on THOSE (no self-advance) and the wait-for-all barrier resumes the supervisor once all K
///         agents complete (<see cref="ParkedAgentWaitCount"/> &gt; 0).</item>
///   <item>HUMAN-WAIT PARK — an ask_human decision posted a question card; the node suspends on a SINGLE
///         <c>Action</c> wait keyed to <see cref="HumanWaitToken"/>, and the human's answer (the existing
///         single-wait resume path) drives the next turn (E4).</item>
/// </list>
/// </summary>
public sealed record SupervisorTurnResult
{
    /// <summary>True when the loop is done (a terminal <c>stop</c>). The node returns success; false → the node parks for the next turn.</summary>
    public required bool IsFinished { get; init; }

    /// <summary>The decision kind this turn settled (audit/output surface).</summary>
    public required string DecisionKind { get; init; }

    /// <summary>The terminal reason on finish (e.g. the stop reason, or "no progress"). Null while parking.</summary>
    public string? TerminalReason { get; init; }

    /// <summary>
    /// The run's final reviewable integrated branch on finish (resolver loop #379, S5) — the head a downstream
    /// <c>git.open_pr</c> / <c>git.open_change_set</c> node targets. Folded from the durable ledger: the most recent
    /// <c>merge</c> that integrated CLEAN, OR — when the conflict was resolved — the VERIFIED resolver's own tested
    /// branch (latest wins). Null while parking + when no clean integration exists (analysis-only / unresolved conflict).
    /// </summary>
    public string? IntegratedBranch { get; init; }

    /// <summary>
    /// The run's final PER-REPO reviewable branches on finish (resolver loop #379, S7-D1/S7-E) — the MULTI-repo
    /// complement of <see cref="IntegratedBranch"/> (single-valued, hence empty for a multi-repo run, which integrates
    /// each repo on its own axis with no single branch). Each carries a repo's id + alias + reconciled head
    /// (<c>sourceBranch</c>) + PR base (<c>targetBranch</c>). EMPTY for a single-repo run (which surfaces
    /// <see cref="IntegratedBranch"/>) and while parking. Defaults to empty and NEVER serializes null, so a consumer can
    /// always treat it as an array. Binds VERBATIM into <c>git.open_change_set</c>'s <c>repositories</c> input (open one
    /// PR per repo) — see <see cref="SupervisorRepositoryBranch"/>.
    /// </summary>
    public IReadOnlyList<SupervisorRepositoryBranch> RepositoryBranches { get; init; } = Array.Empty<SupervisorRepositoryBranch>();

    /// <summary>
    /// The objective acceptance verdict on FINISH (L4 P1) — <c>null</c> when the terminal stop authored no
    /// model acceptance check (the dominant case; the node reports Completed, byte-identical to before),
    /// <c>true</c>/<c>false</c> = the server-run grade's pass/fail. <c>false</c> ⇒ the node reports
    /// <c>AcceptanceFailed</c> and the reviewable branches are withheld (the model's definition of done did not
    /// pass, so there is no verified head to ship). An in-memory node-local read only — a finish never parks, so
    /// this never serializes into a wait payload.
    /// </summary>
    public bool? AcceptancePassed { get; init; }

    /// <summary>
    /// The classified terminal shape on finish (built by <c>SupervisorOutcome.ClassifyStop</c> — the SAME authority the
    /// RESULT card and the journal step read): a server-forced stop (a bound/budget/governance trip) or a model give-up
    /// (a fail-closed no-decision / non-success outcome) is <c>Degraded</c>, so the node reports <c>Stopped</c> instead
    /// of a false <c>Completed</c> — a run that died mid-way must never read as finished. Null while parking (and on a
    /// legacy caller that never classified — which reads as the non-degraded default).
    /// </summary>
    public SupervisorStopClassification? StopClassification { get; init; }

    /// <summary>The folded context for the NEXT turn — carried as the self-advance park wait's payload. Null on finish.</summary>
    public SupervisorTurnContext? NextTurn { get; init; }

    /// <summary>
    /// &gt; 0 when this turn's decision staged that many real <c>AgentRun</c> waits (spawn / retry) — the node
    /// parks on THEM, NOT on a self-advance wait; the wait-for-all barrier drives the next turn once all
    /// complete. 0 for a synchronous self-advance (plan / merge) or a finish.
    /// </summary>
    public int ParkedAgentWaitCount { get; init; }

    /// <summary>The correlation token of the SINGLE <c>Action</c> wait an ask_human turn posted its question card on (E4). Non-null ⇒ the node parks on the human's answer (one answer resumes — NOT the wait-for-all barrier). Null otherwise.</summary>
    public string? HumanWaitToken { get; init; }

    /// <summary>True when this turn parked on staged agent waits (async) rather than a self-advance wait (synchronous). The node reads this to choose its suspend path.</summary>
    public bool ParkedOnAgentWaits => !IsFinished && ParkedAgentWaitCount > 0;

    /// <summary>True when this turn parked on a human answer (ask_human) — the node suspends on a single Action wait keyed to <see cref="HumanWaitToken"/>.</summary>
    public bool ParkedOnHuman => !IsFinished && HumanWaitToken != null;

    public static SupervisorTurnResult Finished(string decisionKind, string? terminalReason, string? integratedBranch = null, IReadOnlyList<SupervisorRepositoryBranch>? repositoryBranches = null, bool? acceptancePassed = null, SupervisorStopClassification? stopClassification = null) =>
        new() { IsFinished = true, DecisionKind = decisionKind, TerminalReason = terminalReason, IntegratedBranch = integratedBranch, RepositoryBranches = repositoryBranches ?? Array.Empty<SupervisorRepositoryBranch>(), AcceptancePassed = acceptancePassed, StopClassification = stopClassification };

    /// <summary>A SYNCHRONOUS non-terminal decision (plan / merge) — the node self-advances on a SupervisorDecision wait carrying the next-turn context.</summary>
    public static SupervisorTurnResult SelfAdvance(string decisionKind, SupervisorTurnContext nextTurn) =>
        new() { IsFinished = false, DecisionKind = decisionKind, NextTurn = nextTurn, ParkedAgentWaitCount = 0 };

    /// <summary>An ASYNC decision (spawn / retry) — the executor staged <paramref name="agentWaitCount"/> AgentRun waits; the node parks on them (the barrier resumes it).</summary>
    public static SupervisorTurnResult ParkOnAgents(string decisionKind, SupervisorTurnContext nextTurn, int agentWaitCount) =>
        new() { IsFinished = false, DecisionKind = decisionKind, NextTurn = nextTurn, ParkedAgentWaitCount = agentWaitCount };

    /// <summary>An ask_human decision — the executor posted a question card on the Action wait <paramref name="humanWaitToken"/>; the node parks on the single human answer (E4).</summary>
    public static SupervisorTurnResult ParkOnHuman(string decisionKind, SupervisorTurnContext nextTurn, string humanWaitToken) =>
        new() { IsFinished = false, DecisionKind = decisionKind, NextTurn = nextTurn, HumanWaitToken = humanWaitToken };
}

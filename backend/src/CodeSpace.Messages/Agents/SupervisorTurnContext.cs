namespace CodeSpace.Messages.Agents;

/// <summary>
/// The folded state of a supervisor run AT THE START OF A TURN — the replay of every prior decision from
/// the durable <c>SupervisorDecisionRecord</c> ledger (a data noun, Rule 18.1: only primitives, never the
/// Core entity). Built by <c>RehydrateFromDecisionLog</c> by reading the ledger in <c>Sequence</c> order:
/// a TERMINAL decision is replayed into <see cref="PriorDecisions"/> (its outcome only — the side effect is
/// NOT re-run), and the one in-flight decision (a non-terminal row, if any) becomes <see cref="InFlight"/>
/// — the resume target the turn re-claims rather than re-emits.
///
/// <para>The decider reads this to choose the next decision deterministically (E2 stub: turn 1 = plan, turn
/// 2 = stop). <see cref="TurnNumber"/> is 0-based and equals <see cref="PriorDecisions"/>.Count +
/// (InFlight is null ? 0 : 0) — i.e. the number of DECIDED turns so far; it drives both the next decision
/// and the per-turn <c>IterationKey</c>. <see cref="Goal"/> is the run-level goal the supervisor pursues.</para>
/// </summary>
public sealed record SupervisorTurnContext
{
    /// <summary>The run-level goal the supervisor is pursuing (carried from node config).</summary>
    public string Goal { get; init; } = "";

    /// <summary>
    /// The supervisor run id (the WorkflowRun id) — the executor links spawned agent runs + their AgentRun waits
    /// to it (E3). Carried so the executor stays a pure-of-engine service (Rule 16). <see cref="Guid.Empty"/> in a
    /// pure-unit context that never spawns.
    /// </summary>
    public Guid SupervisorRunId { get; init; }

    /// <summary>The run's team (tenancy) — spawned agent runs inherit it; never model-supplied. <see cref="Guid.Empty"/> in a pure-unit context.</summary>
    public Guid TeamId { get; init; }

    /// <summary>
    /// The <c>agent.supervisor</c> node id — the prefix of the per-turn-per-spawn AgentRun wait IterationKey
    /// (<c>&lt;nodeId&gt;#turn{N}#{k}</c>, must-fix #1). Empty in a pure-unit context that never spawns.
    /// </summary>
    public string NodeId { get; init; } = "";

    /// <summary>The 0-based number of the turn about to run = how many prior DECIDED (terminal) decisions exist. Drives the next decision + the per-turn IterationKey.</summary>
    public int TurnNumber { get; init; }

    /// <summary>The replayed terminal decisions in <c>Sequence</c> order — outcome only, side effects NOT re-run. The exactly-once replay tape.</summary>
    public IReadOnlyList<SupervisorPriorDecision> PriorDecisions { get; init; } = Array.Empty<SupervisorPriorDecision>();

    /// <summary>The single in-flight (non-terminal) decision, if any — a turn crashed AFTER claiming but BEFORE recording terminal. The resume target the turn re-claims (idempotent), never re-emits. Null on the common path.</summary>
    public SupervisorPriorDecision? InFlight { get; init; }
}

/// <summary>One prior decision replayed from the ledger — its kind + emitted payload + (for a terminal) its recorded outcome. A pure data noun.</summary>
public sealed record SupervisorPriorDecision
{
    public required long Sequence { get; init; }
    public required string DecisionKind { get; init; }
    public required SupervisorDecisionStatus Status { get; init; }
    public required string PayloadJson { get; init; }
    public string? OutcomeJson { get; init; }
    public string? Error { get; init; }
}

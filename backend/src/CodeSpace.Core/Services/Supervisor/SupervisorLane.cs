namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The supervisor-lane feature gate (Rule 8). The bounded durable supervisor lane (PR-E) is built behind this flag so a
/// flag-OFF deployment is byte-identical to today: nothing writes the <c>SupervisorDecisionRecord</c> ledger, and the
/// stale-Pending reaper job is NOT even registered with the scheduler (so no sweep ever runs). The empty table existing
/// is harmless.
///
/// <para>Default-OFF, fail-closed: <see cref="IsEnabled"/> is true ONLY for "1"/"true"/"TRUE" (trimmed) — anything else
/// (null, "", "0", "no", garbage) is OFF. Pinned by a unit test (Rule 8) so a rename is a compile-time-visible decision,
/// not an invisible refactor that silently turns the lane off for an operator who flipped it on via env.</para>
/// </summary>
public static class SupervisorLane
{
    /// <summary>The env var an operator flips to enable the supervisor lane. <c>public const</c> + pinned by a unit test (Rule 8).</summary>
    public const string EnabledEnvVar = "CODESPACE_SUPERVISOR_LANE_ENABLED";

    /// <summary>
    /// The hard cap on decisions one supervisor run may emit (PR-E E2). Load-bearing + fail-closed: the turn
    /// loop counts DECIDED decisions from the durable ledger and, when the count would meet/exceed this,
    /// forces a terminal <c>stop</c> ("budget exhausted") instead of asking the decider — so a runaway loop
    /// always terminates. Counted from the ledger (never an in-memory tally), so it survives a restart/replay
    /// and can't be reset by re-entering the node. An operator's <c>MaxRounds</c> may TIGHTEN it below this,
    /// never raise it past this hard ceiling.
    /// </summary>
    public const int DecisionBudget = 30;

    /// <summary>
    /// The default cap on how many agents one supervisor run may spawn IN TOTAL across the whole run (PR-E E5),
    /// summed from the durable ledger. Load-bearing + fail-closed: at the cap a further spawn FORCE-STOPS the run
    /// with a distinct terminal reason. An operator's <c>MaxTotalSpawns</c> tunes it within
    /// <c>[1, MaxTotalSpawnsCeiling]</c>. Pinned by a unit test (Rule 8).
    /// </summary>
    public const int DefaultMaxTotalSpawns = 50;

    /// <summary>The hard ceiling an operator's <c>MaxTotalSpawns</c> is clamped to — a fat-fingered config can't disable the bound. Pinned by a unit test (Rule 8).</summary>
    public const int MaxTotalSpawnsCeiling = 1_000;

    /// <summary>
    /// The default cap on consecutive decisions that produce NO new SETTLED agent result before the no-progress
    /// guard FORCE-STOPS the run (PR-E E5). BEST-EFFORT (demoted per the design): it stops a decider that loops
    /// without ever advancing the work; a long-running spawn whose agents haven't settled yet does NOT trip it
    /// (its decision is a park, not a fresh decided turn). Counted from the durable ledger. Pinned (Rule 8).
    /// </summary>
    public const int DefaultMaxNoProgressDecisions = 8;

    /// <summary>
    /// The cap on supervisor-spawns-supervisor nesting depth (PR-E E5) — reuses the <c>SubworkflowService.MaxDepth</c>
    /// precedent (a supervisor whose ancestor chain already has this many supervisor runs FORCE-STOPS at turn 0,
    /// before deciding). Guards against a recursive supervisor fan-out exhausting the deployment. Pinned (Rule 8).
    /// </summary>
    public const int MaxSupervisorDepth = 8;

    /// <summary>
    /// The default cap on how many <c>resolve</c> attempts one supervisor run may make against a conflicted
    /// integration (resolver loop #379) — small on purpose: a resolution that fails this many times should fall
    /// back fail-safe to the humans (the K agent branches remain), never an unbounded resolve loop burning agents +
    /// tokens. An operator's <c>MaxResolveAttempts</c> tunes it within <c>[1, MaxResolveAttemptsCeiling]</c>. Pinned (Rule 8).
    /// </summary>
    public const int DefaultMaxResolveAttempts = 1;

    /// <summary>The hard ceiling an operator's <c>MaxResolveAttempts</c> is clamped to — a conflict the model can't reconcile in a few tries is for a human, not an infinite retry. Pinned (Rule 8).</summary>
    public const int MaxResolveAttemptsCeiling = 5;

    /// <summary>Wall-clock cap (seconds) for the OBJECTIVE acceptance grade — re-cloning a resolver's branch and running the operator's acceptance command (L4 A3). A hung check is a non-accept, not a hang. An operator-tunable field is a follow-up. Pinned (Rule 8).</summary>
    public const int AcceptanceGradeTimeoutSeconds = 120;

    /// <summary>Reads the env var through the single gate. Default-OFF: true only for the explicit on-values.</summary>
    public static bool IsEnabled() => IsEnabled(Environment.GetEnvironmentVariable(EnabledEnvVar));

    /// <summary>Pure overload (no env read) so the polarity is unit-testable without env mutation: true ONLY for "1"/"true"/"TRUE" (trimmed); false for null / "" / anything else.</summary>
    public static bool IsEnabled(string? raw)
    {
        var value = raw?.Trim();

        return value is "1" or "true" or "TRUE";
    }
}

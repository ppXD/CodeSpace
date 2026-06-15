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
    /// and can't be reset by re-entering the node.
    /// </summary>
    public const int DecisionBudget = 30;

    /// <summary>Reads the env var through the single gate. Default-OFF: true only for the explicit on-values.</summary>
    public static bool IsEnabled() => IsEnabled(Environment.GetEnvironmentVariable(EnabledEnvVar));

    /// <summary>Pure overload (no env read) so the polarity is unit-testable without env mutation: true ONLY for "1"/"true"/"TRUE" (trimmed); false for null / "" / anything else.</summary>
    public static bool IsEnabled(string? raw)
    {
        var value = raw?.Trim();

        return value is "1" or "true" or "TRUE";
    }
}

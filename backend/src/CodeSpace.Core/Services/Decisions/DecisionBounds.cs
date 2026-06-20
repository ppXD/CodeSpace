namespace CodeSpace.Core.Services.Decisions;

/// <summary>
/// The fail-closed guardrail bounding how many decisions ONE agent run may have pending at once (Decision substrate D5c)
/// — backpressure against a buggy / runaway agent that raises many DISTINCT <c>decision.request</c>s instead of blocking
/// on each. The <c>McpRequestHandler</c> consults it BEFORE claiming a fresh decision row, so a rejected raise never
/// INSERTs a ghost row that would burn the deterministic dedupe key. A re-issue of an ALREADY-pending decision (same key)
/// is exempt: it is not a new raise, it replays the existing answer via the claim's Duplicate path (AC1), so the count is
/// taken EXCLUDING the decision being raised.
///
/// <para>A SOFT cap (count-then-claim, no lock) — under a burst the count read can be stale, so the pending total may
/// overshoot by the concurrent-raise width; that is acceptable backpressure, not a precise quota (mirrors
/// <c>AdmissionController</c>). The cap is env-overridable (Rule 8), pinned by a unit test. The node grain needs no
/// equivalent — its <c>(run, node, iteration)</c> unique index already bounds it to one pending wait per node.</para>
/// </summary>
public static class DecisionBounds
{
    /// <summary>Operator override for the per-run pending-decision cap (Rule 8). Pinned by a unit test — a rename breaks any operator who tuned it.</summary>
    public const string MaxPendingPerRunEnvVar = "CODESPACE_DECISION_MAX_PENDING_PER_RUN";

    internal const int DefaultMaxPendingPerRun = 3;

    // A cap of 1 is the floor (a run may only ever have the one decision it is blocked on); the ceiling stops a
    // fat-fingered env value from effectively disabling the gate while still allowing a deliberate lift.
    internal const int MaxPendingCeiling = 1000;

    /// <summary>The per-run pending-decision cap: the env override (clamped) wins, else the default. Pure + internal so it's unit-pinned (Rule 8).</summary>
    internal static int MaxPendingPerRun => ParseCap(Environment.GetEnvironmentVariable(MaxPendingPerRunEnvVar), DefaultMaxPendingPerRun);

    /// <summary>Parse + clamp a cap env value. Unset / unparseable ⇒ the default; out-of-range ⇒ clamped to [1, ceiling]. Mirrors <c>AdmissionController.ParseCap</c>.</summary>
    internal static int ParseCap(string? raw, int @default) =>
        int.TryParse(raw, out var value) ? Math.Clamp(value, 1, MaxPendingCeiling) : @default;

    /// <summary>The breach message (naming the cap + the env var) when a run already holds the cap's-worth of OTHER pending decisions, else <c>null</c> (admit). <paramref name="otherPendingForRun"/> EXCLUDES the decision being raised, so a re-issue (same key) is never counted against itself. Pure + internal so the boundary is unit-pinned without a DB (Rule 8).</summary>
    internal static string? PendingCapBreach(int otherPendingForRun)
    {
        var cap = MaxPendingPerRun;

        return otherPendingForRun >= cap
            ? $"This run already has {otherPendingForRun} decision(s) awaiting an answer, at its cap of {cap}. Answer one before raising another, or raise {MaxPendingPerRunEnvVar}."
            : null;
    }
}

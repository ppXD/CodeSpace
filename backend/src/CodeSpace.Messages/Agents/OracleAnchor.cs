namespace CodeSpace.Messages.Agents;

/// <summary>
/// What an acceptance grade anchors its ORACLE RESTORE on (a data noun, Rule 18.1): the BASE commit whose bytes the
/// judge is restored from, and the run's own ORACLE INVENTORY — the OPERATOR FLOOR's program files
/// (<c>SupervisorGoalConfig.AcceptanceChecks</c> through <c>AcceptanceOracleProtection.ProgramCandidates</c>) — that
/// says WHICH of a command's program files the run actually owns.
///
/// <para>The two travel as ONE value because either alone is silently useless, and both halves have already been
/// forgotten in production: a base with no inventory restores nothing but an authored path, and an inventory with no
/// base has nothing to restore from. Passing them as separate arguments made "remembered the base, forgot the floor"
/// a compiling, green, protection-disabling mistake; as one record it is unrepresentable.</para>
/// </summary>
/// <param name="BaseSha">The commit the oracle's bytes are restored from — null when the lane has no recorded base (the grade then says <c>graded UNPROTECTED (no base recorded)</c> rather than passing silently).</param>
/// <param name="FloorPrograms">The program files the run's own acceptance floor executes. Empty/null ⇒ the run owns no derived judge, so every program file its checks execute is the SUBJECT under test.</param>
public readonly record struct OracleAnchor(string? BaseSha, IReadOnlyList<string>? FloorPrograms)
{
    /// <summary>No anchor at all — the caller grades whatever bytes the candidate left behind, deliberately and visibly.</summary>
    public static readonly OracleAnchor None = new(null, null);
}

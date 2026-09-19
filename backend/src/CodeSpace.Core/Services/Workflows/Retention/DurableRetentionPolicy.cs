using CodeSpace.Messages.Retention;

namespace CodeSpace.Core.Services.Workflows.Retention;

/// <summary>
/// The retention policy for the durable records that are not artifacts: which classes exist and how long their records
/// are kept. Values are committed here and changed by a pull request — there is no environment override, because a
/// mistyped retention window is unrecoverable data loss and a code review is the control that belongs in front of it.
///
/// <para>A class is listed here together with the cursor that sweeps it, never ahead of one, so this table can always
/// be read as "what is actually reclaimed". Two planes that might be expected are deliberately absent, each refused by
/// a trigger whose own words say why: <c>workflow_run_capture_gap</c> ("a removable gap makes a complete manifest
/// reachable by deleting the evidence", migration 0146) and <c>paired_qualification_result</c> ("paired qualification
/// result is immutable", migration 0219) — and a result's citers are not enumerable in columns at all, since a
/// qualification receipt records its cohort and metrics as JSON.</para>
///
/// <para>An unregistered class is NOT an error a cursor can shrug off: <see cref="For"/> returns null and the decision
/// settles Indeterminate, which keeps the record forever. That is what makes removing a class from this table safe.</para>
/// </summary>
public static class DurableRetentionPolicy
{
    private static readonly TimeSpan Quarantine = TimeSpan.FromHours(24);
    private static readonly TimeSpan Recheck = TimeSpan.FromHours(24);

    /// <summary>
    /// Thirty days after the stream reached its terminal capture state, then a day of quarantine, and a day between
    /// looks at a stream that was kept. Deliberately far longer than any run and longer than any Room a human is still
    /// reading: the bytes this reclaims are an archive nobody opened, and the floor costs storage rather than evidence.
    /// </summary>
    public static readonly DurableRetentionRule LogStream =
        new(DurableRecordClass.LogStream, TimeSpan.FromDays(30), Quarantine, Recheck);

    /// <summary>
    /// Thirty days after the receipt was recorded, which is at or after the run's own terminal instant — a receipt is
    /// only ever written when a run is abandoned, and a compensating sweep writes later still. Measuring from the row
    /// keeps the floor on a column the claim query can read, and can never make it earlier than the rule it states.
    /// </summary>
    public static readonly DurableRetentionRule CleanupReceipt =
        new(DurableRecordClass.CleanupReceipt, TimeSpan.FromDays(30), Quarantine, Recheck);

    /// <summary>
    /// Ninety days after the claim reached a terminal state. Three times the only window a team cap is ever measured
    /// over (<c>TeamCostCap.RollingThirtyDays</c>), so a reclaimed row is one no cap arithmetic can still reach — a
    /// property a test pins against that window rather than leaving it to arithmetic in a reader's head.
    /// </summary>
    public static readonly DurableRetentionRule BudgetReservation =
        new(DurableRecordClass.BudgetReservation, TimeSpan.FromDays(90), Quarantine, Recheck);

    /// <summary>The committed table, public so a test can pin every literal value in it.</summary>
    public static readonly IReadOnlyDictionary<DurableRecordClass, DurableRetentionRule> Rules =
        new Dictionary<DurableRecordClass, DurableRetentionRule>
        {
            [LogStream.Class] = LogStream,
            [CleanupReceipt.Class] = CleanupReceipt,
            [BudgetReservation.Class] = BudgetReservation,
        };

    /// <summary>The rule for <paramref name="value"/>, or null when this build registers none — which every consumer reads as keep.</summary>
    public static DurableRetentionRule? For(DurableRecordClass value) => Rules.TryGetValue(value, out var rule) ? rule : null;
}

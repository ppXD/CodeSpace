using CodeSpace.Messages.Retention;

namespace CodeSpace.Core.Services.Workflows.Retention;

/// <summary>
/// The retention policy for the durable records that are not artifacts: which classes exist and how long their records
/// are kept. Values are committed here and changed by a pull request — there is no environment override, because a
/// mistyped retention window is unrecoverable data loss and a code review is the control that belongs in front of it.
///
/// <para>An unregistered class is NOT an error a cursor can shrug off: <see cref="For"/> returns null and the decision
/// settles Indeterminate, which keeps the record forever. That is what makes removing a class from this table safe.</para>
/// </summary>
public static class DurableRetentionPolicy
{
    private static readonly TimeSpan Quarantine = TimeSpan.FromHours(24);

    /// <summary>
    /// Thirty days after the stream reached its terminal capture state. Deliberately far longer than any run and
    /// longer than any Room a human is still reading: the bytes this reclaims are an archive nobody opened, and the
    /// floor costs storage rather than evidence.
    /// </summary>
    public static readonly DurableRetentionRule LogStream = new(DurableRecordClass.LogStream, TimeSpan.FromDays(30), Quarantine);

    /// <summary>Thirty days after the run went terminal. An Orphaned receipt is NEVER collected — it names a resource nobody reclaimed, so it is kept until something compensates it.</summary>
    public static readonly DurableRetentionRule CleanupReceipt = new(DurableRecordClass.CleanupReceipt, TimeSpan.FromDays(30), Quarantine);

    /// <summary>The same floor as <see cref="LogStream"/>, because a gap describes a span of exactly one stream: outliving it would leave a hole with nothing to be a hole in, and predeceasing it would make an incomplete capture read complete.</summary>
    public static readonly DurableRetentionRule CaptureGap = new(DurableRecordClass.CaptureGap, TimeSpan.FromDays(30), Quarantine);

    /// <summary>Half a year, and only for evidence no sealed result pins. A qualification claim is re-examined long after it was made, so its evidence outlives every other class here by a wide margin.</summary>
    public static readonly DurableRetentionRule QualificationEvidence = new(DurableRecordClass.QualificationEvidence, TimeSpan.FromDays(180), Quarantine);

    /// <summary>Ninety days after reconciliation. A live reservation has no rule at all — it is not in a terminal state and therefore never a candidate.</summary>
    public static readonly DurableRetentionRule BudgetReservation = new(DurableRecordClass.BudgetReservation, TimeSpan.FromDays(90), Quarantine);

    /// <summary>Seven days after the transfer saga ended. The record itself is small; what this class exists to reclaim is the staging object its terminal row still names.</summary>
    public static readonly DurableRetentionRule TransferIntent = new(DurableRecordClass.TransferIntent, TimeSpan.FromDays(7), Quarantine);

    /// <summary>The committed table, public so a test can pin every literal value in it.</summary>
    public static readonly IReadOnlyDictionary<DurableRecordClass, DurableRetentionRule> Rules =
        new Dictionary<DurableRecordClass, DurableRetentionRule>
        {
            [LogStream.Class] = LogStream,
            [CleanupReceipt.Class] = CleanupReceipt,
            [CaptureGap.Class] = CaptureGap,
            [QualificationEvidence.Class] = QualificationEvidence,
            [BudgetReservation.Class] = BudgetReservation,
            [TransferIntent.Class] = TransferIntent,
        };

    /// <summary>The rule for <paramref name="value"/>, or null when this build registers none — which every consumer reads as keep.</summary>
    public static DurableRetentionRule? For(DurableRecordClass value) => Rules.TryGetValue(value, out var rule) ? rule : null;
}

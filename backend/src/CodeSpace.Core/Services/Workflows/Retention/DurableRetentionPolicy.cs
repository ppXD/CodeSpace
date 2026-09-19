using CodeSpace.Messages.Retention;

namespace CodeSpace.Core.Services.Workflows.Retention;

/// <summary>
/// The retention policy for the durable records that are not artifacts: which classes exist and how long their records
/// are kept. Values are committed here and changed by a pull request — there is no environment override, because a
/// mistyped retention window is unrecoverable data loss and a code review is the control that belongs in front of it.
///
/// <para>One class, because one cursor exists. Every other plane a reaper will eventually reach — cleanup receipts,
/// capture gaps, qualification evidence, budget reservations, terminal transfer intents — gets its rule in the same
/// commit as the cursor that acts on it, so this table can always be read as "what is actually reclaimed".</para>
///
/// <para>An unregistered class is NOT an error a cursor can shrug off: <see cref="For"/> returns null and the decision
/// settles Indeterminate, which keeps the record forever. That is what makes removing a class from this table safe.</para>
/// </summary>
public static class DurableRetentionPolicy
{
    /// <summary>
    /// Thirty days after the stream reached its terminal capture state, then a day of quarantine, and a day between
    /// looks at a stream that was kept. Deliberately far longer than any run and longer than any Room a human is still
    /// reading: the bytes this reclaims are an archive nobody opened, and the floor costs storage rather than evidence.
    /// </summary>
    public static readonly DurableRetentionRule LogStream =
        new(DurableRecordClass.LogStream, TimeSpan.FromDays(30), TimeSpan.FromHours(24), TimeSpan.FromHours(24));

    /// <summary>The committed table, public so a test can pin every literal value in it.</summary>
    public static readonly IReadOnlyDictionary<DurableRecordClass, DurableRetentionRule> Rules =
        new Dictionary<DurableRecordClass, DurableRetentionRule> { [LogStream.Class] = LogStream };

    /// <summary>The rule for <paramref name="value"/>, or null when this build registers none — which every consumer reads as keep.</summary>
    public static DurableRetentionRule? For(DurableRecordClass value) => Rules.TryGetValue(value, out var rule) ? rule : null;
}

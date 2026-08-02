namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// THE question every side effect in an agent's completion path has to ask before it fires: does this attempt still
/// own the run?
///
/// <para>An attempt claims a run at a fence epoch. A reconciler that finds the attempt abandoned reclaims it by
/// bumping that epoch, and the old worker — which may still be alive, mid-flight, and unaware — will lose the
/// completion CAS when it finally returns. Everything it does between the reclaim and that loss is work performed
/// on behalf of a run it no longer owns.</para>
///
/// <para>This exists as one authority because the rule was already implemented twice and the two disagreed about
/// WHERE it applied: the branch push asked it and refused, while the delivery-ledger row asserting that the push had
/// happened did not ask at all. A rule stated in two places drifts on which places it covers, and that drift is
/// precisely how a zombie worker could be denied the reversible remote effect and allowed the permanent claim
/// about it.</para>
///
/// <para>Pure and epoch-only on purpose. Reading the CURRENT epoch is the caller's job, because how to read it
/// differs by layer — one holds an entity, one holds a DbSet, a third will hold a conditional UPDATE predicate —
/// and forcing a single read path here would have made the SQL-level comparison impossible.</para>
/// </summary>
public static class AgentRunFence
{
    /// <summary>
    /// Whether an attempt holding <paramref name="claimedEpoch"/> still owns a run whose current epoch is
    /// <paramref name="currentEpoch"/>. Equality, stated once: a later epoch means a reclaimer took the run, and an
    /// EARLIER one is impossible by construction, so anything other than equality is "not ours".
    /// </summary>
    public static bool StillOwns(long currentEpoch, long claimedEpoch) => currentEpoch == claimedEpoch;

    /// <summary>The warning a refusing caller logs, so an operator reads the same sentence wherever a zombie was stopped rather than having to recognise several.</summary>
    public static string RefusalNote(string effect, long currentEpoch, long claimedEpoch) =>
        $"skipping {effect} — the run was reclaimed (epoch {currentEpoch} != claimed {claimedEpoch}); its completion would lose the CAS anyway";
}

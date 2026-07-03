using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// Translates ONE merged-timeline <see cref="RunTimelineEvent"/> into a render-ready <see cref="JournalStep"/> — the
/// per-source (per-kind) copy + classification authority for the chronological journal. A describer claims the events
/// it knows (<see cref="CanDescribe"/>, typically by <c>SourceKey</c>) and maps them; the registry dispatches the first
/// claimant. A NEW event source / kind plugs in as a dropped <see cref="IScopedDependency"/> impl the registry's injected
/// <c>IEnumerable</c> picks up with ZERO registry edit (the timeline-source pattern) — and anything NO describer claims
/// still renders via the mandatory <see cref="IJournalFallbackDescriber"/>, so a step is NEVER silently dropped.
///
/// <para>PURE — event in, step out, no DB / no I/O (the projector does the reads + hands the ordered events in), so a
/// describer is unit-tested exhaustively without infrastructure. Singleton (stateless).</para>
/// </summary>
public interface IJournalStepDescriber : ISingletonDependency
{
    /// <summary>Whether this describer handles the event (typically <c>e.SourceKey == &lt;its source key&gt;</c>). The registry uses the FIRST claimant.</summary>
    bool CanDescribe(RunTimelineEvent e);

    /// <summary>Map the claimed event to its render-ready journal step.</summary>
    JournalStep Describe(RunTimelineEvent e);
}

/// <summary>
/// The MANDATORY fallback describer — renders ANY event no <see cref="IJournalStepDescriber"/> claimed as a plain
/// generic step (never dropped). A DISTINCT interface (not an <see cref="IJournalStepDescriber"/>) so it is NEVER in the
/// registry's specific-describer list — it is the guaranteed last resort the registry always holds. Exactly one impl.
/// </summary>
public interface IJournalFallbackDescriber : ISingletonDependency
{
    JournalStep Describe(RunTimelineEvent e);
}

/// <summary>Dispatches a timeline event to its describer (first claimant, else the fallback) — the one seam the journal walk calls. See <see cref="JournalStepDescriberRegistry"/>.</summary>
public interface IJournalStepDescriberRegistry
{
    JournalStep Describe(RunTimelineEvent e);
}

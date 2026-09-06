namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// The gateway's own health over ONE corpus run, reported NEXT TO the capability number it produced (a pure data
/// noun, Rule 18.1). Two counts, never one: <see cref="Respawns"/> alone says how hard the instrument had to work,
/// but not whether the repair CHANGED the answer — a reader of "solved=12, formatFaultRespawns=9" cannot tell
/// whether those 12 solves were produced under the corpus's declared configuration or under the mitigation's
/// (a fresh conversation with extended thinking DISABLED, a materially different model configuration).
/// <see cref="SolvedAfterMitigation"/> names exactly that overlap, so a rate leaning on the degraded configuration
/// is visible in the verdict line instead of inferred from a number that moved for reasons nothing states.
/// </summary>
public sealed record FormatFaultTally
{
    /// <summary>How many cells the gateway-format-fault mitigation respawned (each cell buys the repair at most once, so this is also the count of cells that hit the fault on their first attempt).</summary>
    public int Respawns { get; init; }

    /// <summary>Of those respawned cells, how many the oracle then graded SOLVED — i.e. how many of the run's solves were produced with extended thinking disabled rather than under the corpus's declared configuration.</summary>
    public int SolvedAfterMitigation { get; init; }

    /// <summary>The verdict-line rendering — ONE wording, shared by every lane that reports it, so two runs are read against the same two names.</summary>
    public override string ToString() => $"formatFaultRespawns={Respawns}, solvedAfterMitigation={SolvedAfterMitigation}";
}

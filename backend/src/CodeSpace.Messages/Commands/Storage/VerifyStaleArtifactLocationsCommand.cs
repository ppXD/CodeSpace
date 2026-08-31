using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Storage;

/// <summary>
/// Re-asks providers whether the objects their recorded locations name are still there, and still what was recorded.
///
/// <para>No permission marker: dispatched by a recurring job on a processing pod, across every team. It changes no row
/// a user owns — only the observed state of a location, and only ever on an answer that leaves no room for doubt.</para>
/// </summary>
public sealed record VerifyStaleArtifactLocationsCommand : ICommand<VerifyStaleArtifactLocationsResponse>;

public sealed record VerifyStaleArtifactLocationsResponse
{
    public required int Checked { get; init; }
    public required int Confirmed { get; init; }
    public required int Missing { get; init; }
    public required int Corrupt { get; init; }
    public required int Inconclusive { get; init; }

    /// <summary>Rows the provider answered about and the database refused to record. Apart from <see cref="Inconclusive"/> on purpose: a pass that could reach every destination and write none of it down is a different fault from one that could reach nothing.</summary>
    public required int Unrecorded { get; init; }

    /// <summary>
    /// Rows the pass selected and then dropped unasked, because their destination had already failed to answer for
    /// itself earlier in the same pass. The count this response would otherwise lose entirely: one destination going
    /// quiet drops rows by the dozen and answers for exactly one, so a response without this number reports a batch of
    /// a hundred as a pass that examined two.
    /// </summary>
    public required int Skipped { get; init; }
}

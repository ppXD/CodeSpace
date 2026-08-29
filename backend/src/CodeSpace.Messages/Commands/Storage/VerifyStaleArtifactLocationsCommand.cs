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
}

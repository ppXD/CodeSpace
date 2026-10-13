namespace CodeSpace.Core.Services.Fleet;

/// <summary>
/// What a worker of THIS build can read that a previous build cannot, advertised by every job server it starts as an
/// extra queue name that nothing is ever enqueued to. A previous-version worker does not list a marker it never knew,
/// so its absence from any live server is what holds a new shape back (<see cref="IFleetCapabilities"/>).
///
/// <para>A marker is compared across builds, so it is never edited: a new shape gets a new marker, and a build that
/// still reads an old shape keeps advertising the old one.</para>
/// </summary>
public static class FleetCapabilityMarkers
{
    /// <summary>
    /// Reads a sealed payload (0247): an <c>agent_run</c> task or a <c>workflow_run_request</c> payload whose public
    /// column is masked and whose exact copy is in the encrypted sealed column. A worker without it executes the mask.
    /// </summary>
    public const string SealedPayloadV1 = "cap-sealed-payload-v1";

    /// <summary>Every marker this build's job servers advertise.</summary>
    public static readonly IReadOnlyList<string> Advertised = [SealedPayloadV1];
}

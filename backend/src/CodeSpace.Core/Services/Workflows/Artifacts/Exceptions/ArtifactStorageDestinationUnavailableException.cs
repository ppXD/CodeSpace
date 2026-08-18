using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;

/// <summary>
/// New artifact bytes could not be placed where the operator's storage route says they belong. The write fails here
/// rather than falling back to local disk: a fallback would keep runs green while the configured destination held
/// nothing, which is exactly the dishonesty the routed artifact plane exists to remove. Reads are unaffected — they
/// resolve through the profile revision their location was stamped with, in every lifecycle state.
/// </summary>
public sealed class ArtifactStorageDestinationUnavailableException : Exception, IFailure
{
    public ArtifactStorageDestinationUnavailableException(Guid teamId, WorkflowArtifactDestinationProblem problem)
        : base($"Team {teamId} routes '{WorkflowArtifactDestinationResolver.DataClassTypeKey}' to a storage destination that cannot accept new content ({problem}); refusing to place the bytes on local disk instead.")
    {
        TeamId = teamId;
        RoutingProblem = problem;
    }

    public ArtifactStorageDestinationUnavailableException(Guid teamId, ArtifactCasProblemCode problem)
        : base($"Team {teamId}'s routed '{WorkflowArtifactDestinationResolver.DataClassTypeKey}' transfer did not commit ({problem}); refusing to place the bytes on local disk instead.")
    {
        TeamId = teamId;
        TransferProblem = problem;
    }

    public Guid TeamId { get; }

    /// <summary>Set when routing policy itself refused the write; null when the transfer reached the provider.</summary>
    public WorkflowArtifactDestinationProblem? RoutingProblem { get; }

    /// <summary>Set when the CAS transfer refused or deferred the commit; null when routing refused first.</summary>
    public ArtifactCasProblemCode? TransferProblem { get; }

    FailureKind IFailure.Kind => FailureKind.Unavailable;
    string IFailure.Code => FailureCodes.ArtifactStorageDestinationUnavailable;
    string? IFailure.ClientMessage => "The configured artifact storage destination cannot accept new content right now.";
}

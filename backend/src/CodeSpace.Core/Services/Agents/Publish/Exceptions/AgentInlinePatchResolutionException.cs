namespace CodeSpace.Core.Services.Agents.Publish.Exceptions;

/// <summary>
/// A manifest-selected inline patch cannot be bound to one trustworthy carrier. This is a durable result/manifest
/// integrity failure, never an empty diff: execution consumers must fail closed instead of applying another
/// repository's bytes or silently dropping an offloaded patch.
/// </summary>
public sealed class AgentInlinePatchResolutionException : Exception
{
    public AgentInlinePatchResolutionException(string repositoryAlias, AgentInlinePatchResolutionKind kind, Guid? artifactId = null)
        : base(MessageFor(repositoryAlias, kind, artifactId))
    {
        RepositoryAlias = repositoryAlias;
        Kind = kind;
        ArtifactId = artifactId;
    }

    public string RepositoryAlias { get; }
    public AgentInlinePatchResolutionKind Kind { get; }
    public Guid? ArtifactId { get; }

    private static string MessageFor(string repositoryAlias, AgentInlinePatchResolutionKind kind, Guid? artifactId) => kind switch
    {
        AgentInlinePatchResolutionKind.RepositoryAliasMissing => $"Agent result has repository outcomes, but none exactly matches manifest alias '{repositoryAlias}'.",
        AgentInlinePatchResolutionKind.RepositoryAliasAmbiguous => $"Agent result has more than one repository outcome exactly matching manifest alias '{repositoryAlias}'.",
        _ => $"Agent result repository '{repositoryAlias}' names offloaded patch {artifactId}; refusing an inline read whose manifest did not select that artifact.",
    };
}

public enum AgentInlinePatchResolutionKind
{
    RepositoryAliasMissing = 0,
    RepositoryAliasAmbiguous = 1,
    UnexpectedArtifactReference = 2,
}

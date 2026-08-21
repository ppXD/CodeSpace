namespace CodeSpace.Core.Services.Agents.Publish.Exceptions;

/// <summary>More than one durable manifest claims the same concrete repository for one owner. Required consumers cannot choose an arbitrary row as authority.</summary>
public sealed class PublishManifestRepositorySelectionException : Exception
{
    public PublishManifestRepositorySelectionException(Guid repositoryId, int matchCount)
        : base($"Publish manifest repository '{repositoryId}' has {matchCount} exact rows; refusing an ambiguous required read.")
    {
        RepositoryId = repositoryId;
        MatchCount = matchCount;
    }

    public Guid RepositoryId { get; }
    public int MatchCount { get; }
}

namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Marker for commands/queries keyed by RepositoryId (URL shape /repositories/:id/…).
/// The pipeline behavior dereferences the repository → its TeamId → membership check.
/// One extra DB read per call; the cleaner contract is worth it.
/// </summary>
public interface IRequireRepositoryAccess
{
    Guid RepositoryId { get; }
}

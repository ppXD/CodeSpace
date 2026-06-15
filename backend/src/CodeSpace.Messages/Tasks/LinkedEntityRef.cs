namespace CodeSpace.Messages.Tasks;

/// <summary>
/// A reference to the external entity a task was launched FROM — an issue, a PR, a chat message — so the
/// projection / grounding can link back to it (Rule 18.1, a pure data noun). <see cref="EntityKind"/> and
/// <see cref="EntityId"/> are OPEN STRINGS (the surface that produced the seed owns their meaning); the
/// optional <see cref="RepositoryId"/> / <see cref="Url"/> carry the provider context when known. Wholly
/// optional on a <c>TaskLaunchSeed</c> — a free-form task has none.
/// </summary>
public sealed record LinkedEntityRef
{
    /// <summary>The kind of linked entity (e.g. <c>"issue"</c>, <c>"pull-request"</c>, <c>"message"</c>) — an open string the producing surface defines.</summary>
    public required string EntityKind { get; init; }

    /// <summary>The provider-scoped id of the linked entity (e.g. an issue iid, a PR number) — an open string.</summary>
    public required string EntityId { get; init; }

    /// <summary>The repository the entity lives in, when known.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>A direct link to the entity in the provider UI, when known.</summary>
    public string? Url { get; init; }
}

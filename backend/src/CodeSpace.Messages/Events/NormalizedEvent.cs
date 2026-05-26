using MediatR;

namespace CodeSpace.Messages.Events;

public abstract class NormalizedEvent : INotification
{
    public required Guid RepositoryId { get; init; }
    public required string ProviderEventId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

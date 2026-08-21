using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Sessions;

namespace CodeSpace.Core.Services.Sessions;

internal interface ISessionRunMetadataPageReader : IScopedDependency
{
    Task<SessionRunMetadataPage?> ReadAsync(SessionRunMetadataPageRequest request, CancellationToken cancellationToken);
}

internal sealed record SessionRunMetadataPageRequest
{
    internal const int DefaultLimit = 128;
    internal const int MaximumLimit = 256;
    internal const int MaximumCursorLength = 512;
    internal const int MaximumClassifierBytes = 128;
    internal const int MaximumNodeIdBytes = 256;
    internal const int MaximumErrorBytes = 512;

    public required Guid TeamId { get; init; }
    public required SessionRunMetadataSelector Selector { get; init; }
    public SessionRunMetadataPageDirection Direction { get; init; } = SessionRunMetadataPageDirection.Tail;
    public string? Cursor { get; init; }
    public int Limit { get; init; } = DefaultLimit;
}

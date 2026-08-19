namespace CodeSpace.Messages.Dtos.Storage;

/// <summary>
/// Discovery metadata for one versioned data class this build can route. It answers "which classes may a route name",
/// never "where does this team's data go" — no route, profile, credential or team state belongs here.
/// </summary>
public sealed record RoutedDataClassDescriptor
{
    public required string TypeKey { get; init; }
    public required string DisplayName { get; init; }
}

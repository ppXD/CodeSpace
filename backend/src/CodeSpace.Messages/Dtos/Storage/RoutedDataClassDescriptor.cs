namespace CodeSpace.Messages.Dtos.Storage;

/// <summary>
/// Discovery metadata for one versioned data class this build can route. It answers "which classes may a route name",
/// never "where does this team's data go" — no route, profile, credential or team state belongs here.
/// </summary>
public sealed record RoutedDataClassDescriptor
{
    public required string TypeKey { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>
    /// Whether this class has a durable home OUTSIDE the routing plane, so a team that has not routed it is storing
    /// its data somewhere rather than not storing it.
    ///
    /// <para>It is the difference between the two sentences a screen owes an operator about an unrouted class -
    /// "these are written to this server's own disk" and "these are not captured at all" - and the difference is not
    /// cosmetic: the second says data is being lost. Projected from the class's own
    /// <c>IRoutedDataClassLocalFallback</c> declaration so a screen never has to know the classes by name.</para>
    /// </summary>
    public required bool HasLocalFallback { get; init; }
}

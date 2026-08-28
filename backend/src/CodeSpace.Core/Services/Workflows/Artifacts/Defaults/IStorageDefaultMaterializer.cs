namespace CodeSpace.Core.Services.Workflows.Artifacts.Defaults;

/// <summary>
/// Turns one enabled deployment template into one team's own storage: a team-owned credential, an Active profile whose
/// namespace is that team's alone, an Active route, and the provenance row recording that it happened.
///
/// <para>Every write happens in ONE transaction, and the destination is PROVED to work before the route is activated.
/// Both are load-bearing rather than tidy. Nothing this writes can be undone afterwards: <c>storage_profile</c> and
/// <c>storage_credential</c> both reject DELETE, a route can never return to Draft, Retired is terminal, and the
/// provenance row's identity columns are immutable. A half-finished materialization is therefore permanent, and a
/// route activated onto a destination that turns out to be unusable does not merely fail the next write — the
/// artifact plane fails CLOSED, so an agent run that would have succeeded loses its diff and its transcripts instead.</para>
/// </summary>
public interface IStorageDefaultMaterializer
{
    Task<StorageMaterialization> MaterializeAsync(StorageMaterializationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One team, one data class, and who is asking.
///
/// <para><paramref name="Automatic"/> separates the two callers, because the template's adoption policy means
/// different things to them: an Automatic template may be materialized by a first write, while an Explicit one may
/// only ever be materialized by a team admin who chose it. A request that does not say which it is cannot enforce
/// that distinction.</para>
/// </summary>
public sealed record StorageMaterializationRequest(Guid TeamId, string DataClassTypeKey, Guid ActorId, bool Automatic);

/// <summary>
/// What happened, as a closed set. Exhaustive by construction so a caller cannot mistake "the team already had its own
/// route" for "this team is now on the deployment default" — the two look identical from a null return.
/// </summary>
public abstract record StorageMaterialization
{
    /// <summary>The team is now on the deployment default: profile and route both Active, provenance recorded.</summary>
    public sealed record Materialized(Guid StorageProfileId, Guid StorageRouteId, int SourceRevision) : StorageMaterialization;

    /// <summary>A provenance row for this (team, data class) already exists. Idempotent: the caller may treat this exactly as success.</summary>
    public sealed record AlreadyMaterialized(Guid StorageProfileId, int SourceRevision) : StorageMaterialization;

    /// <summary>The team already has a route for this class that the deployment did not put there. The team's own configuration always wins; a default is a default.</summary>
    public sealed record TeamOwnsRoute(Guid StorageRouteId) : StorageMaterialization;

    /// <summary>No template is authored for this data class.</summary>
    public sealed record NoTemplate : StorageMaterialization;

    /// <summary>A template exists but is switched off. Read under the row's own lock, not inferred from a revision comparison — disabling deliberately does not advance the revision.</summary>
    public sealed record TemplateDisabled : StorageMaterialization;

    /// <summary>The template is Explicit and this request is automatic. Materializing it would take the team off local storage permanently without anyone choosing that.</summary>
    public sealed record AdoptionRequiresChoice : StorageMaterialization;

    /// <summary>The assembled destination did not accept a write, so nothing was created. The reason is the provider's own, carried verbatim.</summary>
    public sealed record DestinationUnusable(string Reason) : StorageMaterialization;

    /// <summary>The team does not exist. Named rather than folded into a missing-template answer, because the two call for opposite responses: authoring a template, versus fixing the caller.</summary>
    public sealed record TeamNotFound : StorageMaterialization;

    /// <summary>A concurrent writer reached the same (team, data class) first. The caller may retry; a retry observes that writer's committed outcome.</summary>
    public sealed record RaceLost : StorageMaterialization;
}

namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The NORMALIZED task seed — what every launch surface (chat, an issue, an API call) erases its own
/// dimension into before any routing happens (Rule 18.1, a pure data noun). By this point the surface that
/// produced the task is already a flat <see cref="SurfaceKind"/> string, so the router / projection layer
/// never branches on "did this come from chat or an issue" — it reads a uniform seed.
///
/// <para>Only <see cref="Goal"/>, <see cref="SurfaceKind"/> and <see cref="TeamId"/> are required; everything
/// else is the optional context a richer surface supplies (the repo + branch the work targets, grounding
/// context, an effort / recipe hint the router MAY honour, the entity it was launched from, and free-form
/// surface facts). All *Kind hints are OPEN STRINGS — the router decides what to do with an unknown one.</para>
/// </summary>
public sealed record TaskLaunchSeed
{
    /// <summary>The natural-language objective the task pursues — the one thing every surface must supply.</summary>
    public required string Goal { get; init; }

    /// <summary>The launch surface this seed came from (e.g. <c>"chat"</c>, <c>"issue"</c>, <c>"api"</c>) — an open string, already flattened so downstream never re-branches on the origin.</summary>
    public required string SurfaceKind { get; init; }

    /// <summary>The team (tenancy) the task runs under — never surface-supplied past this point; the snapshot run inherits it.</summary>
    public required Guid TeamId { get; init; }

    /// <summary>The repository the work targets, when known. Null = an analysis-only / no-repo task.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>The base branch the work starts from, when the surface named one. Null = the repo's default.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>Pre-gathered grounding context (retrieved docs, the issue body, prior discussion) the projection may fold into the agent's prompt. Null = none.</summary>
    public string? GroundingContext { get; init; }

    /// <summary>An optional effort hint the surface suggested (e.g. <c>"quick"</c>, <c>"deep"</c>) — an open string the router MAY honour or override.</summary>
    public string? SuggestedEffort { get; init; }

    /// <summary>An optional recipe hint the surface suggested (e.g. <c>"bugfix"</c>, <c>"refactor"</c>) — an open string the router MAY honour or override.</summary>
    public string? SuggestedRecipe { get; init; }

    /// <summary>The external entity this task was launched from, when known.</summary>
    public LinkedEntityRef? LinkedEntity { get; init; }

    /// <summary>Free-form facts the surface attached (open key→string map) — grounding the router / projection may read without a typed contract. Defaults empty.</summary>
    public IReadOnlyDictionary<string, string> SeedFacts { get; init; } = new Dictionary<string, string>();
}

namespace CodeSpace.Messages.Dtos.Storage;

/// <summary>What an adoption attempt did. Every outcome is named, so a client never has to read success into an absence.</summary>
public sealed record StorageAdoptionResult
{
    public required StorageAdoptionOutcomeValue Outcome { get; init; }

    /// <summary>The team-owned profile the deployment default produced, when one exists.</summary>
    public Guid? StorageProfileId { get; init; }

    public Guid? StorageRouteId { get; init; }

    /// <summary>The template revision this team was materialized from. Compare against the template's current revision to find a team the deployment has since moved on from.</summary>
    public int? SourceRevision { get; init; }

    /// <summary>Operator-facing detail for an outcome that has one — today, why a destination was refused.</summary>
    public string? Detail { get; init; }
}

public enum StorageAdoptionOutcomeValue
{
    /// <summary>The team is now on the deployment default.</summary>
    Adopted,

    /// <summary>The team was already on it. Idempotent: indistinguishable from success for every purpose except telling the operator nothing changed.</summary>
    AlreadyAdopted,

    /// <summary>The team has its own route for this class, and a default never displaces one.</summary>
    TeamOwnsRoute,

    /// <summary>The deployment has authored no default for this class.</summary>
    NoTemplate,

    /// <summary>A default exists but the deployment has switched it off.</summary>
    TemplateDisabled,

    /// <summary>The destination would not accept a write, so nothing was created. <see cref="StorageAdoptionResult.Detail"/> says what the provider answered.</summary>
    DestinationUnusable,

    /// <summary>Another writer reached this team first. Retrying observes their outcome.</summary>
    RaceLost,
}

/// <summary>
/// One data class, and where this team stands on the deployment's default for it — the whole answer a Settings screen
/// needs to decide between offering adoption, reporting it, and explaining why neither applies.
/// </summary>
public sealed record StorageAdoptionStatus
{
    public required string DataClassTypeKey { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>False when the deployment has authored no default for this class, or has switched it off. Nothing about this team.</summary>
    public required bool DefaultAvailable { get; init; }

    /// <summary>True when this team has already been materialized from the deployment default.</summary>
    public required bool Adopted { get; init; }

    /// <summary>True when the team has a route for this class the deployment did not create — its own choice, which a default never displaces.</summary>
    public required bool TeamOwnsRoute { get; init; }

    /// <summary>
    /// Whether adopting is currently possible: a default is available, and nothing already claims the class. A screen
    /// should read THIS rather than re-deriving it, so the rule stays in one place.
    /// </summary>
    public required bool CanAdopt { get; init; }

    /// <summary>
    /// True when adopting takes this class off a durable home it has now, permanently — an Active route never returns
    /// to Draft. A screen must say so before asking.
    /// </summary>
    public required bool AdoptionIsIrreversible { get; init; }

    /// <summary>The template revision this team was materialized from, when it has been.</summary>
    public int? SourceRevision { get; init; }

    /// <summary>The template's current revision, when a default exists. Ahead of <see cref="SourceRevision"/> means the deployment has changed the default since this team took it.</summary>
    public int? TemplateRevision { get; init; }
}

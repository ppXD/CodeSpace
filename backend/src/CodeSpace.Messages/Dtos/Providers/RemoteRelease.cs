namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Provider-neutral release. GitHub "release" and GitLab "release" both normalise here. Powers BOTH the
/// Code tab's right-rail latest-release card (where <see cref="Body"/>/<see cref="Assets"/> go unused) and
/// the full Releases page (where they render the notes + downloads). Best-effort: list/latest calls may
/// leave <see cref="Body"/> null and <see cref="Assets"/> empty to keep payloads small.
/// </summary>
public sealed record RemoteRelease
{
    /// <summary>The tag the release is cut from — `3.0.5`. Always present (a release requires a tag).</summary>
    public required string TagName { get; init; }

    /// <summary>Human title when set, distinct from the tag. Null when the release was named after its tag.</summary>
    public string? Name { get; init; }

    /// <summary>Release notes (markdown). Populated on the Releases-page list; null on the latest-release card.</summary>
    public string? Body { get; init; }

    public string? AuthorLogin { get; init; }

    /// <summary>When the release was published. Null when the provider didn't report a date.</summary>
    public DateTimeOffset? PublishedDate { get; init; }

    public required string WebUrl { get; init; }

    /// <summary>True for a pre-release (GitHub `prerelease`). GitLab has no pre-release flag → always false there.</summary>
    public bool IsPrerelease { get; init; }

    /// <summary>True only for the repository's single "Latest" release — GitHub's badge. Computed by the provider for the list.</summary>
    public bool IsLatest { get; init; }

    /// <summary>Downloadable assets (source archives + uploaded files). Empty on the latest-release card.</summary>
    public IReadOnlyList<RemoteReleaseAsset> Assets { get; init; } = Array.Empty<RemoteReleaseAsset>();
}

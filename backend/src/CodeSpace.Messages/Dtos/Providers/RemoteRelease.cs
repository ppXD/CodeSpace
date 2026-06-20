namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Provider-neutral release for the Code tab's right-rail Releases card — the single latest release.
/// GitHub "release" and GitLab "release" both normalise here. Best-effort: null when the repo has no
/// releases. <see cref="WebUrl"/> points at the release on the provider; the card links the repo's
/// releases-list page separately.
/// </summary>
public sealed record RemoteRelease
{
    /// <summary>The tag the release is cut from — `3.0.5`. Always present (a release requires a tag).</summary>
    public required string TagName { get; init; }

    /// <summary>Human title when set, distinct from the tag. Null when the release was named after its tag.</summary>
    public string? Name { get; init; }

    /// <summary>When the release was published. Null when the provider didn't report a date.</summary>
    public DateTimeOffset? PublishedDate { get; init; }

    public required string WebUrl { get; init; }

    /// <summary>True for a pre-release (GitHub `prerelease`). GitLab has no pre-release flag → always false there.</summary>
    public bool IsPrerelease { get; init; }
}

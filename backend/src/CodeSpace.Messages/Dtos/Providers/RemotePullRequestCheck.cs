using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// One CI check on a PR. Normalised across GitHub Actions check_runs and GitLab
/// pipeline jobs — see <see cref="PullRequestCheckStatus"/> for the bucket model.
/// Multiple checks per PR are typical (one workflow = many jobs).
/// </summary>
public sealed record RemotePullRequestCheck
{
    /// <summary>Human-readable display name, e.g. "build / test (ubuntu-latest)".</summary>
    public required string Name { get; init; }

    public required PullRequestCheckStatus Status { get; init; }

    /// <summary>
    /// Provider's raw conclusion string ("success", "failure", "neutral", etc.) for the
    /// rare case where the UI wants more detail than the 6-bucket Status enum. Null when
    /// the check is still running (no conclusion yet).
    /// </summary>
    public string? Conclusion { get; init; }

    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Convenience field — duration in whole seconds when both timestamps exist.
    /// Saves the SPA from doing the math.
    /// </summary>
    public int? DurationSeconds { get; init; }

    /// <summary>
    /// Link to the check's page on the provider — `details_url` on GitHub,
    /// `web_url` on GitLab. Lets the user click through to view full logs.
    /// </summary>
    public string? DetailsUrl { get; init; }
}

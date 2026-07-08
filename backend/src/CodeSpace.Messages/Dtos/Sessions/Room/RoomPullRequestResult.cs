using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Sessions.Room;

/// <summary>The Room's Open-PR action result (PR-6) — one entry per repository the run published to (one for a single-repo run, N for a multi-repo one). A pure data noun (Rule 18.1).</summary>
public sealed record RoomPullRequestResult
{
    public required IReadOnlyList<RoomPullRequestOpened> PullRequests { get; init; }
}

/// <summary>
/// One repository's PR-open outcome. Honesty invariant (mirrors <c>ChangeSetPullRequestOutcome</c>): a multi-repo
/// run's per-repo failure is recorded here, never sinking the whole set — <see cref="Number"/>/<see cref="Url"/> are
/// null unless <see cref="Disposition"/> is <see cref="RoomPullRequestDisposition.Opened"/> or
/// <see cref="RoomPullRequestDisposition.AlreadyOpened"/>.
/// </summary>
public sealed record RoomPullRequestOpened
{
    public Guid? RepositoryId { get; init; }
    public required string Alias { get; init; }
    public required RoomPullRequestDisposition Disposition { get; init; }
    public int? Number { get; init; }
    public string? Url { get; init; }

    /// <summary>The skip reason (Skipped) or the redacted provider failure (Failed); null for Opened/AlreadyOpened.</summary>
    public string? Error { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoomPullRequestDisposition
{
    /// <summary>A PR was freshly opened this call.</summary>
    Opened,

    /// <summary>A PR already existed (a repeat click) — reused, not duplicated.</summary>
    AlreadyOpened,

    /// <summary>The repo had no branch to open a PR from — nothing to open, not a failure.</summary>
    Skipped,

    /// <summary>The provider rejected the open (scope / permission / validation); <see cref="RoomPullRequestOpened.Error"/> names why.</summary>
    Failed,
}

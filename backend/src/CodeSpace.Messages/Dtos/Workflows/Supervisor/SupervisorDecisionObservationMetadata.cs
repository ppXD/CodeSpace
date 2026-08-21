using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Workflows.Supervisor;

public static class SupervisorDecisionObservationPageLimits
{
    public const int DefaultLimit = 128;
    public const int MaximumLimit = 500;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupervisorDecisionObservationStoryPageMode
{
    Tail,
    Older,
    Newer,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupervisorDecisionObservationStatus
{
    Pending,
    AwaitingApproval,
    Running,
    Succeeded,
    Failed,
    Expired,
    LegacyUnknown,
    Corrupt,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupervisorDecisionObservationErrorState
{
    None,
    Complete,
    Truncated,
    Corrupt,
}

/// <summary>
/// Bounded observation-only metadata for one durable supervisor decision. DecisionKind deliberately remains an open
/// raw discriminator; Status is closed so future or malformed persisted values cannot fail EF materialization or be
/// mistaken for a known lifecycle state. Payload, outcome, envelopes and other bodies are structurally absent.
/// </summary>
public sealed record SupervisorDecisionObservationMetadata
{
    public required Guid DecisionId { get; init; }
    public required Guid SupervisorRunId { get; init; }
    public required string DecisionKind { get; init; }
    public required SupervisorDecisionObservationStatus Status { get; init; }
    public required long StoryOrder { get; init; }
    public required long ObservationRevision { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastModifiedAt { get; init; }
    public string? ErrorPrefix { get; init; }
    public required int ErrorTotalBytes { get; init; }
    public required SupervisorDecisionObservationErrorState ErrorState { get; init; }
}

/// <summary>
/// One bounded identity/story page. Items are always ascending by StoryOrder even though Tail/Older use a descending
/// index scan before their bounded in-memory reversal. SnapshotRevision is only the initial change-feed watermark
/// carried by the opaque story cursor; it is not a cross-request MVCC snapshot. An Older request can therefore show
/// the latest metadata for an older story row. HeadRevision is the current request's repeatable-read snapshot head.
/// </summary>
public sealed record SupervisorDecisionObservationStoryPage
{
    public required Guid SupervisorRunId { get; init; }
    public required string Mode { get; init; }
    public string? RequestCursor { get; init; }
    public required int Limit { get; init; }
    public required long SnapshotRevision { get; init; }
    public required long HeadRevision { get; init; }
    public required IReadOnlyList<SupervisorDecisionObservationMetadata> Items { get; init; }
    public required bool HasMore { get; init; }
    public string? NextOlderCursor { get; init; }
    public required string NextNewerCursor { get; init; }
}

/// <summary>
/// One bounded latest-state change page. SnapshotRevision echoes the decoded after-cursor (zero for the first page),
/// while HeadRevision is the greatest revision visible in this request's repeatable-read snapshot. Repeated updates to
/// one decision coalesce to its latest durable row; they never materialize outcome bytes.
/// </summary>
public sealed record SupervisorDecisionObservationChangePage
{
    public required Guid SupervisorRunId { get; init; }
    public string? RequestCursor { get; init; }
    public required int Limit { get; init; }
    public required long SnapshotRevision { get; init; }
    public required long HeadRevision { get; init; }
    public required IReadOnlyList<SupervisorDecisionObservationMetadata> Items { get; init; }
    public required bool HasMore { get; init; }
    public required string NextCursor { get; init; }
}

/// <summary>Opaque v1 story boundary, bound to one exact team/run identity and its initial change-feed watermark.</summary>
public readonly record struct SupervisorDecisionObservationStoryCursor(Guid TeamId, Guid SupervisorRunId, long StoryOrder, long SnapshotRevision)
{
    private const string Version = "story/v1";
    public const int MaximumEncodedLength = 192;

    public string Encode()
    {
        if (TeamId == Guid.Empty || SupervisorRunId == Guid.Empty || StoryOrder < 0 || SnapshotRevision < 0)
            throw new InvalidOperationException("A story cursor requires non-empty scope and non-negative positions.");

        return SupervisorDecisionObservationCursorCodec.Encode(Version, TeamId, SupervisorRunId, StoryOrder, SnapshotRevision);
    }

    public static bool TryDecode(string? value, Guid teamId, Guid supervisorRunId, out SupervisorDecisionObservationStoryCursor cursor)
    {
        cursor = default;
        if (!SupervisorDecisionObservationCursorCodec.TryDecode(value, Version, MaximumEncodedLength, expectedParts: 5, out var parts)) return false;
        if (!SupervisorDecisionObservationCursorCodec.TryScope(parts, teamId, supervisorRunId)) return false;
        if (!long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var storyOrder) || storyOrder < 0) return false;
        if (!long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var snapshotRevision) || snapshotRevision < 0) return false;
        cursor = new SupervisorDecisionObservationStoryCursor(teamId, supervisorRunId, storyOrder, snapshotRevision);
        return true;
    }
}

/// <summary>Opaque v1 observation-revision boundary, bound to one exact team/run identity.</summary>
public readonly record struct SupervisorDecisionObservationChangeCursor(Guid TeamId, Guid SupervisorRunId, long ObservationRevision)
{
    private const string Version = "change/v1";
    public const int MaximumEncodedLength = 160;

    public string Encode()
    {
        if (TeamId == Guid.Empty || SupervisorRunId == Guid.Empty || ObservationRevision < 0)
            throw new InvalidOperationException("A change cursor requires non-empty scope and a non-negative revision.");

        return SupervisorDecisionObservationCursorCodec.Encode(Version, TeamId, SupervisorRunId, ObservationRevision);
    }

    public static bool TryDecode(string? value, Guid teamId, Guid supervisorRunId, out SupervisorDecisionObservationChangeCursor cursor)
    {
        cursor = default;
        if (!SupervisorDecisionObservationCursorCodec.TryDecode(value, Version, MaximumEncodedLength, expectedParts: 4, out var parts)) return false;
        if (!SupervisorDecisionObservationCursorCodec.TryScope(parts, teamId, supervisorRunId)) return false;
        if (!long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0) return false;
        cursor = new SupervisorDecisionObservationChangeCursor(teamId, supervisorRunId, revision);
        return true;
    }
}

internal static class SupervisorDecisionObservationCursorCodec
{
    internal static string Encode(string version, Guid teamId, Guid supervisorRunId, params long[] positions)
    {
        var raw = string.Join('\n', new[] { version, teamId.ToString("N"), supervisorRunId.ToString("N") }
            .Concat(positions.Select(position => position.ToString(CultureInfo.InvariantCulture))));
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    internal static bool TryDecode(string? value, string version, int maximumLength, int expectedParts, out string[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength) return false;

        try
        {
            parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value)).Split('\n');
            return parts.Length == expectedParts && parts[0] == version;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static bool TryScope(string[] parts, Guid teamId, Guid supervisorRunId) =>
        teamId != Guid.Empty && supervisorRunId != Guid.Empty
        && Guid.TryParseExact(parts[1], "N", out var encodedTeamId) && encodedTeamId == teamId
        && Guid.TryParseExact(parts[2], "N", out var encodedRunId) && encodedRunId == supervisorRunId;
}

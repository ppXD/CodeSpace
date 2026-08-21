namespace CodeSpace.Messages.Dtos.Sessions.Room;

/// <summary>
/// Stable identity of one changed file. A repo-relative path is not globally unique in a multi-repository workspace;
/// repository id/alias bind it to the correct repo and agent-run id binds it to the producing attempt. The three
/// optional coordinates are null on legacy single-repo/path-only rows, which remain readable when unambiguous.
/// </summary>
public sealed record RoomFileIdentity
{
    public required string Path { get; init; }
    public Guid? AgentRunId { get; init; }
    public Guid? RepositoryId { get; init; }
    public string? RepositoryAlias { get; init; }
}

/// <summary>
/// A GENERIC preview of ONE file a turn produced — the backend resolves the file from the producing agent's captured
/// diff (durable, offline, any repo) and hands the frontend a rendered-by-<see cref="Kind"/> view. The frontend owns
/// no resolution logic: it renders text, a diff, a binary notice, or an unavailable notice by <see cref="Kind"/>, so
/// a new resolution source is a backend-only change. Always returned for a file in the turn's change set (never throws);
/// the query returns null only for a foreign / missing run (tenancy).
/// </summary>
public sealed record RoomFilePreview
{
    /// <summary>The repo-relative path previewed (echoes the request).</summary>
    public required string Path { get; init; }

    /// <summary>The exact file identity resolved, or the requested partial identity when resolution was unavailable.</summary>
    public RoomFileIdentity? Identity { get; init; }

    /// <summary>How the frontend renders this: <c>text</c> (full content) · <c>diff</c> (unified-diff section) · <c>binary</c> (notice) · <c>unavailable</c> (notice + optional source link).</summary>
    public required string Kind { get; init; }

    /// <summary>How the file changed this turn — <c>Added</c> / <c>Modified</c> / <c>Deleted</c> / <c>Renamed</c> / <c>Binary</c>. Null when the change couldn't be classified.</summary>
    public string? ChangeKind { get; init; }

    /// <summary>The renderable body: the full file content when <see cref="Kind"/> is <c>text</c>, or the unified-diff section when <c>diff</c>. Null for <c>binary</c> / <c>unavailable</c>.</summary>
    public string? Text { get; init; }

    /// <summary>The UTF-8 byte size of <see cref="Text"/> (before any truncation), when known.</summary>
    public long? SizeBytes { get; init; }

    /// <summary><see cref="Text"/> was capped to the preview limit — the frontend shows a "truncated" affordance.</summary>
    public bool Truncated { get; init; }

    /// <summary>A link to the same file/change on the provider (the turn's PR), when the turn delivered one — the fallback way to see a binary / unavailable file.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>A one-line human explanation when the preview is degraded (binary / unavailable / truncated). Null on a clean text/diff preview.</summary>
    public string? Note { get; init; }

    /// <summary>Typed reason when <see cref="Kind"/> is <c>unavailable</c>; null for renderable or binary previews.</summary>
    public RoomFileUnavailableReason? UnavailableReason { get; init; }
}

/// <summary>Closed, transport-stable reasons a changed file cannot currently produce a safe inline preview.</summary>
public enum RoomFileUnavailableReason
{
    NotInChangeSet,
    AmbiguousRepository,
    MetadataMissing,
    PhysicalObjectMissing,
    IntegrityFailure,
    BackendUnavailable,
    AccessDenied,
    ReconstructionUnavailable,
}

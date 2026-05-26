using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// One file in a PR's diff. <see cref="Patch"/> is a unified-diff hunk string in the
/// standard <c>@@ -a,b +c,d @@</c> format — both providers return that shape, so the
/// SPA can parse it identically. Large binary or generated files commonly omit the patch
/// (GitHub truncates patches > ~3000 lines); we surface that via a null Patch so the UI
/// can show "diff suppressed" instead of trying to render nothing.
/// </summary>
public sealed record RemotePullRequestFile
{
    public required string FileName { get; init; }

    /// <summary>Previous filename when the file was renamed (status = Renamed). Null otherwise.</summary>
    public string? PreviousFileName { get; init; }

    public required FileChangeStatus Status { get; init; }

    public required int Additions { get; init; }
    public required int Deletions { get; init; }

    /// <summary>
    /// Unified-diff patch text. Null when the provider suppressed the diff (binary, too
    /// large, etc.) — the UI shows a "view on provider" hint instead of trying to render.
    /// </summary>
    public string? Patch { get; init; }
}

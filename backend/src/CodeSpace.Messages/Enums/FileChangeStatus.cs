namespace CodeSpace.Messages.Enums;

/// <summary>
/// Per-file change status in a PR/MR diff. Normalised across providers:
///   GitHub returns lowercase strings (added/removed/modified/renamed/copied/changed/unchanged).
///   GitLab returns the trio (NewFile / DeletedFile / RenamedFile / [neither = Modified]).
/// We collapse to four canonical buckets the UI cares about; the diff itself carries
/// the line-level detail.
/// </summary>
public enum FileChangeStatus
{
    Added,
    Modified,
    Removed,
    Renamed
}

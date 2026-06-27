namespace CodeSpace.Messages.Enums;

/// <summary>The per-artifact outcome of a pack commit. <see cref="Updated"/> is the idempotent re-sync case (an
/// existing (pack, source-path) row was refreshed); <see cref="Skipped"/> is a handle collision with a different
/// active definition; <see cref="Failed"/> is an unreadable/unmatched path.</summary>
public enum PackImportOutcome
{
    Imported,
    Updated,
    Skipped,
    Failed,
}

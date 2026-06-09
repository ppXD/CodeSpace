namespace CodeSpace.Messages.Enums;

/// <summary>
/// Cross-provider classification of a single repository tree entry. GitHub's
/// file/dir/submodule/symlink and GitLab's blob/tree/commit both normalize into this so the
/// Code browser renders one shape regardless of provider.
/// </summary>
public enum RemoteTreeEntryType
{
    File,
    Directory,
    Submodule,
    Symlink
}

using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Source;

/// <summary>
/// Normalizes each provider's raw tree-entry-type token into the cross-provider
/// <see cref="RemoteTreeEntryType"/>. GitHub emits file/dir/submodule/symlink; GitLab emits
/// blob/tree/commit. Anything unrecognized falls back to <see cref="RemoteTreeEntryType.File"/> — a
/// safe default that renders as a leaf (never a fake folder that would dead-end navigation).
/// </summary>
public static class RepositoryTreeEntryTypeMap
{
    public static RemoteTreeEntryType From(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "dir" or "tree" => RemoteTreeEntryType.Directory,
        "submodule" or "commit" => RemoteTreeEntryType.Submodule,
        "symlink" => RemoteTreeEntryType.Symlink,
        _ => RemoteTreeEntryType.File
    };
}

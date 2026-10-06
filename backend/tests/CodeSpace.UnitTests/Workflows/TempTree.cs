namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// A GUID-named directory tree under the temp path, deleted on dispose, for tests that lay out real files and links.
/// <see cref="Root"/> is spelled through a symlink to the real directory and never resolved, as production never
/// resolves a workspace path, so every host exercises a workspace reached through a link — not only macOS, whose temp
/// path already runs through <c>/var</c>. Windows, where these tests do not run, gets the real directory.
/// </summary>
internal sealed class TempTree : IDisposable
{
    private readonly string _real = Path.Combine(Path.GetTempPath(), $"cs-tree-{Guid.NewGuid():N}");

    public TempTree()
    {
        System.IO.Directory.CreateDirectory(_real);
        Root = OperatingSystem.IsWindows() ? _real : System.IO.File.CreateSymbolicLink($"{_real}-link", _real).FullName;
    }

    /// <summary>The tree's root, through its link.</summary>
    public string Root { get; }

    /// <summary>The directory at <paramref name="relative"/> below the root, created with its parents.</summary>
    public string Directory(string relative) => System.IO.Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    /// <summary>The file at <paramref name="relative"/> below the root, written with <paramref name="content"/> and its parents created.</summary>
    public string File(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A symlink at <paramref name="relative"/> below the root whose target is <paramref name="target"/> exactly as given — absolute, or relative to the link's own directory.</summary>
    public string Link(string relative, string target)
    {
        var path = Path.Combine(Root, relative);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.CreateSymbolicLink(path, target);
        return path;
    }

    public void Dispose()
    {
        try { if (Root != _real) System.IO.File.Delete(Root); } catch { /* best-effort cleanup of a temp link */ }
        try { System.IO.Directory.Delete(_real, recursive: true); } catch { /* best-effort cleanup of a temp directory */ }
    }
}

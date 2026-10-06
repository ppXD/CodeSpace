namespace CodeSpace.Core.Services.Agents.Harnesses;

/// <summary>
/// Where a path really is once the kernel has followed every symlink on it. A harness needs this wherever a CLI keys
/// or loads something by the path it resolved rather than the one it was given.
/// </summary>
internal static class PhysicalPath
{
    /// <summary>The most links <see cref="File"/> follows on one path — the kernel's own limit (Linux <c>MAXSYMLINKS</c>), past which an open fails with <c>ELOOP</c>.</summary>
    internal const int MaxLinkHops = 40;

    /// <summary>
    /// The directory a process resolves <paramref name="path"/> to as its cwd: every component's symlink followed, a
    /// link whose own target runs through another link included. A path that does not exist resolves to no cwd, so it
    /// is returned as given.
    /// </summary>
    public static string Directory(string path)
    {
        if (!System.IO.Directory.Exists(path)) return path;

        var full = Path.GetFullPath(path);
        var physical = Path.GetPathRoot(full)!;

        foreach (var segment in full[physical.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(physical, segment);
            physical = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: true) is { } target ? Directory(target.FullName) : next;
        }

        return physical;
    }

    /// <summary>
    /// The entry the kernel reaches when a process opens <paramref name="path"/>, or null when it reaches none: a
    /// missing entry, a link that dangles, a link chain longer than <see cref="MaxLinkHops"/> or a component it may not
    /// search. The path is first normalised the way a caller's own path library does (a <c>..</c> in it is lexical),
    /// then walked one component at a time as the kernel walks it. A <c>..</c> inside a link's target is taken from
    /// where that link really is, never textually: <c>deep/../x</c> with <c>deep</c> linked to <c>/o/p/q</c> is
    /// <c>/o/p/x</c>, which a lexical reading would place beside <c>deep</c>.
    /// </summary>
    public static string? File(string path)
    {
        try
        {
            return Walk(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="physicalPath"/> is <paramref name="physicalRoot"/> or lies below it. Both must already be physical: a spelling through a symlink is compared as text.</summary>
    public static bool StaysInside(string physicalRoot, string physicalPath)
    {
        var root = Path.TrimEndingDirectorySeparator(physicalRoot);
        var below = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

        return physicalPath == root || physicalPath.StartsWith(below, StringComparison.Ordinal);
    }

    private static string? Walk(string full)
    {
        var current = Path.GetPathRoot(full)!;
        var pending = Components(full[current.Length..]);
        var hops = 0;

        while (pending.Count > 0)
        {
            var name = Pop(pending);

            if (name == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var next = Path.Combine(current, name);

            if (new FileInfo(next).LinkTarget is { } target)
            {
                if (++hops > MaxLinkHops) return null;
                if (Path.IsPathRooted(target)) current = Path.GetPathRoot(target)!;
                Push(pending, target);
                continue;
            }

            if (!Exists(next, isLast: pending.Count == 0)) return null;

            current = next;
        }

        return current;
    }

    /// <summary>A component that names nothing ends the walk, and so does a file with components still to walk below it, which the kernel refuses with <c>ENOTDIR</c>.</summary>
    private static bool Exists(string entry, bool isLast) => System.IO.Directory.Exists(entry) || (isLast && System.IO.File.Exists(entry));

    /// <summary>The components still to walk, the next one last; a <c>.</c> and an empty component name nothing and are dropped.</summary>
    private static List<string> Components(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Where(name => name != ".").Reverse().ToList();

    private static string Pop(List<string> pending)
    {
        var name = pending[^1];
        pending.RemoveAt(pending.Count - 1);
        return name;
    }

    /// <summary>A link's target goes in front of whatever was left below the link.</summary>
    private static void Push(List<string> pending, string target) => pending.AddRange(Components(Path.IsPathRooted(target) ? target[Path.GetPathRoot(target)!.Length..] : target));
}

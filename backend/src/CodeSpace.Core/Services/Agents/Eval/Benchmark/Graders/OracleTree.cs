namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;

/// <summary>
/// The file-level half of grading from platform-owned bytes: copy a candidate tree somewhere the agent never wrote, put
/// a judge's scope back exactly as the platform holds it, seal those bytes before the check runs, and fingerprint a
/// scope so a grade can tell what changed under it (<see cref="OracleGuard"/>).
///
/// <para>It walks trees the candidate wrote, so it never follows a symbolic link and never opens a file it has not
/// proven readable: a link is copied and compared as a link, and an empty file is never opened, because a FIFO also
/// reports a length of 0 and opening one for reading would hang the grade.</para>
/// </summary>
public static class OracleTree
{
    /// <summary>Copy <paramref name="source"/> into <paramref name="destination"/> (created if absent): directories, regular files with their mode, and links as links. A FIFO or socket arrives as an empty file. Refuses a destination inside the source, which would copy the copy into itself.</summary>
    public static void CopyTree(string source, string destination)
    {
        var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var to = Path.GetFullPath(destination);

        if (to.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Cannot copy {source} into {destination}: the destination lies inside the source");

        Directory.CreateDirectory(destination);

        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
            CopyEntry(entry, Path.Combine(destination, entry.Name));
    }

    /// <summary>
    /// Make <paramref name="scope"/> (a repo-relative file, or a directory ending in <c>/</c>) under
    /// <paramref name="gradingRoot"/> byte-identical to the same scope under <paramref name="platformRoot"/> — edits
    /// reverted, additions removed, deletions restored, modes put back — and return every relative path that differed
    /// beforehand, in ordinal order, classified (<see cref="ScopeChangeKind"/>). A link or file standing where the scope's
    /// directories should be is replaced, so the restore can never write through it to somewhere outside the grading tree.
    /// </summary>
    public static IReadOnlyList<ScopeChange> RestoreScope(string platformRoot, string gradingRoot, string scope)
    {
        var expected = Entries(platformRoot, scope);
        var actual = ContainedDirectories(gradingRoot, scope) ? Entries(gradingRoot, scope) : new Dictionary<string, Entry> { [scope.TrimEnd('/')] = Entry.Unreadable };

        var differing = expected.Keys.Union(actual.Keys).Select(path => Classify(path, expected.GetValueOrDefault(path), actual.GetValueOrDefault(path))).OfType<ScopeChange>().OrderBy(change => change.Path, StringComparer.Ordinal).ToList();

        Replace(platformRoot, gradingRoot, scope);

        return differing;
    }

    /// <summary>
    /// What <paramref name="scopes"/> under <paramref name="root"/> hold right now, by CONTENT: each file's digest and each
    /// link's target, never a mode (the seal changes those) and never a followed link. Past <paramref name="maxEntries"/>
    /// files it stops and says it is incomplete (<see cref="ScopeFingerprint.Complete"/>) instead of guessing.
    /// </summary>
    public static ScopeFingerprint Fingerprint(string root, IEnumerable<string> scopes, int maxEntries = DefaultFingerprintBudget)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var scope in scopes.Distinct(StringComparer.Ordinal))
        {
            var found = ContainedDirectories(root, scope) ? Entries(root, scope) : new Dictionary<string, Entry> { [scope.TrimEnd('/')] = Entry.Unreadable };

            foreach (var (path, entry) in found) entries[path] = entry.Content;

            if (entries.Count > maxEntries) return new ScopeFingerprint(new Dictionary<string, string>(StringComparer.Ordinal), Complete: false);
        }

        return new ScopeFingerprint(entries, Complete: true);
    }

    /// <summary>How many files a fingerprint reads before it gives up: a judge's own directory is small, and a scope this large is too costly to hash before and after every check.</summary>
    public const int DefaultFingerprintBudget = 20_000;

    /// <summary>Delete <paramref name="relative"/> under <paramref name="root"/> — a file, a link (never what it points at) or a directory — after making sure nothing on the way to it is a link that would carry the delete outside the tree.</summary>
    public static void RemovePath(string root, string relative)
    {
        if (!ContainedDirectories(root, relative)) return;

        Remove(Path.Combine(root, relative.TrimEnd('/')));
    }

    /// <summary>
    /// Remove every write bit from each regular file under <paramref name="scopes"/>. A speed bump, not a guarantee: the
    /// check runs as the user that owns these files, so its code can put a write bit back or replace a directory entry.
    /// What a grade relies on is the comparison after the check (<see cref="OracleGuard.AfterCheck"/>), which voids a grade
    /// whose platform-owned bytes changed. Directories stay writable: a judge may build or cache beside itself. Links are
    /// skipped (a chmod would follow them out of the tree). A no-op on Windows or for a scope that is absent.
    /// </summary>
    public static void Seal(string root, IEnumerable<string> scopes)
    {
        foreach (var scope in scopes)
            SealEntry(Path.Combine(root, scope.TrimEnd('/')));
    }

    private static void SealEntry(string path)
    {
        var info = new FileInfo(path);

        if (info.LinkTarget is not null) return;

        if (Directory.Exists(path))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                SealEntry(child);
            return;
        }

        if (info.Exists && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(path) & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
    }

    private static void CopyEntry(FileSystemInfo entry, string destination)
    {
        if (entry.LinkTarget is { } target)
        {
            File.CreateSymbolicLink(destination, target);
            return;
        }

        if (entry is DirectoryInfo directory)
        {
            CopyTree(directory.FullName, destination);
            return;
        }

        CopyFile((FileInfo)entry, destination);
    }

    private static void CopyFile(FileInfo file, string destination)
    {
        if (file.Length == 0) File.WriteAllBytes(destination, Array.Empty<byte>());
        else File.Copy(file.FullName, destination);

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, file.UnixFileMode);
    }

    /// <summary>Whether every directory on the way to <paramref name="scope"/> under <paramref name="root"/> is a real directory — not a link and not a file — so walking it stays inside the tree.</summary>
    private static bool ContainedDirectories(string root, string scope)
    {
        var path = root;

        foreach (var segment in scope.TrimEnd('/').Split('/').SkipLast(scope.EndsWith('/') ? 0 : 1))
        {
            path = Path.Combine(path, segment);
            var info = new DirectoryInfo(path);

            if (info.LinkTarget is not null || File.Exists(path)) return false;
        }

        return true;
    }

    /// <summary>The files and links under <paramref name="scope"/>, keyed by repo-relative path. Absent ⇒ empty.</summary>
    private static Dictionary<string, Entry> Entries(string root, string scope)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var path = Path.Combine(root, scope.TrimEnd('/'));

        if (new FileInfo(path).LinkTarget is not null || File.Exists(path)) entries[scope.TrimEnd('/')] = Entry.Of(new FileInfo(path));
        else if (Directory.Exists(path)) Collect(root, new DirectoryInfo(path), entries);

        return entries;
    }

    private static void Collect(string root, DirectoryInfo directory, Dictionary<string, Entry> entries)
    {
        foreach (var info in directory.EnumerateFileSystemInfos())
        {
            if (info is DirectoryInfo child && info.LinkTarget is null)
            {
                Collect(root, child, entries);
                continue;
            }

            entries[Path.GetRelativePath(root, info.FullName).Replace(Path.DirectorySeparatorChar, '/')] = Entry.Of(info);
        }
    }

    /// <summary>Delete whatever stands at <paramref name="scope"/> in the grading tree (and any link or file in place of its parent directories), then copy the platform's scope in.</summary>
    private static void Replace(string platformRoot, string gradingRoot, string scope)
    {
        var relative = scope.TrimEnd('/');
        var target = Path.Combine(gradingRoot, relative);

        EnsureRealParents(gradingRoot, relative);
        Remove(target);

        var source = Path.Combine(platformRoot, relative);

        if (Directory.Exists(source)) CopyTree(source, target);
        else if (File.Exists(source)) CopyFile(new FileInfo(source), target);
    }

    /// <summary>Make every directory on the way to <paramref name="relative"/> under <paramref name="root"/> a real directory, removing a link or file that stands in for one — so a write to <paramref name="relative"/> can never land outside the tree through it.</summary>
    public static void EnsureRealParents(string root, string relative)
    {
        var path = root;

        foreach (var segment in relative.Split('/').SkipLast(1))
        {
            path = Path.Combine(path, segment);

            if (new DirectoryInfo(path).LinkTarget is not null || File.Exists(path)) Remove(path);

            Directory.CreateDirectory(path);
        }
    }

    private static void Remove(string path)
    {
        var info = new FileInfo(path);

        if (info.LinkTarget is not null || info.Exists) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static ScopeChange? Classify(string path, Entry? expected, Entry? actual)
    {
        if (expected is null) return new ScopeChange(path, ScopeChangeKind.Added);

        if (actual is null || expected.Content != actual.Content) return new ScopeChange(path, ScopeChangeKind.Changed);

        return expected.Mode == actual.Mode ? null : new ScopeChange(path, ScopeChangeKind.ModeOnly);
    }

    /// <summary>How a path in a restored scope differed from the platform's: its bytes (an edit, a deletion, a link retargeted), its presence (an addition the platform never had), or only its mode.</summary>
    public enum ScopeChangeKind
    {
        /// <summary>The platform holds this path and the tree's content differed or was missing — a change to platform-owned bytes.</summary>
        Changed,

        /// <summary>The platform never held this path — the tree added it.</summary>
        Added,

        /// <summary>Same content, different mode (an exec bit added or dropped) — restored, but not a change to the platform's bytes.</summary>
        ModeOnly,
    }

    /// <summary>One path a restore found different, and how.</summary>
    public sealed record ScopeChange(string Path, ScopeChangeKind Kind);

    /// <summary>A scope's content at one instant (<see cref="Fingerprint"/>), keyed by repo-relative path.</summary>
    public sealed record ScopeFingerprint(IReadOnlyDictionary<string, string> Entries, bool Complete)
    {
        /// <summary>Every path the fingerprint holds, in ordinal order.</summary>
        public IReadOnlyList<string> Paths => Entries.Keys.OrderBy(path => path, StringComparer.Ordinal).ToList();

        /// <summary>What differs in <paramref name="after"/>: paths this fingerprint held whose content changed or vanished, and paths it never held.</summary>
        public ScopeComparison Compare(ScopeFingerprint after) => new(
            Entries.Where(entry => after.Entries.GetValueOrDefault(entry.Key) != entry.Value).Select(entry => entry.Key).OrderBy(path => path, StringComparer.Ordinal).ToList(),
            after.Entries.Keys.Where(path => !Entries.ContainsKey(path)).OrderBy(path => path, StringComparer.Ordinal).ToList());
    }

    /// <summary>The difference between two fingerprints of the same scopes.</summary>
    public sealed record ScopeComparison(IReadOnlyList<string> Changed, IReadOnlyList<string> Added);

    /// <summary>What a path holds, compared without following links or opening an empty file: a link's target, or a regular file's mode and bytes.</summary>
    private sealed record Entry(string? LinkTarget, UnixFileMode Mode, string? Sha256)
    {
        public static readonly Entry Unreadable = new("(not a directory)", 0, null);

        /// <summary>The entry's content alone — what a mode change leaves untouched.</summary>
        public string Content => LinkTarget is not null ? "link:" + LinkTarget : "file:" + Sha256;

        public static Entry Of(FileSystemInfo info)
        {
            if (info.LinkTarget is { } target) return new Entry(target, 0, null);

            var file = (FileInfo)info;
            var mode = OperatingSystem.IsWindows() ? 0 : file.UnixFileMode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            return new Entry(null, mode, file.Length == 0 ? "" : Hash(file.FullName));
        }

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);

            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        }
    }
}

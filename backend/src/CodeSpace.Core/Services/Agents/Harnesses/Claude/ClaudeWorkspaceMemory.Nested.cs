using System.Text;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents.Workspace;

namespace CodeSpace.Core.Services.Agents.Harnesses.Claude;

/// <summary>
/// Nested memory: a directory below the cwd that holds a <c>CLAUDE.md</c>, a <c>.claude/CLAUDE.md</c> or a rule without
/// <c>paths:</c>. The unpinned CLI attached it once the run read a file below that directory; the settings pin gates that
/// route, but an <c>--add-dir</c> naming the directory loads it in place, before the first request, labelled as project
/// instructions and with its in-repository imports — a scoped rule's included, though the rule itself stays out (observed
/// against 2.1.263). So every such directory is added after the workspace and its repositories, shallowest first — when
/// all of them together fit <see cref="MaxInPlaceDirectories"/> and <see cref="MaxInPlaceBytes"/>, the bytes of the files
/// their memory imports counted too. Past that none is: a partial pick would load arbitrary packages up front while the
/// one the run works in stays out, and the timeline says why. Each one is checked like any root (<see cref="Guard"/>) as
/// the walk finds it, which is how its imports are counted, so memory that reaches outside the workspace leaves its
/// directory out by name. A directory left out still counts against the budget, and one past the directory bound is not
/// checked at all, so the guard reads the imports of at most <see cref="MaxInPlaceDirectories"/> directories that hold
/// memory files — beside those whose only memory is scoped rules, which it checks for imports — within the build's
/// <see cref="Budget"/>.
///
/// <para>The walk never follows a link, and never enters <c>.git</c>, <c>node_modules</c> or another dot-directory, so
/// memory under <c>.github</c> stays where it is, and so does memory a read reaches through a directory link: the CLI
/// keys nested memory by the path read, the walk by the path it walked. A directory whose path below the cwd holds
/// anything but <see cref="SafeRelativePath"/>'s characters is not added: the name came from the repository and rides
/// the argv. The bounds (<see cref="MaxWalkDepth"/>, <see cref="MaxWalkedEntries"/>, and the build's
/// <see cref="Budget"/>) only cap what one build spends; memory past them is not found, and the timeline says so. Every
/// file is opened no-follow, non-blocking and only if regular, and nothing that resolves outside the workspace is opened
/// at all: such memory counts as held, with no bytes, and the guard then leaves its directory out.</para>
/// </summary>
internal static partial class ClaudeWorkspaceMemory
{
    /// <summary>The most nested directories added in place; one more and none is. Pinned by a test.</summary>
    internal const int MaxInPlaceDirectories = 16;

    /// <summary>The most bytes of memory files — <c>CLAUDE.md</c>, <c>.claude/CLAUDE.md</c>, unconditional rules — and of the files they and scoped rules import, all nested directories together may hold to be added in place. Pinned by a test.</summary>
    internal const int MaxInPlaceBytes = 32 * 1024;

    /// <summary>How many directories below the cwd the walk looks; a deeper one is not looked at. Pinned by a test.</summary>
    internal const int MaxWalkDepth = 32;

    /// <summary>The most entries the walk examines: every directory it lists, the cwd included, and every rules entry it reads. Pinned by a test.</summary>
    internal const int MaxWalkedEntries = 20000;

    /// <summary>The characters a nested directory's path below the cwd may hold.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._@+\-/]+\z")]
    private static partial Regex SafeRelativePath();

    private static readonly EnumerationOptions Listing = new() { AttributesToSkip = 0, IgnoreInaccessible = true };

    /// <summary>The nested directories to add in place, in walk order, and one sentence for each thing the walk left out.</summary>
    private sealed record Nested(IReadOnlyList<string> Directories, IReadOnlyList<string> Notices);

    /// <summary>One memory file the CLI reads from a directory: its bytes, and whether it is a rule scoped by <c>paths:</c>, which loads in place only what it imports. One that resolves outside the workspace has no bytes and is held as memory, so the guard then leaves its directory out.</summary>
    private sealed record MemoryFile(long Bytes, bool Scoped);

    /// <summary>
    /// One build's walk below <paramref name="cwd"/>. <paramref name="roots"/> are added already and are not nested
    /// memory, though the walk goes through them; <paramref name="workspace"/> is the physical root memory may resolve
    /// anywhere inside; <paramref name="guard"/> checks each directory to add, and holds the build's budget.
    /// </summary>
    private sealed class NestedWalk(string cwd, IEnumerable<string> roots, string workspace, Guard guard)
    {
        private readonly string _cwd = Path.TrimEndingDirectorySeparator(cwd);
        private readonly HashSet<string> _roots = roots.Select(Path.TrimEndingDirectorySeparator).ToHashSet(StringComparer.Ordinal);
        private readonly string _physicalCwd = PhysicalPath.File(cwd) ?? cwd;
        private readonly List<(string Directory, long Bytes)> _held = [];
        private readonly List<string> _notices = [];
        private int _entries;
        private bool _stopped;

        /// <summary>Every directory with memory, while the set still fits in place; none, and why, once it does not. Where the build's budget runs out the walk stops, and what it found so far is planned (the budget's notice says so).</summary>
        public Nested Plan()
        {
            try
            {
                foreach (var directory in Walk())
                {
                    if (MemoryOf(directory) is not { } bytes || !IsSafe(directory)) continue;

                    if (!Hold(directory, bytes)) return OverBudget();
                }
            }
            catch (MemoryBudgetSpentException)
            {
                // The walk stops here; BudgetNotice says so.
            }

            return new Nested(_held.Select(held => held.Directory).ToList(), _notices);
        }

        /// <summary>Every directory below the cwd that is no root, breadth first and by name within a level, to <see cref="MaxWalkDepth"/> deep.</summary>
        private IEnumerable<string> Walk()
        {
            var pending = new Queue<(string Directory, int Depth)>();

            pending.Enqueue((_cwd, 0));

            while (pending.TryDequeue(out var current) && Examine())
            {
                if (current.Depth > 0 && !_roots.Contains(current.Directory)) yield return current.Directory;

                var children = Subdirectories(current.Directory);

                if (current.Depth == MaxWalkDepth && children.Count > 0)
                {
                    Note($"Left any memory more than {MaxWalkDepth} directories below the workspace out of this run: the runner looks no deeper.");
                    continue;
                }

                foreach (var child in children) pending.Enqueue((Path.Combine(current.Directory, child), current.Depth + 1));
            }
        }

        /// <summary>The subdirectories the walk enters, by name: no link, no dot-directory, no <c>node_modules</c>.</summary>
        private static List<string> Subdirectories(string directory) =>
            Names(() => new DirectoryInfo(directory).EnumerateDirectories("*", Listing).Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint) && !child.Name.StartsWith('.') && child.Name != "node_modules").Select(child => child.Name));

        /// <summary>
        /// The bytes of memory the CLI would load from <paramref name="directory"/> in place, before what that memory
        /// imports, or null when it would load nothing: its <c>CLAUDE.md</c>, its <c>.claude/CLAUDE.md</c> and each rule it
        /// reads without globs. A directory whose only memory is rules scoped by <c>paths:</c> loads in place just what
        /// those import, so it is one to add when they import anything — or reach outside, which the guard then says.
        /// </summary>
        private long? MemoryOf(string directory)
        {
            var physical = Path.Combine(_physicalCwd, Path.GetRelativePath(_cwd, directory));
            var files = FileAt(Resolve(physical, "CLAUDE.md")).Concat(DotClaude(Path.GetRelativePath(_cwd, Path.Combine(directory, ".claude")), Resolve(physical, ".claude"))).ToList();

            if (files.Any(file => !file.Scoped)) return files.Where(file => !file.Scoped).Sum(file => file.Bytes);

            return files.Count > 0 && Imports(directory) ? 0 : null;
        }

        /// <summary>Whether the directory's memory imports anything the CLI reads, or reaches where the guard leaves it out.</summary>
        private bool Imports(string directory) => guard.Of(directory) is var check && (check.ImportedBytes > 0 || check.Escape is not null);

        /// <summary>What a <c>.claude</c> directory holds: its <c>CLAUDE.md</c> and its rules. One that resolves outside the workspace is held whole.</summary>
        private IEnumerable<MemoryFile> DotClaude(string spelled, string? dotClaude)
        {
            if (dotClaude is null || !Directory.Exists(dotClaude)) return [];

            if (!PhysicalPath.StaysInside(workspace, dotClaude)) return [new MemoryFile(0, false)];

            return FileAt(Resolve(dotClaude, "CLAUDE.md")).Concat(Rules(Path.Combine(spelled, "rules"), Resolve(dotClaude, "rules"), new HashSet<string>(StringComparer.Ordinal)));
        }

        /// <summary>
        /// Each rule under a rules folder, folders followed once each, by name; a folder that resolves outside the
        /// workspace is held with no bytes. An entry that is a link counts only while it resolves inside the cwd: the CLI
        /// skips one that leads anywhere else (2.1.263), so a rule linked into a sibling repository the cwd does not hold
        /// is no memory of this directory.
        /// </summary>
        private IEnumerable<MemoryFile> Rules(string spelled, string? folder, HashSet<string> seen)
        {
            if (folder is null || !Directory.Exists(folder)) yield break;

            if (!PhysicalPath.StaysInside(workspace, folder))
            {
                yield return new MemoryFile(0, false);
                yield break;
            }

            if (!seen.Add(folder)) yield break;

            foreach (var name in Names(() => new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Listing).Select(entry => entry.Name)))
            {
                if (!Examine()) yield break;

                var entry = Resolve(folder, name);

                if (entry is null || (entry != Path.Combine(folder, name) && !PhysicalPath.StaysInside(_physicalCwd, entry))) continue;

                if (Directory.Exists(entry))
                {
                    foreach (var file in Rules(Path.Combine(spelled, name), entry, seen)) yield return file;
                }
                else if (name.EndsWith(".md", StringComparison.Ordinal) && Rule(Path.Combine(spelled, name), entry) is { } rule)
                {
                    yield return rule;
                }
            }
        }

        /// <summary>A memory file: none for nothing there or a directory, no bytes for one outside the workspace, its length for a regular file inside it.</summary>
        private IEnumerable<MemoryFile> FileAt(string? physical)
        {
            if (physical is null || Directory.Exists(physical)) return [];

            if (!PhysicalPath.StaysInside(workspace, physical)) return [new MemoryFile(0, false)];

            return Length(physical) is { } length ? [new MemoryFile(length, false)] : [];
        }

        /// <summary>A rule with its length, scoped when the CLI reads globs from its frontmatter (<see cref="ClaudeRuleScope.Read"/>); none for one that is unreadable, or whose frontmatter runs past what the runner reads, which is said by its path below the cwd, <paramref name="relative"/>.</summary>
        private MemoryFile? Rule(string relative, string physical)
        {
            if (Frontmatter(physical) is not { } read) return null;

            if (!read.Cut) return new MemoryFile(read.Length, ClaudeRuleScope.Read(read.Text) is not null);

            Note($"Left the rule '{Printable(relative)}' unclassified: its frontmatter runs past {MaxScannedBytes} bytes, more than the runner reads to tell whether paths: scope it.");
            return null;
        }

        /// <summary>
        /// The text of a rule the CLI's frontmatter parse needs, and the rule's whole length: its first
        /// <see cref="ClaudeRuleScope.MaxHeadBytes"/>, or — when those open a fence they do not close — as much more as it
        /// takes to close it, to <see cref="MaxScannedBytes"/> in all, <c>Cut</c> when even that does not. Null for
        /// anything but a regular file, opened as <see cref="Length"/> opens it. Every byte read is spent from the build's
        /// budget.
        /// </summary>
        private (string Text, long Length, bool Cut)? Frontmatter(string physical)
        {
            try
            {
                using var stream = new FileStream(LocalAcceptanceFileIdentity.Open(physical, directory: false), FileAccess.Read);
                var bytes = ReadUpTo(stream, [], ClaudeRuleScope.MaxHeadBytes);
                var text = Encoding.UTF8.GetString(bytes);

                if (!ClaudeRuleScope.IsUnclosed(text) || stream.Length <= bytes.Length) return (text, stream.Length, false);

                bytes = ReadUpTo(stream, bytes, MaxScannedBytes);
                text = Encoding.UTF8.GetString(bytes);

                return (text, stream.Length, ClaudeRuleScope.IsUnclosed(text) && stream.Length > bytes.Length);
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary><paramref name="read"/> followed by the stream's next bytes, to <paramref name="total"/> in all or the stream's end, each spent from the build's budget.</summary>
        private byte[] ReadUpTo(Stream stream, byte[] read, int total)
        {
            var buffer = new byte[(int)Math.Min(total, Math.Max(read.Length, stream.Length))];

            read.CopyTo(buffer, 0);

            var length = read.Length + ReadFully(stream, buffer, read.Length);

            guard.Budget.Read(length - read.Length);
            return buffer[..length];
        }

        /// <summary>Whether the directory's path below the cwd may ride the argv; one that may not is said by name.</summary>
        private bool IsSafe(string directory)
        {
            if (SafeRelativePath().IsMatch(Path.GetRelativePath(_cwd, directory))) return true;

            _notices.Add($"Left the memory in {Place(_cwd, directory)} out of this run: its path holds a character other than a letter, a digit or one of . _ @ + - /.");
            return false;
        }

        /// <summary>One more nested directory to add in place, counted with the bytes the files its memory imports hold; false once the set no longer fits. One past the directory bound does not fit whatever it holds, so the guard never reads it.</summary>
        private bool Hold(string directory, long bytes)
        {
            if (_held.Count == MaxInPlaceDirectories) return false;

            _held.Add((directory, bytes + guard.Of(directory).ImportedBytes));

            return _held.Sum(held => held.Bytes) <= MaxInPlaceBytes;
        }

        private Nested OverBudget() => new([], [.. _notices, $"Left the memory of every nested directory out of this run: together it spans more than {MaxInPlaceDirectories} directories or {MaxInPlaceBytes} bytes, more than a run loads before its first request."]);

        /// <summary>One more entry examined; false, and said once, when that is more than the walk examines. Throws once the build's budget is spent.</summary>
        private bool Examine()
        {
            guard.Budget.EnsureLeft();

            if (_stopped) return false;

            if (++_entries <= MaxWalkedEntries) return true;

            _stopped = true;
            Note($"Left any memory past the first {MaxWalkedEntries} directories and rules the runner examined out of this run: it looks no further.");
            return false;
        }

        /// <summary>Where <paramref name="name"/> in a physical directory really is: there when it is no link, wherever its links lead when it is one — resolved against the build's budget — and null when that is nothing.</summary>
        private string? Resolve(string physicalDirectory, string name)
        {
            var path = Path.Combine(physicalDirectory, name);
            var info = new FileInfo(path);

            if (info.LinkTarget is not null) return guard.Budget.Resolve(path);

            return info.Exists || Directory.Exists(path) ? path : null;
        }

        private void Note(string notice)
        {
            if (!_notices.Contains(notice)) _notices.Add(notice);
        }
    }

    /// <summary>Names a listing returns, by ordinal order; none when the directory cannot be listed.</summary>
    private static List<string> Names(Func<IEnumerable<string>> listing)
    {
        try
        {
            return listing().Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The length of a regular file, opened no-follow and non-blocking; null for anything else.</summary>
    private static long? Length(string physical)
    {
        try
        {
            using var stream = new FileStream(LocalAcceptanceFileIdentity.Open(physical, directory: false), FileAccess.Read);
            return stream.Length;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

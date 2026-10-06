using System.Text;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents.Workspace;

namespace CodeSpace.Core.Services.Agents.Harnesses.Claude;

/// <summary>One directory's memory walked to everything it reaches (<see cref="Closure"/>): the files it reads, within the bounds and the build's <see cref="Budget"/>, and the paths their imports name.</summary>
internal static partial class ClaudeWorkspaceMemory
{
    /// <summary>A superset of the CLI's import grammar (<c>(?:^|\s)@((?:[^\s\\]|\\ )+)</c>): no whitespace is required before the <c>@</c>.</summary>
    [GeneratedRegex(@"@((?:[^\s\\]|\\ )+)")]
    private static partial Regex ImportToken();

    /// <summary>One path the closure still has to resolve: <see cref="Origin"/> is the memory entry, relative to the directory, it was reached from.</summary>
    private sealed record Entry(string Path, string Origin, int Hops, bool IsRules);

    /// <summary>
    /// Everything one directory's memory can reach, walked until the first thing that leaves the workspace or cannot be
    /// checked. Fewest hops first, in the order each entry was found, so a file is first reached at its least depth — a
    /// rule a <c>CLAUDE.md</c> also imports is read as the rule it is, with all of its own imports' hops left — and the
    /// notice names the same escape on every build. Every path it resolves and every byte it reads is spent from the
    /// build's <paramref name="budget"/>, which throws once that is spent.
    /// </summary>
    private sealed class Closure(string workspace, string root, Budget budget)
    {
        private readonly PriorityQueue<Entry, (int Hops, int Order)> _pending = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly HashSet<string> _imports = new(StringComparer.Ordinal);
        private int _found;
        private int _scanned;

        /// <summary>The bytes of every file the memory imports, which the CLI loads beside it — known in full once <see cref="FirstEscape"/> found none.</summary>
        public long ImportedBytes { get; private set; }

        /// <summary>Why the directory must be left out, or null when all of its memory stays inside the workspace.</summary>
        public string? FirstEscape()
        {
            foreach (var seed in Seeds(root)) Enqueue(seed);

            while (_pending.TryDequeue(out var entry, out _))
            {
                if (Examine(entry) is { } escape) return escape;
            }

            return null;
        }

        /// <summary>One more path to resolve; why the directory must be left out once that is more than the guard resolves.</summary>
        private string? Enqueue(Entry entry)
        {
            _pending.Enqueue(entry, (entry.Hops, _found++));

            return _found > MaxLookups ? $"its memory names more than {MaxLookups} paths to check" : null;
        }

        /// <summary>The memory the CLI reads from an added directory, as hop 0, and the <c>.claude</c> directory it reads it from.</summary>
        private static IEnumerable<Entry> Seeds(string root) =>
        [
            new(Path.Combine(root, "CLAUDE.md"), "CLAUDE.md", 0, false),
            new(Path.Combine(root, ".claude"), ".claude", 0, false),
            new(Path.Combine(root, ".claude", "CLAUDE.md"), ".claude/CLAUDE.md", 0, false),
            new(Path.Combine(root, ".claude", "rules"), ".claude/rules", 0, true),
        ];

        /// <summary>
        /// Where one entry really is, and whether that leaves the workspace. Nothing there gives the CLI nothing to read,
        /// and neither does a directory an import names, wherever it is (the CLI imports files only), or a file in a
        /// rules folder that is not markdown (the CLI reads only <c>.md</c> rules). Every other entry must resolve inside
        /// the workspace — a directory included, because a rules folder is read entry by entry and what a
        /// <c>.claude</c> directory outside holds is not the workspace's to vouch for, whatever it holds while this looks.
        /// </summary>
        private string? Examine(Entry entry)
        {
            if (budget.Resolve(entry.Path) is not { } physical) return null;

            var isDirectory = Directory.Exists(physical);

            if (entry.Hops > 0 && isDirectory) return null;

            if (entry.IsRules && !isDirectory && !entry.Path.EndsWith(".md", StringComparison.Ordinal)) return null;

            if (!PhysicalPath.StaysInside(workspace, physical)) return Describe(entry, "resolves outside the workspace");

            if (!_seen.Add(physical)) return null;

            return isDirectory ? Enumerate(entry, physical) : Scan(entry, physical);
        }

        /// <summary>A rules directory's entries join the walk under the path the CLI reads them by; <c>.claude</c> itself is read only through its <c>CLAUDE.md</c> and <c>rules</c>, which are seeds of their own.</summary>
        private string? Enumerate(Entry entry, string physical)
        {
            if (!entry.IsRules) return null;

            foreach (var name in ChildNames(physical))
            {
                if (Enqueue(new Entry(Path.Combine(entry.Path, name), $"{entry.Origin}/{name}", entry.Hops, true)) is { } tooMany) return tooMany;
            }

            return null;
        }

        /// <summary>Every entry of a directory, hidden ones too, as the CLI's own listing returns them — one past the bound at most, which is enough to trip it.</summary>
        private static IEnumerable<string> ChildNames(string directory) =>
            new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }).Select(child => child.Name).Take(MaxLookups + 1);

        /// <summary>A file the CLI may read: every import it names joins the walk, until the hop limit, past which the CLI reads nothing.</summary>
        private string? Scan(Entry entry, string physical)
        {
            if (entry.Hops >= MaxImportHops) return null;

            var text = ReadBounded(physical, MaxScannedBytes - _scanned, out var read);

            budget.Read(read);
            _scanned += read;

            if (entry.Hops > 0) ImportedBytes += read;

            if (_scanned > MaxScannedBytes) return $"its memory spans more than {MaxScannedBytes} bytes to check";

            foreach (var target in Imports(text ?? "", Path.GetDirectoryName(physical)!))
            {
                if (_imports.Add(target) && Enqueue(new Entry(target, entry.Origin, entry.Hops + 1, false)) is { } tooMany) return tooMany;
            }

            return null;
        }

        private static string Describe(Entry entry, string what) => entry.Hops == 0 ? $"{Printable(entry.Origin)} {what}" : $"a file {Printable(entry.Origin)} imports {what}";
    }

    /// <summary>
    /// Every path the text @-imports, as the CLI resolves it: a fragment after <c>#</c> dropped, an escaped space
    /// unescaped, an absolute path as written, any other relative to the importing file's own physical directory.
    /// A <c>~</c> import is skipped (see the class remarks), and so is one the platform cannot hold as a path.
    /// </summary>
    private static IEnumerable<string> Imports(string text, string directory)
    {
        foreach (Match match in ImportToken().Matches(text))
        {
            var token = match.Groups[1].Value.Split('#')[0].Replace("\\ ", " ", StringComparison.Ordinal);

            if (token.Length == 0 || token.StartsWith('~') || token.Contains('\0')) continue;

            yield return Path.GetFullPath(Path.IsPathRooted(token) ? token : Path.Combine(directory, token));
        }
    }

    /// <summary>
    /// The text of a regular file, read to its end but no further than <paramref name="budget"/> bytes and one more;
    /// null for anything the CLI would not read either — a FIFO, a device, a file it may not open. <paramref name="read"/>
    /// is how many bytes were read, which passes the budget, and nothing is returned, when the file runs past it.
    /// </summary>
    private static string? ReadBounded(string physical, int budget, out int read)
    {
        read = 0;

        try
        {
            using var stream = new FileStream(LocalAcceptanceFileIdentity.Open(physical, directory: false), FileAccess.Read);
            var buffer = new byte[Math.Min(budget + 1L, stream.Length + 1)];

            read = ReadFully(stream, buffer);

            return read > budget ? null : Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/> on, to its end or the stream's; how many bytes that read.</summary>
    private static int ReadFully(Stream stream, byte[] buffer, int offset = 0)
    {
        var total = offset;

        for (int read; total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0;) total += read;

        return total - offset;
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Stagers;

/// <summary>A per-suite file manifest. Staging verifies the frozen bytes and modes, including on retry; it never falls back to seed content.</summary>
public sealed class HiddenSuiteFixtureStager : IBenchmarkFixtureStager
{
    private sealed record Entry(string Path, bool Directory, string Sha256, int? UnixMode);

    private readonly string _root;
    private readonly IReadOnlyList<Entry> _entries;
    private readonly HashSet<string> _references;

    private HiddenSuiteFixtureStager(string root, IReadOnlyList<Entry> entries, HashSet<string> references)
    {
        _root = root;
        _entries = entries;
        _references = references;
    }

    public static HiddenSuiteFixtureStager Capture(string suiteDirectory, IReadOnlyList<string> references, byte[] tasksBytes, out string contentHash)
    {
        var root = Path.GetFullPath(Path.Combine(suiteDirectory, "fixtures"));
        RejectLink(Path.GetFullPath(suiteDirectory));
        RejectLink(Path.Combine(suiteDirectory, "tasks.json"));
        RejectLink(root);
        var entries = ReadTree(root, "");
        var refs = references.ToHashSet(StringComparer.Ordinal);
        foreach (var reference in refs)
        {
            ValidateReference(reference);
            if (!entries.Any(e => e.Directory && e.Path == reference))
                throw new InvalidOperationException($"Hidden suite fixture '{reference}' is missing.");
        }

        using var manifest = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            format = "hidden-suite-files-v2",
            tasksSha256 = Convert.ToHexStringLower(SHA256.HashData(tasksBytes)),
            entries,
        }));
        contentHash = ContractHashing.Hash(manifest.RootElement);
        return new HiddenSuiteFixtureStager(root, entries, refs);
    }

    public void Stage(string fixtureRef, string directory)
    {
        ValidateReference(fixtureRef);
        if (!_references.Contains(fixtureRef)) throw new InvalidOperationException($"Hidden suite does not declare fixture '{fixtureRef}'.");
        var target = Path.GetFullPath(directory);
        RejectLink(target);
        if (Directory.EnumerateFileSystemEntries(target).Any()) throw new InvalidOperationException("A hidden fixture requires an empty workspace.");

        var expected = _entries.Where(e => e.Path == fixtureRef || e.Path.StartsWith(fixtureRef + "/", StringComparison.Ordinal)).ToArray();
        EnsureSourceParents(fixtureRef);
        var actual = ReadTree(Path.Combine(_root, fixtureRef), fixtureRef, includeRoot: true);
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"Hidden fixture '{fixtureRef}' changed after the suite was frozen.");

        foreach (var entry in expected)
        {
            if (entry.Path == fixtureRef) continue;
            var relative = entry.Path[(fixtureRef.Length + 1)..];
            var destination = Path.Combine(target, relative);
            if (entry.Directory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            EnsureSourceParents(entry.Path);
            var source = Path.Combine(_root, entry.Path);
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                input.CopyTo(output);
                output.Position = 0;
                if (Convert.ToHexStringLower(SHA256.HashData(output)) != entry.Sha256)
                    throw new InvalidOperationException($"Hidden fixture '{fixtureRef}' changed while staging.");
            }
            if (!OperatingSystem.IsWindows() && entry.UnixMode is { } mode) File.SetUnixFileMode(destination, (UnixFileMode)mode);
        }
    }

    private void EnsureSourceParents(string relative)
    {
        RejectLink(Path.GetDirectoryName(_root)!);
        RejectLink(_root);
        var path = _root;
        foreach (var segment in relative.Split('/'))
        {
            path = Path.Combine(path, segment);
            RejectLink(path);
        }
    }

    private static IReadOnlyList<Entry> ReadTree(string root, string prefix, bool includeRoot = false)
    {
        if (!Directory.Exists(root)) throw new InvalidOperationException("Hidden suite fixtures directory is missing.");
        RejectLink(root);
        var entries = new List<Entry>();
        if (includeRoot) entries.Add(new Entry(prefix, true, "", null));
        foreach (var path in Directory.EnumerateFileSystemEntries(root).OrderBy(p => p, StringComparer.Ordinal))
        {
            RejectLink(path);
            var name = Path.GetFileName(path);
            ValidateReference(name);
            var relative = prefix.Length == 0 ? name : prefix + "/" + name;
            if (Directory.Exists(path)) entries.AddRange(ReadTree(path, relative, includeRoot: true));
            else
            {
                using var file = File.OpenRead(path);
                entries.Add(new Entry(relative, false, Convert.ToHexStringLower(SHA256.HashData(file)), OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path)));
            }
        }
        return entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || reference.Contains('\\') || reference.Contains(':') || reference.Split('/').Any(s => s is "" or "." or ".."))
            throw new InvalidOperationException("Hidden fixture references must be portable relative paths without traversal.");
    }

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Hidden suite symbolic links and reparse points are not supported.");
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException("A frozen hidden suite path is missing.", exception);
        }
    }
}

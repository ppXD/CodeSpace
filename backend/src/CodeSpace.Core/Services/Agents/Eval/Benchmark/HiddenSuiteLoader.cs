using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Contracts;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>One loaded hidden suite: the tasks plus the BYTES-level content hash the protocol manifest freezes.</summary>
public sealed record HiddenSuite(IReadOnlyList<BenchmarkTask> Tasks, string SuiteContentHash);

/// <summary>
/// The SEALED-suite mechanism (v4.2 Q contract / NOW-parallel track): loads an evaluation suite from a directory
/// OUTSIDE the repository — sealed qualification content is held by the operator, never by the codebase the
/// implementers and agents can read; the repo ships only this loader. Layout: <c>&lt;dir&gt;/tasks.json</c> (a
/// <see cref="BenchmarkTask"/> array) plus <c>&lt;dir&gt;/fixtures/&lt;fixtureRef&gt;/**</c>. The suite hash covers
/// BYTES — tasks.json and every fixture file's content, path-sorted — so an edited fixture under an unchanged ref
/// can never impersonate the frozen suite (the M1a fixture-content hole, closed for this lane). Fail-loud: a
/// PRESENT-but-broken suite throws; only an ABSENT directory reads null (the lane self-skips). DEFAULT-ON by
/// owner ruling: no env toggle — the suite lives at ONE conventional path outside the repo, and pointing
/// elsewhere is a code change, never a deployment knob.
/// </summary>
public static class HiddenSuiteLoader
{
    /// <summary>THE conventional sealed-suite location — outside every repository checkout, owner-held. Pinned by test: moving it is an explicit, reviewed decision.</summary>
    public static string DefaultSuiteDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codespace", "hidden-suite");

    public static HiddenSuite? LoadFromDefaultLocation() =>
        Directory.Exists(DefaultSuiteDirectory) ? Load(DefaultSuiteDirectory) : null;

    public static HiddenSuite Load(string suiteDirectory)
    {
        var tasksPath = Path.Combine(suiteDirectory, "tasks.json");

        if (!File.Exists(tasksPath))
            throw new InvalidOperationException($"Hidden suite at '{suiteDirectory}' has no tasks.json — a configured suite must be loadable, never silently empty");

        var tasksBytes = File.ReadAllBytes(tasksPath);
        var tasks = JsonSerializer.Deserialize<List<BenchmarkTask>>(tasksBytes, Agents.AgentJson.Options)
                    ?? throw new InvalidOperationException($"Hidden suite tasks.json at '{suiteDirectory}' deserialized to null");

        if (tasks.Count == 0)
            throw new InvalidOperationException($"Hidden suite at '{suiteDirectory}' declares zero tasks — an empty qualification suite is a misconfiguration, not a pass");

        return new HiddenSuite(tasks, HashSuiteBytes(suiteDirectory, tasksBytes));
    }

    /// <summary>Bytes over structure: tasks.json + every fixture file (relative path + content hash), ordinal path order — identical trees hash identically on every OS.</summary>
    private static string HashSuiteBytes(string suiteDirectory, byte[] tasksBytes)
    {
        var entries = new List<(string Path, string Sha256)> { ("tasks.json", Convert.ToHexStringLower(SHA256.HashData(tasksBytes))) };
        var fixturesRoot = Path.Combine(suiteDirectory, "fixtures");

        if (Directory.Exists(fixturesRoot))
            foreach (var file in Directory.EnumerateFiles(fixturesRoot, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(suiteDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
                entries.Add((relative, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))));
            }

        var files = entries.OrderBy(e => e.Path, StringComparer.Ordinal).Select(e => new { path = e.Path, sha256 = e.Sha256 });

        return ContractHashing.Hash(JsonDocument.Parse(JsonSerializer.Serialize(new { files })).RootElement);
    }
}

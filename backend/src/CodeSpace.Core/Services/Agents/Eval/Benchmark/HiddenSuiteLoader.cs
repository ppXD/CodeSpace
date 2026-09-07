using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Stagers;
using System.Text.Json;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>A frozen task list, file-manifest digest, and the per-suite source used for both initial staging and retry.</summary>
public sealed record HiddenSuite(IReadOnlyList<BenchmarkTask> Tasks, string SuiteContentHash, IBenchmarkFixtureStager FixtureStager);

/// <summary>
/// The SEALED-suite mechanism (v4.2 Q contract / NOW-parallel track): loads an evaluation suite from a directory
/// OUTSIDE the repository — sealed qualification content is held by the operator, never by the codebase the
/// implementers and agents can read; the repo ships only this loader. Layout: <c>&lt;dir&gt;/tasks.json</c> (a
/// <see cref="BenchmarkTask"/> array) plus <c>&lt;dir&gt;/fixtures/&lt;fixtureRef&gt;/**</c>. The suite hash covers
/// BYTES, file modes and directories — path-sorted — so an edited fixture under an unchanged ref
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

        if ((File.GetAttributes(suiteDirectory) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(tasksPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Hidden suite directories and task definitions cannot be symbolic links.");

        var tasksBytes = File.ReadAllBytes(tasksPath);
        var tasks = JsonSerializer.Deserialize<List<BenchmarkTask>>(tasksBytes, Agents.AgentJson.Options)
                    ?? throw new InvalidOperationException($"Hidden suite tasks.json at '{suiteDirectory}' deserialized to null");

        if (tasks.Count == 0)
            throw new InvalidOperationException($"Hidden suite at '{suiteDirectory}' declares zero tasks — an empty qualification suite is a misconfiguration, not a pass");

        var stager = HiddenSuiteFixtureStager.Capture(suiteDirectory, tasks.Select(t => t.FixtureRef).ToArray(), tasksBytes, out var hash);
        var frozenTasks = tasks.Select(t => t with { Modes = Array.AsReadOnly(t.Modes.ToArray()), TestCommand = Array.AsReadOnly(t.TestCommand.ToArray()) }).ToList().AsReadOnly();
        return new HiddenSuite(frozenTasks, hash, stager);
    }
}

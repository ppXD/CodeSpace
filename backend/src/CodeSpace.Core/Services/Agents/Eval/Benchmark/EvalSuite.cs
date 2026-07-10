using System.Security.Cryptography;
using System.Text;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// M1a — the pure suite-identity + fixed-denominator scorer over a benchmark corpus (the behavior half of
/// <see cref="EvalSuiteManifest"/>/<see cref="CorpusCellOutcome"/>, Rule 18.1). Three invariants, each pinned:
/// (1) the manifest VERSION is content-derived and order-independent — any semantic change to any task (id,
/// fixture ref, oracle, test command, goal, description, modes) is a NEW suite, so a percentage claim always
/// names exactly what it measured; (2) the cell universe is the FIXED DENOMINATOR — every (task × mode) pair is
/// classified, and a pair whose plumbing failed is <see cref="CorpusCellState.InfraUnknown"/>, never silently
/// dropped from the divisor; (3) each cell is <b>@1</b> — the FIRST authorized attempt's outcome, no best-of-N,
/// no retry, no seed-picking (the corpus loop dispatches every cell exactly once).
/// </summary>
public static class EvalSuite
{
    private const char Sep = (char)0x1F;   // unit separator — unambiguous canonical-field delimiter, unprintable in any task text

    private const char Rec = (char)0x1E;   // record separator — closes each task record

    /// <summary>The version prefix naming the canonicalization algorithm — bumped on ANY canonical-form change (v2: length-prefixed fields kill the field-migration collision the v1 flat join allowed; TimeoutSeconds joined the fields — a timeout change flips TimedOut↔Solved, i.e. it changes the measurement protocol, so it IS suite identity).</summary>
    public const string VersionAlgorithm = "sha256/corpus-v2";

    /// <summary>The immutable manifest for a corpus: content-derived version + the canonical cell universe (sorted by task id then mode, so authoring order never changes identity). FAIL-LOUD on a duplicate (task, mode) cell — two cells under one identity would alias every id-keyed read (the same strict-identity bar H2 set for plan subtask ids), and <see cref="Classify"/> keys on exactly that identity.</summary>
    public static EvalSuiteManifest ManifestFor(IReadOnlyList<BenchmarkTask> corpus)
    {
        var duplicateId = corpus.GroupBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicateId is not null) throw new ArgumentException($"the corpus declares task id '{duplicateId.Key}' more than once — BenchmarkTask.Id must be unique within a corpus (two cells under one identity would alias every id-keyed read)");

        var duplicateMode = corpus.FirstOrDefault(t => t.Modes.GroupBy(m => m).Any(g => g.Count() > 1));
        if (duplicateMode is not null) throw new ArgumentException($"task '{duplicateMode.Id}' declares the same mode more than once — a (task, mode) cell is an identity");

        var canonical = new StringBuilder();

        foreach (var task in corpus.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            // Length-prefixed fields (v2): a flat join let a value migrate across field boundaries (e.g. a
            // TestCommand element vs the Goal) and two semantically DIFFERENT suites collide on one version —
            // the exact lie the version exists to prevent. The length prefix makes every field self-delimiting.
            AppendField(canonical, task.Id);
            AppendField(canonical, task.FixtureRef);
            AppendField(canonical, task.Harness);
            AppendField(canonical, task.Grading.ToString());
            AppendField(canonical, task.TimeoutSeconds.ToString());
            AppendList(canonical, task.TestCommand);
            AppendField(canonical, task.Goal);
            AppendField(canonical, task.Description);
            AppendList(canonical, task.Modes.OrderBy(m => m).Select(m => m.ToString()));
            canonical.Append(Rec);
        }

        var version = $"{VersionAlgorithm}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..16]}";

        var cells = corpus
            .SelectMany(t => t.Modes.Select(m => new CorpusCellRef { TaskId = t.Id, Mode = m }))
            .OrderBy(c => c.TaskId, StringComparer.Ordinal).ThenBy(c => c.Mode)
            .ToList();

        return new EvalSuiteManifest { Version = version, Cells = cells };
    }

    /// <summary>
    /// Classify EVERY manifest cell into the four-state vocabulary: a graded result → Solved/Unsolved off the
    /// oracle's own verdict; an errored pair → InfraUnknown carrying the infra error; a cell with NEITHER → InfraUnknown too
    /// (defense-in-depth — the shipped runner always records one or the other; a future runner that skips a cell
    /// must still see it occupy its slot in the fixed denominator). Abstained has no producer yet (M3's
    /// impossible-task tier feeds it); the state exists so the vocabulary is stable when it arrives.
    /// </summary>
    public static IReadOnlyList<CorpusCellOutcome> Classify(EvalSuiteManifest manifest, IReadOnlyList<BenchmarkResult> results, IReadOnlyList<CorpusBenchmarkError> errored)
    {
        // FAIL-LOUD identity guards: a duplicate result for one cell or a result/error that maps to NO manifest
        // cell means the runner's identity plumbing corrupted — the numbers would be garbage, and garbage reported
        // as a percentage is worse than a lost run. (Unreachable from the shipped loop, which dispatches each
        // manifest cell exactly once — defense-in-depth for future runners.)
        var universe = manifest.Cells.Select(c => (c.TaskId, c.Mode)).ToHashSet();

        var duplicate = results.GroupBy(r => (r.TaskId, r.Mode)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"cell ({duplicate.Key.TaskId}, {duplicate.Key.Mode}) carries {duplicate.Count()} results — each cell is @1, exactly one attempt");

        var orphan = results.Select(r => (r.TaskId, r.Mode)).Concat(errored.Select(e => (e.TaskId, e.Mode))).FirstOrDefault(k => !universe.Contains(k));
        if (orphan != default) throw new ArgumentException($"result/error for ({orphan.TaskId}, {orphan.Mode}) maps to no suite cell — runner identity corruption");

        var resultByCell = results.ToDictionary(r => (r.TaskId, r.Mode));
        var errorByCell = errored.ToDictionary(e => (e.TaskId, e.Mode));

        return manifest.Cells.Select(cell =>
        {
            if (resultByCell.TryGetValue((cell.TaskId, cell.Mode), out var result))
                return new CorpusCellOutcome
                {
                    TaskId = cell.TaskId, Mode = cell.Mode,
                    State = result.Grade.Passed ? CorpusCellState.Solved : CorpusCellState.Unsolved,
                    Detail = result.Grade.Detail,
                };

            return new CorpusCellOutcome
            {
                TaskId = cell.TaskId, Mode = cell.Mode,
                State = CorpusCellState.InfraUnknown,
                Detail = errorByCell.TryGetValue((cell.TaskId, cell.Mode), out var error) ? error.Error : "the corpus loop never reached this cell",
            };
        }).ToList();
    }

    private static void AppendField(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(Sep);

    private static void AppendList(StringBuilder builder, IEnumerable<string> values)
    {
        var list = values.ToList();
        builder.Append(list.Count).Append(';');
        foreach (var v in list) AppendField(builder, v);
    }

    /// <summary>The fixed-denominator reduction — pure counting over the classified cells.</summary>
    public static CorpusCellScore Score(IReadOnlyList<CorpusCellOutcome> cells) => new()
    {
        Solved = cells.Count(c => c.State == CorpusCellState.Solved),
        Unsolved = cells.Count(c => c.State == CorpusCellState.Unsolved),
        Abstained = cells.Count(c => c.State == CorpusCellState.Abstained),
        InfraUnknown = cells.Count(c => c.State == CorpusCellState.InfraUnknown),
    };
}

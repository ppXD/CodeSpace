using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Fail-closed floor for the lane that forgets the producing run's posture. Only a REQUEST overload of
/// <see cref="ISupervisorAcceptanceGrader"/> carries an <see cref="AcceptanceGradingPosture"/>, and the request records
/// make it <c>required</c>. The positional overloads carry none, and the real grader runs them with network off. That
/// is safe, but it is not the producing run's posture, so a Trusted run's setup could no longer download. So every
/// production call site grades through a request.
///
/// <para>The per-file COUNTS are pinned as well. A new grade lane must change this list, and the reviewer then asks
/// whose posture it passes. The lanes' own tests pin that the posture each one passes is the right producer's.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class AcceptanceGradePostureInventoryTests
{
    private const string ScannedRoot = "backend/src";

    /// <summary>Where the grader's members are declared and implemented. Its positional overloads forward to its request overloads there, with no posture: that is their documented fail-closed meaning, not a lane.</summary>
    private static readonly string[] Declarations =
    [
        "CodeSpace.Core/Services/Supervisor/ISupervisorAcceptanceGrader.cs",
        "CodeSpace.Core/Services/Supervisor/SupervisorAcceptanceGrader.cs",
    ];

    /// <summary>Every production file that grades an acceptance contract, with how many grade calls it makes, all of them through a posture-carrying request.</summary>
    private static readonly IReadOnlyDictionary<string, int> GradeLanes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["CodeSpace.Core/Services/Agents/AgentRunExecutor.cs"] = 3,                          // branch, patch, multi-repo
        ["CodeSpace.Core/Services/Agents/Workspace/LocalAcceptanceVerifier.cs"] = 1,         // the repo-less live workspace
        ["CodeSpace.Core/Services/Supervisor/SupervisorTurnService.Rehydrate.cs"] = 9,       // resolve branch + patch, unit branch + patch + captured + base + multi-repo, branchless stop, stop targets
    };

    /// <summary>A grade-named call whose receiver is NOT the acceptance grader: the executor's hand-off to the local verifier, which derives the posture itself.</summary>
    private const string LocalVerifierHandOff = "GetRequiredService<LocalAcceptanceVerifier>().GradeAsync(";

    private static readonly Regex GradeCall = new(@"\.(GradeAsync|GradePatchAsync|GradeBaseAsync|GradeCapturedAsync|GradeDirectoryAsync)\((?<argument>[^)]{0,80})", RegexOptions.Compiled);

    private static readonly Regex PostureRequest = new(@"^new (Repository|Patch|Base|Captured|Directory)AcceptanceGradeRequest\b", RegexOptions.Compiled);

    [Fact]
    public void Every_production_grade_passes_a_posture_carrying_request()
    {
        var calls = GradeCalls();

        calls.ShouldNotBeEmpty("the scan found no grade call at all — every check here would pass vacuously");

        var offenders = calls.Where(call => !PostureRequest.IsMatch(call.Argument)).Select(call => $"{call.File}:{call.Line} — {call.Argument}").ToList();

        offenders.ShouldBeEmpty(
            "these grade an acceptance contract through a POSITIONAL overload, which carries no producing-run posture " +
            "and so grades with network off whatever the run had. Pass a request record with Posture = " +
            $"{nameof(AcceptanceGradingPosturePolicy)}.{nameof(AcceptanceGradingPosturePolicy.For)}(the producing run's task):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_set_of_grade_lanes_is_pinned()
    {
        var found = GradeCalls().GroupBy(call => call.File).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        found.OrderBy(pair => pair.Key, StringComparer.Ordinal).ShouldBe(GradeLanes.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            customMessage: "the acceptance grade lanes changed. A new lane belongs in GradeLanes, after its test proves it passes the posture " +
                           "of the run whose bytes it executes (not the posture of whoever happens to call it).");
    }

    /// <summary>
    /// Every production file that hands an oracle a runner, with how many grading contexts it builds. The benchmark and
    /// qualification instruments never go through <see cref="ISupervisorAcceptanceGrader"/>, so the scan above cannot
    /// see them, yet they run the same agent-written bytes: each one binds its runner to a posture.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> GradingContextLanes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["CodeSpace.Core/Services/Agents/Eval/Benchmark/BenchmarkTaskGrading.cs"] = 1,      // BenchmarkRunner + TaskLaunchBenchmarkCellRunner
        ["CodeSpace.Core/Services/Supervisor/SupervisorAcceptanceGrader.cs"] = 1,            // every acceptance lane above
    };

    private const string GradingContextDeclaration = "CodeSpace.Core/Services/Agents/Eval/Benchmark/IBenchmarkGrader.cs";

    private static readonly Regex GradingContextConstruction = new(@"new BenchmarkGradingContext\b|BenchmarkGradingContext\.For(Acceptance|Command)\(", RegexOptions.Compiled);

    [Fact]
    public void Every_oracle_runner_is_bound_to_a_producing_run_posture()
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        var unbound = new List<string>();

        foreach (var (relative, text) in ProductionSources())
        {
            if (relative == GradingContextDeclaration) continue;

            var count = text.Split('\n').Count(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && !line.TrimStart().StartsWith('*') && GradingContextConstruction.IsMatch(line));

            if (count == 0) continue;

            found[relative] = count;

            if (!text.Contains($"{nameof(AcceptanceGradingPosturePolicy)}.{nameof(AcceptanceGradingPosturePolicy.Bind)}(", StringComparison.Ordinal)) unbound.Add(relative);
        }

        found.OrderBy(pair => pair.Key, StringComparer.Ordinal).ShouldBe(GradingContextLanes.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            customMessage: "the places that hand an oracle a runner changed. A new one belongs in GradingContextLanes, after its test proves the runner it hands over is bound to the producing run's posture.");
        unbound.ShouldBeEmpty($"these hand an oracle a raw runner, so agent-written code they execute runs with no ceilings; bind it with {nameof(AcceptanceGradingPosturePolicy)}.{nameof(AcceptanceGradingPosturePolicy.Bind)}");
    }

    private static IEnumerable<(string Relative, string Text)> ProductionSources()
    {
        var root = Path.Combine(RepositoryRoot(), ScannedRoot);

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Select(file => (Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(file)));
    }

    /// <summary>Every grade call in a production file that holds the acceptance grader, minus the declarations and the local-verifier hand-off. Comment lines are text about a call, not a call.</summary>
    private static List<(string File, int Line, string Argument)> GradeCalls()
    {
        var calls = new List<(string, int, string)>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), ScannedRoot), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Path.Combine(RepositoryRoot(), ScannedRoot), file).Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(file);

            if (Declarations.Contains(relative) || !HoldsTheGrader(text)) continue;

            var lines = text.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();

                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*') || line.Contains(LocalVerifierHandOff, StringComparison.Ordinal)) continue;

                foreach (Match match in GradeCall.Matches(line)) calls.Add((relative, i + 1, match.Groups["argument"].Value.TrimStart()));
            }
        }

        return calls;
    }

    private static bool HoldsTheGrader(string text) => text.Contains(nameof(ISupervisorAcceptanceGrader), StringComparison.Ordinal) || text.Contains("_acceptanceGrader.", StringComparison.Ordinal);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException($"repository root not found walking up from {AppContext.BaseDirectory}");
    }
}

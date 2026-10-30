using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Fail-closed floor for the shape that was pasted five times: the rule for which acceptance argvs can be run at all —
/// a blank first element or a NUL anywhere makes the whole argv unrunnable, and every other element is kept. One
/// statement of it (<see cref="AcceptanceArgv.NormalizeOrNull"/>) is only one if nothing else restates it: a private
/// copy that drifts reads the operator's floor differently from the code that runs it, so the launch bakes a command
/// the grade refuses, or the grade runs one the prompt never showed. <c>AcceptanceArgvTests</c> pins what the helper
/// decides; it cannot see a caller that stopped asking it, which is what this pins.
///
/// <para>Two halves. Every reader that decides whether an argv can run — the two launch projections, the supervisor
/// rehydrate and stop grade, the in-loop Stop hook, the local verifier, the two spec-preview validators — must ask the
/// helper, at every site: each file pins how many times it does, because the rehydrate has four and one of them
/// reverting to a private copy leaves the file still asking the helper three times. And no other file under
/// <c>backend/src</c>, in any assembly, may spell the rule out itself, unless it is named in
/// <see cref="StatesTheRuleByDesign"/> with the reason: each entry pins a COUNT as well as a reason, so a new copy in
/// an already-named file is as loud as one in a new file.</para>
///
/// <para>The scan is a text heuristic, not a parser, in the shape of <see cref="ScopedTransactionInventoryTests"/>,
/// and it matches per FILE, not per line: a file whose code tests a first element for blankness
/// (<c>IsNullOrWhiteSpace(x[0])</c>) AND tests for a NUL (<c>Contains('\0')</c>, <c>IndexOf('\0')</c>) states the rule,
/// wherever the two clauses sit. A per-line match missed exactly that: <c>PlannerAcceptanceDraft</c> writes the null/NUL
/// clause on one line and the blank-first clause on the next. A copy spelled some other way still slips past the second
/// half and is caught by the first, since the site that holds it no longer asks the helper.</para>
/// </summary>
[Trait("Category", "Unit")]
public class AcceptanceArgvInventoryTests
{
    private const string ScannedRoot = "backend/src";
    private const string Helper = "AcceptanceArgv.NormalizeOrNull(";

    /// <summary>A first element tested for blankness — the rule's program clause. Any identifier or member path is the argv, so <c>command[0]</c> and <c>spec.Command[0]</c> both match.</summary>
    private static readonly Regex BlankFirst = new(@"IsNullOrWhiteSpace\(\s*[\w.!?]+\s*\[0\]\s*\)", RegexOptions.Compiled);

    /// <summary>A NUL tested for — the rule's token clause, in either literal form and either method the repo writes it with.</summary>
    private static readonly Regex NulTest = new(@"(Contains|IndexOf)\(\s*['""]\\0['""]", RegexOptions.Compiled);

    /// <summary>The readers that decide whether an acceptance argv can run, each through <see cref="AcceptanceArgv"/> and never a copy of it, with how many sites ask it: the rehydrate's are the operator floor's resolve grade and oracle inventory, a unit's own check, and the stop gate's floor and model check.</summary>
    private static readonly IReadOnlyDictionary<string, int> AsksTheHelper = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["CodeSpace.Core/Services/Agents/InLoopAcceptanceHook.cs"] = 1,
        ["CodeSpace.Core/Services/Agents/Workspace/LocalAcceptanceVerifier.cs"] = 1,
        ["CodeSpace.Core/Services/Supervisor/SupervisorTurnService.Rehydrate.cs"] = 4,
        ["CodeSpace.Core/Services/Tasks/Projection/Builders/SingleAgent/SingleAgentDefinitionBuilder.cs"] = 1,
        ["CodeSpace.Core/Services/Tasks/Projection/Builders/Supervisor/SupervisorDefinitionBuilder.cs"] = 1,
        ["CodeSpace.Core/Services/Tasks/SpecPreview/TaskSpecAcceptanceAssessment.cs"] = 1,
        ["CodeSpace.Core/Services/Tasks/SpecPreview/TaskSpecSourceArgv.cs"] = 1,
    };

    /// <summary>The files that still spell the rule out, with how many lines hold its blank-first clause. The helper is the one statement; the planner's payload check is the one reader that cannot ask it.</summary>
    private static readonly IReadOnlyDictionary<string, (int Lines, string Reason)> StatesTheRuleByDesign = new Dictionary<string, (int, string)>(StringComparer.Ordinal)
    {
        ["CodeSpace.Core/Services/Agents/AcceptanceArgv.cs"] = (1, "the one statement of the rule."),
        ["CodeSpace.Core/Services/Workflows/Planning/PlannerAcceptanceDraft.cs"] = (1, "checks a planner-authored payload and must tell the re-ask WHICH clause failed — a null or NUL element and a blank executable are two separate messages, one per violated clause — and the same method checks file-oracle paths, where ANY blank is wrong. The helper answers only runnable or not, so folding it would make both messages one."),
    };

    private sealed record SourceFile(string Name, IReadOnlyList<(string Text, int Number)> Code);

    private static readonly Lazy<IReadOnlyList<SourceFile>> Sources = new(ReadSources);

    [Fact]
    public void Every_reader_that_decides_an_argv_can_run_asks_the_helper_at_every_site()
    {
        var asked = Sources.Value
            .Select(file => (file.Name, Calls: file.Code.Sum(line => line.Text.Split(Helper).Length - 1)))
            .Where(file => file.Calls > 0)
            .ToDictionary(file => file.Name, file => file.Calls, StringComparer.Ordinal);

        asked.ShouldNotBeEmpty("the source scan found no caller of the helper at all — every check here would pass vacuously");

        var drift = AsksTheHelper.Keys.Union(asked.Keys)
            .Where(file => asked.GetValueOrDefault(file) != AsksTheHelper.GetValueOrDefault(file))
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(file => $"{file} — expected {AsksTheHelper.GetValueOrDefault(file)} site(s) asking the helper, found {asked.GetValueOrDefault(file)}")
            .ToList();

        drift.ShouldBeEmpty(
            $"the sites asking {nameof(AcceptanceArgv)}.{nameof(AcceptanceArgv.NormalizeOrNull)} changed. A new reader or site belongs in {nameof(AsksTheHelper)}; " +
            "a LOST one is a private copy of the rule again, which can drift from the helper unseen — ask the helper instead:\n  " + string.Join("\n  ", drift));
    }

    [Fact]
    public void No_other_file_states_the_rule_again()
    {
        var found = RuleStatements();

        found.ShouldNotBeEmpty("the source scan found no statement of the rule at all — every check here would pass vacuously");

        var offenders = found
            .Where(file => !StatesTheRuleByDesign.ContainsKey(file.Key) || StatesTheRuleByDesign[file.Key].Lines != file.Value.Count)
            .Select(file => $"{file.Key}:{string.Join(",", file.Value)} — {(StatesTheRuleByDesign.TryGetValue(file.Key, out var e) ? $"named for {e.Lines}, found {file.Value.Count}" : "not named")}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            $"these spell out which acceptance argvs can run — a blank first element and a NUL — instead of asking {nameof(AcceptanceArgv)}.{nameof(AcceptanceArgv.NormalizeOrNull)}. " +
            $"A copy drifts: ask the helper, or name the file in {nameof(StatesTheRuleByDesign)} with the reason it must restate the rule:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_named_files_do_not_rot()
    {
        var found = RuleStatements();

        foreach (var (file, entry) in StatesTheRuleByDesign)
        {
            found.ShouldContainKey(file, $"named file '{file}' no longer states the rule — remove it from {nameof(StatesTheRuleByDesign)}");
            entry.Reason.ShouldNotBeNullOrWhiteSpace($"named file '{file}' must say why it restates the rule");
        }
    }

    /// <summary>Every file under the scanned root whose code tests a NUL AND a first element's blankness, with the lines that hold the blank-first clause. Per file, so a copy split across lines is still one statement of the rule. Comment lines are prose about the rule, not a statement of it.</summary>
    private static Dictionary<string, List<int>> RuleStatements()
    {
        var sites = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var file in Sources.Value.Where(file => file.Code.Any(line => NulTest.IsMatch(line.Text))))
        {
            var lines = file.Code.Where(line => BlankFirst.IsMatch(line.Text)).Select(line => line.Number).ToList();

            if (lines.Count > 0) sites[file.Name] = lines;
        }

        return sites;
    }

    private static IReadOnlyList<SourceFile> ReadSources()
    {
        var root = Path.Combine(RepositoryRoot(), ScannedRoot);

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(file => (Path: file, Name: Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(file => !file.Name.Split('/').Any(segment => segment is "obj" or "bin"))
            .Select(file => new SourceFile(file.Name, CodeLines(file.Path)))
            .ToList();
    }

    private static IReadOnlyList<(string Text, int Number)> CodeLines(string file) => File.ReadAllLines(file)
        .Select((text, index) => (Text: text.TrimStart(), Number: index + 1))
        .Where(line => !line.Text.StartsWith("//", StringComparison.Ordinal) && !line.Text.StartsWith('*'))
        .ToList();

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException($"repository root not found walking up from {AppContext.BaseDirectory}");
    }
}

using System.Text.Json;
using System.Xml.Linq;
using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

internal static class StopHookQualificationCensus
{
    internal static readonly IReadOnlyDictionary<string, string> Arms = new Dictionary<string, string>
    {
        ["claude"] = typeof(RealModelStopHookE2ETests).FullName + "." + nameof(RealModelStopHookE2ETests.A_real_claude_agent_reacts_to_the_stop_hooks_feedback_and_creates_the_missing_file),
        ["codex"] = typeof(RealModelCodexStopHookE2ETests).FullName + "." + nameof(RealModelCodexStopHookE2ETests.A_real_codex_agent_reacts_to_the_stop_hooks_feedback_and_creates_the_missing_file),
    };

    internal static IReadOnlyList<StopHookArmCensus> Read(string directory, string trxPath)
    {
        var tests = XDocument.Load(trxPath).Descendants().Where(e => e.Name.LocalName == "UnitTestResult").ToArray();
        var census = new List<StopHookArmCensus>();
        foreach (var (arm, testName) in Arms)
        {
            var test = tests.Where(e => (string?)e.Attribute("testName") == testName).ShouldHaveSingleItem($"the stop-hook census requires exactly one {arm} test result");
            var outcome = (string?)test.Attribute("outcome");
            outcome.ShouldBeOneOf("Passed", "Failed", "NotExecuted");
            var record = JsonSerializer.Deserialize<StopHookExecutionRecord>(File.ReadAllText(Path.Combine(directory, arm + ".json")), AgentJson.Options);
            record.ShouldNotBeNull();
            record.SchemaVersion.ShouldBe(1);
            record.Arm.ShouldBe(arm);
            if (outcome == "Passed")
            {
                record.ModelObserved.ShouldBeTrue($"{arm}: xUnit Passed without native CLI and model evidence is not a measured qualification result");
                record.BehavioralPassed.ShouldNotBeNull($"{arm}: a Passed test must carry a behavioral verdict, including an informational miss");
            }

            census.Add(new StopHookArmCensus(arm, outcome!, record.Measurement, record.BehavioralPassed, outcome == "Passed" && record.QualificationSucceeded));
        }

        return census;
    }
}

internal sealed record StopHookArmCensus(string Arm, string TestOutcome, string Measurement, bool? BehavioralPassed, bool QualificationSucceeded);

/// <summary>Run separately after the two live arms so xUnit success cannot substitute for actual measurement.</summary>
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class StopHookQualificationCensusE2ETests
{
    [SkippableFact]
    public void The_stop_hook_results_distinguish_execution_from_report_only_behavior()
    {
        var directory = Environment.GetEnvironmentVariable(StopHookExecutionEvidence.DirectoryEnvVar);
        if (string.IsNullOrWhiteSpace(directory)) throw new Xunit.SkipException("the stop-hook census is only evaluated after its dedicated real-model job has written both arms");
        var results = Path.GetDirectoryName(Path.GetFullPath(directory))!;
        var census = StopHookQualificationCensus.Read(directory, Path.Combine(results, "real-model-stop-hook.trx"));
        File.WriteAllText(Path.Combine(results, "stop-hook-census.json"), JsonSerializer.Serialize(census, AgentJson.Options));
        var lines = new List<string> { "Stop-hook execution census — report-only and skipped arms are not qualification success.", "", "| arm | xUnit | measurement | behavioral verdict | qualification succeeded |", "|---|---|---|---|---|" };
        lines.AddRange(census.Select(r => $"| {r.Arm} | {r.TestOutcome} | {r.Measurement} | {r.BehavioralPassed?.ToString() ?? "not measured"} | {r.QualificationSucceeded} |"));
        var report = string.Join('\n', lines) + "\n";
        Console.WriteLine(report);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary) File.AppendAllText(summary, report);
    }
}

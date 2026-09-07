using System.Text.Json;
using System.Xml.Linq;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class StopHookQualificationCensusTests
{
    [Fact]
    public void The_frozen_false_green_TRX_shape_with_two_pre_admission_faults_is_rejected()
    {
        WithResults("Passed", new StopHookExecutionRecord { Arm = "claude" }, directory => Should.Throw<ShouldAssertException>(() => StopHookQualificationCensus.Read(directory, Path.Combine(directory, "results.trx"))));
    }

    [Fact]
    public void A_serialized_success_claim_cannot_override_missing_execution_evidence()
    {
        WithResults("Passed", new StopHookExecutionRecord { Arm = "claude" }, directory =>
        {
            var path = Path.Combine(directory, "claude.json");
            var json = File.ReadAllText(path).Replace("\"modelObserved\":false", "\"modelObserved\":true", StringComparison.Ordinal).Replace("\"qualificationSucceeded\":false", "\"qualificationSucceeded\":true", StringComparison.Ordinal);
            json.ShouldContain("\"modelObserved\":true");
            json.ShouldContain("\"qualificationSucceeded\":true");
            File.WriteAllText(path, json);
            Should.Throw<ShouldAssertException>(() => StopHookQualificationCensus.Read(directory, Path.Combine(directory, "results.trx")));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_observed_model_measures_both_arms_but_only_a_behavior_pass_qualifies(bool behavior)
    {
        WithResults("Passed", new StopHookExecutionRecord { Arm = "claude", RunId = Guid.NewGuid(), NativeSessionEvents = 1, InputTokens = 5, OutputTokens = 7, BehavioralPassed = behavior }, directory =>
        {
            var rows = StopHookQualificationCensus.Read(directory, Path.Combine(directory, "results.trx"));
            rows.Count.ShouldBe(2);
            rows.ShouldAllBe(r => r.Measurement == "measured" && r.QualificationSucceeded == behavior);
        });
    }

    [Theory]
    [InlineData("NotExecuted")]
    [InlineData("Failed")]
    public void Skipped_or_failed_arms_preserve_their_result_and_never_qualify(string outcome)
    {
        WithResults(outcome, new StopHookExecutionRecord { Arm = "claude" }, directory =>
        {
            var rows = StopHookQualificationCensus.Read(directory, Path.Combine(directory, "results.trx"));
            rows.ShouldAllBe(r => r.TestOutcome == outcome && !r.QualificationSucceeded);
        });
    }

    /// <summary>
    /// A CLI-observed run whose own recorded failure is gateway/transport infra (<see cref="StopHookExecutionEvidence.AssessAsync"/>
    /// routes it to a non-gating skip, landing the TRX as NotExecuted) must read from the census as neither a pass NOR
    /// a behavioral miss — <see cref="StopHookArmCensus.BehavioralPassed"/> stays null (nothing was measured), never
    /// <c>false</c> (which would misread an outage as "the model tried and failed").
    /// </summary>
    [Fact]
    public void An_infra_fault_record_counts_as_neither_a_pass_nor_a_behavioral_miss()
    {
        WithResults("NotExecuted", new StopHookExecutionRecord { Arm = "claude", RunId = Guid.NewGuid(), NativeSessionEvents = 1, RecordedFailureKind = FailureKind.Unavailable, FailureDetail = "exceeded retry limit, last status: 429 Too Many Requests" }, directory =>
        {
            var rows = StopHookQualificationCensus.Read(directory, Path.Combine(directory, "results.trx"));
            rows.ShouldAllBe(r => r.Measurement == "infra-fault" && r.BehavioralPassed == null && !r.QualificationSucceeded);
        });
    }

    [Fact]
    public void A_missing_Codex_arm_cannot_hide_behind_a_passing_Claude_arm()
    {
        WithResults("Passed", new StopHookExecutionRecord { Arm = "claude", RunId = Guid.NewGuid(), NativeSessionEvents = 1, InputTokens = 5, OutputTokens = 7, BehavioralPassed = true }, directory =>
        {
            var path = Path.Combine(directory, "results.trx");
            var trx = XDocument.Load(path);
            trx.Descendants("UnitTestResult").Last().Remove();
            trx.Save(path);
            Should.Throw<ShouldAssertException>(() => StopHookQualificationCensus.Read(directory, path));
        });
    }

    [Fact]
    public void The_stop_hook_job_collects_the_execution_census_even_when_a_live_arm_fails()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, ".github", "workflows", "real-model.yml"))) root = root.Parent;
        root.ShouldNotBeNull();
        var workflow = File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "real-model.yml"));
        var job = workflow[workflow.IndexOf("  real-model-stop-hook:", StringComparison.Ordinal)..];
        job.ShouldContain("FullyQualifiedName~StopHookQualificationCensusE2ETests");
        job.ShouldContain("CODESPACE_STOP_HOOK_EVIDENCE_DIRECTORY:");
        job.Contains("bash .github/scripts/assert-every-filter-clause-ran.sh", StringComparison.Ordinal).ShouldBeTrue("the existing consecutive-dark-run policy must remain in force");
        var census = job[job.IndexOf("- name: Census the real stop-hook executions", StringComparison.Ordinal)..];
        census[..census.IndexOf("run:", StringComparison.Ordinal)].ShouldContain("if: always()");
    }

    private static void WithResults(string outcome, StopHookExecutionRecord record, Action<string> verify)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-stop-hook-census-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var arm in StopHookQualificationCensus.Arms.Keys) File.WriteAllText(Path.Combine(directory, arm + ".json"), JsonSerializer.Serialize(record with { Arm = arm }, AgentJson.Options));
            new XDocument(new XElement("TestRun", new XElement("Results", StopHookQualificationCensus.Arms.Values.Select(name => new XElement("UnitTestResult", new XAttribute("testName", name), new XAttribute("outcome", outcome)))))).Save(Path.Combine(directory, "results.trx"));
            verify(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class StopHookExecutionEvidenceTests
{
    [Fact]
    public async Task A_report_only_pre_admission_fault_is_not_a_successful_execution_measurement()
    {
        using var evidence = new StopHookExecutionEvidence("claude", directory: "");
        await Assert.ThrowsAsync<ShouldAssertException>(() => evidence.AssessAsync(() => throw new InvalidOperationException("pre-admission refusal")));
        evidence.Record.RunId.ShouldBeNull();
        evidence.Record.Measurement.ShouldBe("not-admitted");
        evidence.Record.QualificationSucceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task A_succeeded_row_without_native_CLI_output_does_not_qualify()
    {
        using var evidence = new StopHookExecutionEvidence("claude", directory: "");
        Capture(evidence, "claude", 5, 7, NativeEvidence.Missing);
        await Assert.ThrowsAsync<ShouldAssertException>(() => evidence.AssessAsync(() => Task.FromResult((true, "file present"))));
        evidence.Record.Measurement.ShouldBe("no-native-cli-evidence");
        evidence.Record.QualificationSucceeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(-1, 5)]
    public async Task A_native_CLI_session_without_valid_positive_model_output_is_not_a_measured_pass(int? input, int? output)
    {
        using var evidence = new StopHookExecutionEvidence("codex", directory: "");
        Capture(evidence, "codex", input, output);
        await Assert.ThrowsAsync<ShouldAssertException>(() => evidence.AssessAsync(() => Task.FromResult((true, "file present"))));
        evidence.Record.CliObserved.ShouldBeTrue();
        evidence.Record.ModelObserved.ShouldBeFalse();
        evidence.Record.QualificationSucceeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("claude", false)]
    [InlineData("codex", true)]
    [InlineData("codex", false)]
    public async Task Native_frames_and_positive_usage_measure_the_arm_without_promoting_report_only_behavior(string arm, bool created)
    {
        using var evidence = new StopHookExecutionEvidence(arm, directory: "");
        Capture(evidence, arm, 5, 7);
        await evidence.AssessAsync(() => Task.FromResult((created, "behavior verdict")));
        evidence.Record.ModelObserved.ShouldBeTrue();
        evidence.Record.BehavioralPassed.ShouldBe(created);
        evidence.Record.QualificationSucceeded.ShouldBe(created);
    }

    [Fact]
    public async Task An_existing_wire_infra_skip_stays_unmeasured_and_never_becomes_a_behavior_pass()
    {
        using var evidence = new StopHookExecutionEvidence("codex", directory: "");
        Capture(evidence, "codex", null, null);
        await Should.ThrowAsync<Xunit.SkipException>(() => evidence.AssessAsync(() => throw new AgentExecutionInfraException("wire unavailable")));
        evidence.Record.CliObserved.ShouldBeTrue();
        evidence.Record.ModelObserved.ShouldBeFalse();
        evidence.Record.BehavioralPassed.ShouldBeNull();
        evidence.Record.QualificationSucceeded.ShouldBeFalse();
    }

    /// <summary>
    /// The run COMPLETED (drive returned instead of throwing) with no model output — the exact shape real-model
    /// stop-hook lane runs 34135877074 and 34136267088 hit: the codex CLI started, then its own turn failed on a
    /// gateway rate-limit, and <c>drive()</c> returned <c>(false, verdict)</c> rather than throwing. Whether that
    /// reds the gate or skips it must turn on the run's OWN recorded failure, not on whether some caller upstream of
    /// this evidence happened to classify it first.
    /// </summary>
    [Theory]
    [InlineData("exceeded retry limit, last status: 429 Too Many Requests", true)]     // Codex's own retry-loop giving up — verbatim text from both cited runs
    [InlineData("unrecognized codex failure: malformed prompt template", false)]       // no recognized infra marker — a genuine miss, must still red, now with the detail attached
    public async Task A_CLI_observed_run_is_classified_by_its_own_recorded_failure_not_left_as_a_flat_red(string error, bool expectInfraSkip)
    {
        using var evidence = new StopHookExecutionEvidence("codex", directory: "");
        CaptureFailed(evidence, "codex", error);

        if (expectInfraSkip)
        {
            await Should.ThrowAsync<Xunit.SkipException>(() => evidence.AssessAsync(() => Task.FromResult((false, "did not create the file"))));
            evidence.Record.Measurement.ShouldBe("infra-fault");
            evidence.Record.RecordedFailureKind.ShouldBe(FailureKind.Unavailable);
            evidence.Record.BehavioralPassed.ShouldBeNull();
        }
        else
        {
            var assertion = await Assert.ThrowsAsync<ShouldAssertException>(() => evidence.AssessAsync(() => Task.FromResult((false, "did not create the file"))));
            assertion.Message.ShouldContain(error);
            evidence.Record.Measurement.ShouldBe("no-model-evidence");
            evidence.Record.RecordedFailureKind.ShouldBeNull();
        }

        evidence.Record.CliObserved.ShouldBeTrue();
        evidence.Record.ModelObserved.ShouldBeFalse();
        evidence.Record.QualificationSucceeded.ShouldBeFalse();
    }

    [Fact]
    public void Native_frames_from_another_run_cannot_prove_this_run_executed()
    {
        using var evidence = new StopHookExecutionEvidence("claude", directory: "");
        Capture(evidence, "claude", 5, 7, NativeEvidence.ForeignRun);
        evidence.Record.CliObserved.ShouldBeFalse();
        evidence.Record.QualificationSucceeded.ShouldBeFalse();
    }

    [Fact]
    public void The_persisted_census_does_not_copy_native_frames_or_model_identifiers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-stop-hook-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var evidence = new StopHookExecutionEvidence("claude", directory)) Capture(evidence, "claude", 5, 7);
            var json = File.ReadAllText(Path.Combine(directory, "claude.json"));
            json.ShouldNotContain("PRIVATE-MODEL-NAME");
            json.ShouldNotContain("PRIVATE-ENDPOINT");
            json.ShouldNotContain("PRIVATE-API-KEY");
            JsonSerializer.Deserialize<StopHookExecutionRecord>(json, AgentJson.Options)!.ModelObserved.ShouldBeTrue();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private enum NativeEvidence { Present, Missing, ForeignRun }

    private static void Capture(StopHookExecutionEvidence evidence, string arm, int? input, int? output, NativeEvidence variant = NativeEvidence.Present)
    {
        var runId = Guid.NewGuid();
        evidence.Admitted(runId);
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Model = "PRIVATE-MODEL-NAME", TokenUsage = input is null || output is null ? null : new AgentTokenUsage { InputTokens = input.Value, OutputTokens = output.Value } };
        var run = new AgentRun { Id = runId, Status = AgentRunStatus.Succeeded, StartedAt = DateTimeOffset.UtcNow, ResultJson = JsonSerializer.Serialize(result, AgentJson.Options) };
        var native = arm == "claude" ? new ClaudeCodeHarness().ParseEvents("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"PRIVATE-ENDPOINT\",\"apiKey\":\"PRIVATE-API-KEY\"}") : new CodexHarness().ParseEvents("{\"type\":\"thread.started\",\"thread_id\":\"PRIVATE-ENDPOINT\",\"apiKey\":\"PRIVATE-API-KEY\"}");
        var events = variant != NativeEvidence.Missing ? native.Select(e => new AgentRunEvent { AgentRunId = variant == NativeEvidence.ForeignRun ? Guid.NewGuid() : runId, Kind = e.Kind, DataJson = e.Data?.GetRawText(), Text = e.Text }).ToArray() : Array.Empty<AgentRunEvent>();
        evidence.Capture(run, events);
    }

    /// <summary>A CLI-observed run that FAILED with the given recorded error — <see cref="Capture"/>'s sibling for the failure-classification cases, which need <see cref="AgentRunStatus.Failed"/> with a real <see cref="AgentRun.Error"/> rather than the always-Succeeded happy path above.</summary>
    private static void CaptureFailed(StopHookExecutionEvidence evidence, string arm, string error)
    {
        var runId = Guid.NewGuid();
        evidence.Admitted(runId);
        var result = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = error };
        var run = new AgentRun { Id = runId, Status = AgentRunStatus.Failed, Error = error, StartedAt = DateTimeOffset.UtcNow, ResultJson = JsonSerializer.Serialize(result, AgentJson.Options) };
        var native = arm == "claude" ? new ClaudeCodeHarness().ParseEvents("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"PRIVATE-ENDPOINT\"}") : new CodexHarness().ParseEvents("{\"type\":\"thread.started\",\"thread_id\":\"PRIVATE-ENDPOINT\"}");
        var events = native.Select(e => new AgentRunEvent { AgentRunId = runId, Kind = e.Kind, DataJson = e.Data?.GetRawText(), Text = e.Text }).ToArray();
        evidence.Capture(run, events);
    }
}

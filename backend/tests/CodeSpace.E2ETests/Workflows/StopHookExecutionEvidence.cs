using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>Measurement evidence for the two real stop-hook arms. It contains no model names, prompts, endpoints or raw CLI frames.</summary>
internal sealed class StopHookExecutionEvidence : IDisposable
{
    internal const string DirectoryEnvVar = "CODESPACE_STOP_HOOK_EVIDENCE_DIRECTORY";
    private readonly string? _directory;
    private StopHookExecutionRecord _record;

    internal StopHookExecutionEvidence(string arm, string? directory = null)
    {
        arm.ShouldBeOneOf("claude", "codex");
        _record = new StopHookExecutionRecord { Arm = arm };
        _directory = directory ?? Environment.GetEnvironmentVariable(DirectoryEnvVar);
    }

    internal StopHookExecutionRecord Record => _record;

    internal void Admitted(Guid runId) => _record = _record with { RunId = runId };

    internal void Capture(AgentRun run, IReadOnlyList<AgentRunEvent> events)
    {
        ((Guid?)run.Id).ShouldBe(_record.RunId);
        var result = run.ResultJson is null ? null : JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson, AgentJson.Options);
        _record = _record with
        {
            NativeSessionEvents = events.Count(e => e.AgentRunId == run.Id && e.Kind == AgentEventKind.Started && (e.DataJson is not null || e.DataArtifactId is not null)),
            InputTokens = result?.TokenUsage?.InputTokens,
            OutputTokens = result?.TokenUsage?.OutputTokens,
            RecordedFailureKind = ClassifyFailure(run),
            FailureDetail = run.Error,
        };
    }

    /// <summary>
    /// Whether the run's own recorded failure is gateway/transport INFRA rather than a genuine miss — read from the
    /// SAME vocabulary the direct-drive arms already gate on (<see cref="RealModelRunClassifier.IsGatewayInfra"/>),
    /// never a second copy of it, so this evidence layer can never disagree with the arm that captured the run.
    /// <see cref="RealModelRunClassifier.IsGatewayInfra"/> requires a NON-Succeeded run, so a Succeeded run is never
    /// infra-classed here whatever its token usage turned out to be. Null when the run is not infra-classed — still
    /// possibly a genuine failure, just not one this vocabulary recognizes as environmental.
    /// </summary>
    private static FailureKind? ClassifyFailure(AgentRun run) =>
        run.Status != AgentRunStatus.Succeeded && RealModelRunClassifier.IsGatewayInfra(run) ? FailureKind.Unavailable : null;

    internal async Task AssessAsync(Func<Task<(bool Created, string Verdict)>> drive)
    {
        await RealModelGate.AssessLiveAsync(_record.Arm == "claude" ? "Anthropic" : "OpenAI", async () =>
        {
            var result = await drive().ConfigureAwait(false);

            // The run completed (drive returned instead of throwing) but recorded no model output. When ITS OWN
            // failure is the SAME gateway/transport infra the direct-drive arms already skip on, this is a gateway
            // outage that happened to surface after drive() returned rather than from it — route it through the
            // identical non-gating SkipException path (RealModelGate's own catch below recognizes this exception),
            // so an outage never reds the gate as a behavioral miss. BehavioralPassed stays unset here, matching the
            // "drive threw before producing a verdict" case: an infra-eaten run never produced one either.
            if (!_record.ModelObserved && _record.RecordedFailureKind == FailureKind.Unavailable)
                throw new AgentExecutionInfraException($"the {_record.Arm} stop-hook run recorded no model output, and its own recorded failure is gateway/transport infra, not a behavioral miss: {_record.FailureDetail}");

            _record = _record with { BehavioralPassed = result.Created };
            return result;
        }, gating: false).ConfigureAwait(false);

        // The report-only gate intentionally swallows non-assertion faults. A normal return therefore does not
        // prove that admission or the CLI was reached. Infrastructure SkipExceptions retain their existing path.
        _record.RunId.ShouldNotBeNull("stop-hook execution was not admitted; an informational fault is not a measured pass");
        _record.CliObserved.ShouldBeTrue("stop-hook execution has no persisted native CLI session event; a status row or version probe is not execution evidence");
        _record.ModelObserved.ShouldBeTrue(ModelObservedFailureMessage());
        _record.BehavioralPassed.ShouldNotBeNull("the model ran but the arm never produced a behavioral verdict");
    }

    /// <summary>The red-assertion message for a missing model measurement — including the run's own recorded failure text when there is one, so a genuine (non-infra) miss is diagnosable from the assertion alone instead of sending a reader to dig up the run by id.</summary>
    private string ModelObservedFailureMessage()
    {
        const string reason = "stop-hook execution has no valid positive model output usage; no model measurement may be counted as a behavioral pass";

        return _record.FailureDetail is { Length: > 0 } detail ? $"{reason} (recorded run failure: {detail})" : reason;
    }

    public void Dispose()
    {
        var json = JsonSerializer.Serialize(_record, AgentJson.Options);
        Console.WriteLine($"[stop-hook-execution] {json}");
        if (string.IsNullOrWhiteSpace(_directory)) return;
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, _record.Arm + ".json"), json);
    }
}

internal sealed record StopHookExecutionRecord
{
    public int SchemaVersion { get; init; } = 1;
    public required string Arm { get; init; }
    public Guid? RunId { get; init; }
    public int NativeSessionEvents { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public bool? BehavioralPassed { get; init; }

    /// <summary>The run's failure classification, from the same vocabulary <see cref="RealModelRunClassifier.IsGatewayInfra"/> gates on. <see cref="FailureKind.Unavailable"/> when the run's own recorded failure is gateway/transport infra; null when the run succeeded, carries no failure, or its failure matches no recognized infra marker.</summary>
    public FailureKind? RecordedFailureKind { get; init; }

    /// <summary>The run's own recorded failure text (<see cref="AgentRun.Error"/>), when there is one — carried so a red assertion on a non-infra failure names WHY instead of sending a reader to look the run up by id. Null when the run recorded no failure.</summary>
    public string? FailureDetail { get; init; }

    public bool CliObserved => RunId is { } runId && runId != Guid.Empty && NativeSessionEvents > 0;
    public bool ModelObserved => CliObserved && InputTokens is >= 0 && OutputTokens is > 0;
    public bool QualificationSucceeded => ModelObserved && BehavioralPassed is true;
    public string Measurement => RunId is null ? "not-admitted" : !CliObserved ? "no-native-cli-evidence" : !ModelObserved ? (RecordedFailureKind == FailureKind.Unavailable ? "infra-fault" : "no-model-evidence") : "measured";
}

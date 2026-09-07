using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
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
        };
    }

    internal async Task AssessAsync(Func<Task<(bool Created, string Verdict)>> drive)
    {
        await RealModelGate.AssessLiveAsync(_record.Arm == "claude" ? "Anthropic" : "OpenAI", async () =>
        {
            var result = await drive().ConfigureAwait(false);
            _record = _record with { BehavioralPassed = result.Created };
            return result;
        }, gating: false).ConfigureAwait(false);

        // The report-only gate intentionally swallows non-assertion faults. A normal return therefore does not
        // prove that admission or the CLI was reached. Infrastructure SkipExceptions retain their existing path.
        _record.RunId.ShouldNotBeNull("stop-hook execution was not admitted; an informational fault is not a measured pass");
        _record.CliObserved.ShouldBeTrue("stop-hook execution has no persisted native CLI session event; a status row or version probe is not execution evidence");
        _record.ModelObserved.ShouldBeTrue("stop-hook execution has no valid positive model output usage; no model measurement may be counted as a behavioral pass");
        _record.BehavioralPassed.ShouldNotBeNull("the model ran but the arm never produced a behavioral verdict");
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
    public bool CliObserved => RunId is { } runId && runId != Guid.Empty && NativeSessionEvents > 0;
    public bool ModelObserved => CliObserved && InputTokens is >= 0 && OutputTokens is > 0;
    public bool QualificationSucceeded => ModelObserved && BehavioralPassed is true;
    public string Measurement => RunId is null ? "not-admitted" : !CliObserved ? "no-native-cli-evidence" : !ModelObserved ? "no-model-evidence" : "measured";
}

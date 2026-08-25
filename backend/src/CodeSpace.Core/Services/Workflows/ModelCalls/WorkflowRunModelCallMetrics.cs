using System.Diagnostics.Metrics;

namespace CodeSpace.Core.Services.Workflows.ModelCalls;

/// <summary>Low-cardinality OpenTelemetry instruments for projection/materialization throughput and saturation.</summary>
public static class WorkflowRunModelCallMetrics
{
    public const string MeterName = "CodeSpace.Workflows.ModelCalls";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> ProjectionChanges = Meter.CreateCounter<long>("codespace.workflow.model_call.projection.changes");
    private static readonly Counter<long> MaterializationOutcomes = Meter.CreateCounter<long>("codespace.workflow.model_call.materialization.outcomes");
    private static readonly Counter<long> SaturatedSweeps = Meter.CreateCounter<long>("codespace.workflow.model_call.sweep.saturated");
    private static readonly Histogram<double> SweepDuration = Meter.CreateHistogram<double>("codespace.workflow.model_call.sweep.duration", "ms");

    public static void RecordProjection(WorkflowRunModelCallProjectionResult result, int batchSize, TimeSpan elapsed)
    {
        Add("terminal-projected", result.TerminalAttemptsProjected);
        Add("started-projected", result.StartedAttemptsProjected);
        Add("late-start-attached", result.LateStartsAttached);
        Add("late-terminal-attached", result.LateTerminalsAttached);
        Add("orphaned-start-settled", result.OrphanedStartsSettled);
        Add("body-capture-declared", result.BodyCapturesDeclared);
        if (result.TerminalAttemptsProjected >= batchSize || result.StartedAttemptsProjected >= batchSize) SaturatedSweeps.Add(1, Tags("stage", "projection"));
        SweepDuration.Record(elapsed.TotalMilliseconds, Tags("stage", "projection"));
    }

    public static void RecordMaterialization(WorkflowRunModelCallBodyMaterializationSummary result, int batchSize, TimeSpan elapsed)
    {
        AddMaterialization("available", result.Available);
        AddMaterialization("not-recorded", result.NotRecorded);
        AddMaterialization("corrupt", result.Corrupt);
        AddMaterialization("capture-failed", result.CaptureFailed);
        AddMaterialization("external-indeterminate", result.ExternalStateIndeterminate);
        AddMaterialization("retry-scheduled", result.RetryScheduled);
        AddMaterialization("lost-lease", result.LostLease);
        if (result.Claimed >= batchSize) SaturatedSweeps.Add(1, Tags("stage", "materialization"));
        SweepDuration.Record(elapsed.TotalMilliseconds, Tags("stage", "materialization"));
    }

    private static void Add(string outcome, int value)
    {
        if (value > 0) ProjectionChanges.Add(value, Tags("outcome", outcome));
    }

    private static void AddMaterialization(string outcome, int value)
    {
        if (value > 0) MaterializationOutcomes.Add(value, Tags("outcome", outcome));
    }

    private static KeyValuePair<string, object?>[] Tags(string key, string value) => [new(key, value)];
}

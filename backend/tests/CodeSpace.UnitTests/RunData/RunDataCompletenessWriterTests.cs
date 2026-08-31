using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Messages.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.UnitTests.RunData;

/// <summary>
/// The writer's CONTAINMENT, from the side nobody looks at until it is the only thing left: what the warning says when
/// a claim is lost. Every method here runs on its own unit of work precisely so a refusal cannot take a producer's
/// records down with it — which means the only trace a lost claim leaves anywhere is this one log line.
///
/// <para>A line about a lost record that cannot say WHICH record it was is an account of nothing. That is not
/// hypothetical: the run placeholder carried the workflow run alone, so a gap belonging to a standalone Agent Run —
/// the run whose losses this plane was extended to be able to record at all — logged its loss against a blank.</para>
/// </summary>
public sealed class RunDataCompletenessWriterTests
{
    [Fact]
    public async Task A_lost_gap_names_the_standalone_agent_run_it_was_about()
    {
        var agentRunId = Guid.NewGuid();
        var logger = new CapturingLogger();
        var writer = new RunDataCompletenessWriter(new UnusableScopeFactory(), logger);

        (await writer.NoticeAsync(Gap(workflowRunId: null, agentRunId), CancellationToken.None)).ShouldBeFalse(
            customMessage: "a lost claim is REPORTED, never thrown — the containment is what keeps a refused claim out of the producer's outcome");

        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain(agentRunId.ToString(),
            customMessage: "this gap's only identity is its Agent Run, so a warning that does not name it describes a loss nobody can go and look for");
    }

    [Fact]
    public async Task A_lost_gap_of_a_workflow_bound_run_names_that_run_too()
    {
        var workflowRunId = Guid.NewGuid();
        var agentRunId = Guid.NewGuid();
        var logger = new CapturingLogger();
        var writer = new RunDataCompletenessWriter(new UnusableScopeFactory(), logger);

        await writer.NoticeAsync(Gap(workflowRunId, agentRunId), CancellationToken.None);

        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Message.ShouldContain(workflowRunId.ToString(),
            customMessage: "the workflow run is how every reader of this run reaches its record, and naming the Agent Run instead of it would trade one blank for another");
        warning.Message.ShouldContain(agentRunId.ToString(),
            customMessage: "both keys are present on this gap, so both are named — a reader chasing either one arrives");
    }

    private static WorkflowRunCaptureGap Gap(Guid? workflowRunId, Guid agentRunId)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), WorkflowRunId = workflowRunId, AgentRunId = agentRunId,
            SubjectKind = WorkflowRunDataOwnerKinds.HarnessProcessAttempt, SubjectId = Guid.NewGuid().ToString(),
            RangeKind = CaptureGapRangeKind.Unbounded, Reason = CaptureGapReason.WriteRefused,
            ReasonDetail = "the durable write of this harness process attempt was refused",
            CaptureSource = "unit/v1", NoticedAt = now, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        };
    }

    /// <summary>The failure the containment exists for, substituted at the seam the writer opens its own unit of work through, so no database is needed to reach the one branch under test.</summary>
    private sealed class UnusableScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("no scope for this write");
    }

    private sealed class CapturingLogger : ILogger<RunDataCompletenessWriter>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

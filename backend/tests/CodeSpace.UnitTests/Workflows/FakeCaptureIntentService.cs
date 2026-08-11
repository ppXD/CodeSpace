using CodeSpace.Core.Services.Agents.Capture;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>Test double for <see cref="ICaptureIntentService"/>: every write no-ops — pure executor unit tests assert the capture MAPPING, not the saga rows (those are integration-pinned).</summary>
internal sealed class FakeCaptureIntentService : ICaptureIntentService
{
    public Task OpenAsync(Guid agentRunId, Guid teamId, Guid? workflowRunId, long fenceEpoch, string? expectationsJson, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> CommitAsync(Guid agentRunId, long fenceEpoch, string factsJson, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<int> MarkIndeterminateForRunAsync(Guid agentRunId, CancellationToken cancellationToken) => Task.FromResult(0);
    public Task<int> SweepDanglingForTerminalRunsAsync(int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);
}

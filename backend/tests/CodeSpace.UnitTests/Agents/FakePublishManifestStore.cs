using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Agents;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Test double for <see cref="IPublishManifestStore"/>: every read returns empty, every write no-ops. Used by pure
/// turn-loop unit tests that construct <see cref="CodeSpace.Core.Services.Supervisor.SupervisorTurnService"/>
/// directly and never touch a real ledger — <see cref="RehydrateFromDecisionLogAsync"/>'s P0-5 published-agent
/// fold reads through this whenever a scenario stages any agent, so it must return a real (empty) result rather
/// than the <c>null!</c> placeholder these tests used before that fold existed.
/// </summary>
internal sealed class FakePublishManifestStore : IPublishManifestStore
{
    public Task UpsertForAgentRunAsync(Guid agentRunId, PublishManifestUpsert input, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpsertForIntegrationAsync(PublishManifestUpsert input, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<PublishManifest>> ListForAgentRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PublishManifest>>(Array.Empty<PublishManifest>());

    public Task<IReadOnlyList<PublishManifest>> ListForWorkflowRunAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PublishManifest>>(Array.Empty<PublishManifest>());

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>>> ListForWorkflowRunsAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>>>(new Dictionary<Guid, IReadOnlyList<PublishManifest>>());
}

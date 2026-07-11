using CodeSpace.Core.Services.Workflows.Planning;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>A no-grounding <see cref="IRepoGroundingProvider"/> for tests that construct <c>LlmSupervisorDecider</c> directly — the decider's grounding is fail-soft, so null simply omits the prompt section (the trajectory/eval harnesses bind no repo anyway).</summary>
public sealed class NullRepoGrounding : IRepoGroundingProvider
{
    public Task<string?> BuildGroundingAsync(Guid? repositoryId, Guid teamId, string? reference, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

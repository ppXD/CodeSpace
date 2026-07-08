using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.Publish.Guards;

/// <summary>The repo-level override (<see cref="Repository.PublishMode"/>) — the escape hatch for a protected/compliance-sensitive repository that must never receive an agent-pushed branch. The diff is still captured and offloaded (I1 holds regardless); only the push is skipped.</summary>
public sealed class RepositoryPolicyPublishGuard : IPublishGuard, IScopedDependency
{
    public string Name => "repository-policy";

    public int Order => 20;

    public PublishGuardVerdict? Evaluate(AgentTask task, Repository? repository) =>
        repository?.PublishMode == RepositoryPublishMode.PatchOnly ? new PublishGuardVerdict(Name, "the repository requires patch-only publishing") : null;
}

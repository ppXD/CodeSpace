using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Publish.Guards;

/// <summary>The launch profile's own explicit opt-out — <see cref="AgentTask.PushProducedBranch"/> set to exactly <c>false</c> (not merely absent/null, which now means "push" — the default flip). Runs first: an explicit per-run choice should never be shadowed by a repo-level policy.</summary>
public sealed class ProfileOptOutPublishGuard : IPublishGuard, IScopedDependency
{
    public string Name => "profile-opt-out";

    public int Order => 0;

    public PublishGuardVerdict? Evaluate(AgentTask task, Repository? repository) =>
        task.PushProducedBranch == false ? new PublishGuardVerdict(Name, "push disabled by the launch profile") : null;
}

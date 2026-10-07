using Autofac;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Tools;

/// <summary>
/// The repository's own say over an agent's use of it, once the run's binding has admitted the repository: whether a
/// command may check out the ref it names (read-only context only at its bound or default branch, which takes the
/// repository's default branch to judge), and whether it takes a pull-request write — open, merge, review, comment.
/// A write meets the SAME guard chain an agent's pushed branch meets (<see cref="IPublishGuard"/>), so a repository whose
/// policy refuses agent-pushed branches (<c>RepositoryPublishMode.PatchOnly</c>, the protected / compliance-sensitive
/// marker) takes no agent-opened, -merged, -reviewed or -commented pull request either — the reach the supervisor's own
/// pull-request opener already gives that policy.
/// </summary>
public interface IAgentRepositoryPolicy
{
    /// <summary>The refusal the model reads, or null when the repository takes the use (or did not resolve in the use's team — the tool then reports it not found itself).</summary>
    Task<string?> RefusalAsync(AgentRepositoryUse use, CancellationToken cancellationToken);
}

/// <summary>
/// The guard chain is walked as the integration push walks it: in <see cref="IPublishGuard.Order"/>, first verdict wins,
/// over a neutral task — a tool write has no per-run push opt-out, so only the repository-scoped guards can speak. The
/// repository is read in a child of the owning scope, because one run's tool catalog serves concurrent calls and must not
/// share a DbContext between them (the reason <c>NodeInvocationExecutor</c> gives each node invocation its own).
/// </summary>
public sealed class AgentRepositoryPolicy(ILifetimeScope owner) : IAgentRepositoryPolicy, IScopedDependency
{
    private static readonly AgentTask NeutralTask = new() { Goal = "", Harness = "" };

    public async Task<string?> RefusalAsync(AgentRepositoryUse use, CancellationToken cancellationToken)
    {
        await using var scope = owner.BeginLifetimeScope();

        var repository = await LoadRepositoryAsync(scope.Resolve<CodeSpaceDbContext>(), use.Bound.RepositoryId, use.TeamId, cancellationToken).ConfigureAwait(false);

        return Refusal(repository, use, scope.Resolve<IEnumerable<IPublishGuard>>());
    }

    /// <summary>The verdict over <paramref name="repository"/> for <paramref name="use"/>, worded for the agent — or null when it may proceed or the repository did not resolve. Pure, so the mapping is pinned over the production guards.</summary>
    internal static string? Refusal(Repository? repository, AgentRepositoryUse use, IEnumerable<IPublishGuard> guards)
    {
        if (repository is null) return null;

        if (!AgentRepositoryBinding.AllowsRef(use.Bound, use.Ref, repository.DefaultBranch)) return AgentRepositoryBinding.RefOutsideBinding(repository.Id, use.Bound, use.Ref, repository.DefaultBranch);

        return use.Writes ? WriteRefusal(repository, guards) : null;
    }

    /// <summary>The first publish guard's verdict over <paramref name="repository"/>, worded for the agent — or null when no guard blocks.</summary>
    private static string? WriteRefusal(Repository repository, IEnumerable<IPublishGuard> guards)
    {
        var verdict = guards.OrderBy(guard => guard.Order).Select(guard => guard.Evaluate(NeutralTask, repository)).FirstOrDefault(found => found is not null);

        return verdict is null ? null : $"Repository {repository.Id} does not take pull-request writes from an agent: {verdict.Reason}.";
    }

    private static Task<Repository?> LoadRepositoryAsync(CodeSpaceDbContext db, Guid repositoryId, Guid teamId, CancellationToken cancellationToken) =>
        db.Repository.AsNoTracking().SingleOrDefaultAsync(r => r.Id == repositoryId && r.TeamId == teamId && r.DeletedDate == null, cancellationToken);
}

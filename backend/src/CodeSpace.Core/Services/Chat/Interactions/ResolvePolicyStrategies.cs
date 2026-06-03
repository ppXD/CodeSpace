using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Chat.Interactions;

namespace CodeSpace.Core.Services.Chat.Interactions;

/// <summary>First terminal click resolves — single-responder, today's default (and the only behaviour a card without a stored policy gets).</summary>
public sealed class FirstResolvePolicyStrategy : IResolvePolicyStrategy, ISingletonDependency
{
    public ResolvePolicyKind Kind => ResolvePolicyKind.First;

    public bool IsSatisfied(IReadOnlyList<TerminalVote> nonVetoVotes, ResolvePolicy policy) => nonVetoVotes.Count > 0;
}

/// <summary>
/// Resolves when one action key has the required number of DISTINCT responders. Votes arrive deduped per
/// responder, so "2 approvals" = two different people clicking the same key (one person clicking twice, or
/// changing their vote, still counts once). Count is floored at 1 so a malformed 0 can't deadlock.
/// </summary>
public sealed class QuorumResolvePolicyStrategy : IResolvePolicyStrategy, ISingletonDependency
{
    public ResolvePolicyKind Kind => ResolvePolicyKind.Quorum;

    public bool IsSatisfied(IReadOnlyList<TerminalVote> nonVetoVotes, ResolvePolicy policy) =>
        nonVetoVotes.GroupBy(v => v.Key).Any(g => g.Count() >= Math.Max(1, policy.Count));
}

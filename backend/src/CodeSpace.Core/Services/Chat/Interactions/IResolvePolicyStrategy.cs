using CodeSpace.Messages.Dtos.Chat.Interactions;

namespace CodeSpace.Core.Services.Chat.Interactions;

/// <summary>
/// One resolve-policy kind, as a pluggable strategy keyed by <see cref="Kind"/>. Adding a policy
/// (unanimous, weighted, …) is a new class implementing this — the evaluator and the respond path don't
/// change (mirrors the IProviderAuthStrategy registry). The strategy only sees the NON-veto terminal
/// votes (a veto short-circuits in the evaluator, ahead of any policy), each already deduped to one per
/// responder (last-wins, so a changed vote counts once).
/// </summary>
public interface IResolvePolicyStrategy
{
    ResolvePolicyKind Kind { get; }

    /// <summary>True if these votes satisfy the policy, so the wait should resolve now.</summary>
    bool IsSatisfied(IReadOnlyList<TerminalVote> nonVetoVotes, ResolvePolicy policy);
}

/// <summary>A responder's current terminal vote — which action key they last clicked, and whether it vetoes.</summary>
public sealed record TerminalVote(Guid ResponderId, string Key, bool Vetoes);

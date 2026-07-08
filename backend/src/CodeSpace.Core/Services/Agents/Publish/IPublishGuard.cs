using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Publish;

/// <summary>
/// A named policy reason a repository's non-empty diff should publish as patch-only instead of a pushed branch —
/// the "guard chain" that replaced the deleted <c>CODESPACE_AGENT_PUSH_BRANCH_ENABLED</c> / integrate env gates
/// (an env var is invisible, all-or-nothing, and requires a deploy to change; a guard is per-repo, inspectable, and
/// explains itself on the manifest). Push is the DEFAULT for a non-empty diff — a guard is an explicit OPT-OUT, not
/// an opt-in gate. DI-discovered (<see cref="IScopedDependency"/> auto-registration) and evaluated in a fixed
/// <see cref="Order"/> (never DI registration order, which is not guaranteed stable) by <c>AgentRunExecutor</c>'s
/// guard-chain evaluator — the FIRST guard whose <see cref="Evaluate"/> returns non-null wins; later guards are not
/// consulted. A future guard (e.g. a sensitive-data scan) is a new sibling implementation here, not a widened
/// existing one (Rule 7).
/// </summary>
public interface IPublishGuard
{
    /// <summary>Short, stable name folded into the manifest's recorded reason (e.g. "profile-opt-out").</summary>
    string Name { get; }

    /// <summary>Fixed evaluation order, ascending — lower runs first. Explicit rather than relying on DI registration order, which is not guaranteed stable across a restart or an assembly-scan reorder.</summary>
    int Order { get; }

    /// <summary>Non-null blocks the push with <see cref="PublishGuardVerdict.Reason"/> as the recorded cause; null lets evaluation continue to the next guard. <paramref name="repository"/> is null when the task carries no resolvable repository (nothing to gate — every guard should then also return null).</summary>
    PublishGuardVerdict? Evaluate(AgentTask task, Repository? repository);
}

/// <summary>One guard's blocking verdict — <see cref="GuardName"/> + a human-readable <see cref="Reason"/> that lands verbatim on the publish-manifest row's <c>Summary</c> when no branch and no error exist (a by-choice skip, never a silent one).</summary>
public sealed record PublishGuardVerdict(string GuardName, string Reason);

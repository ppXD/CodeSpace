using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Exact team/session-scoped input for the run-intelligence context and rolling-summary paths. Carries the complete
/// attempt lineage metadata those consumers need to select the effective attempt, but only bounded goal/result leaf
/// prefixes rather than either JSON root. <see cref="LegacyBranch"/> intentionally remains an unbounded compatibility
/// leaf: historical prompts rendered it verbatim and changing that text is an intelligence-semantic change, not a
/// read optimization. Authoritative branches still come from the separately batched publish-manifest ledger.
/// </summary>
internal interface ISessionIntelligenceTurnReader : IScopedDependency
{
    Task<IReadOnlyList<SessionIntelligenceTurn>> ListAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken);
}

internal sealed record SessionIntelligenceTurn(
    Guid Id,
    Guid? RootRunId,
    int? SessionTurnIndex,
    WorkflowRunStatus Status,
    DateTimeOffset CreatedDate,
    string? Goal,
    string? Result,
    string? LegacyBranch);

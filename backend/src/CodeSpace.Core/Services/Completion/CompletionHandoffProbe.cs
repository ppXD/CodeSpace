using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Completion;

public interface ICompletionHandoffProbe
{
    /// <summary>Whether every DELIVERED target of the run still resolves to a reference the launching user can reach. Vacuously true for a run that delivered nothing (its handoff surface is the run room itself).</summary>
    Task<bool> IsHandoffReachableAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<ReceiptEnvelope> receipts, CancellationToken cancellationToken);
}

/// <summary>
/// P3b-4: the v1 handoff-reachability probe — the clean-success predicate's LAST conjunct. A Delivered receipt
/// names its repository target; the handoff is reachable when every such repository still EXISTS and is bound to
/// the run's team (the user-facing reference chain — room → branch → repo — resolves). Deep provider-side ACL
/// probing (can THIS user's credential actually read the branch) is a Q-tier deepening; the v1 boundary is the
/// durable reference chain, and an unparseable target fails CLOSED (unreachable, never assumed fine).
/// </summary>
public sealed class CompletionHandoffProbe : ICompletionHandoffProbe, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public CompletionHandoffProbe(CodeSpaceDbContext db) => _db = db;

    public async Task<bool> IsHandoffReachableAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<ReceiptEnvelope> receipts, CancellationToken cancellationToken)
    {
        var deliveredTargets = receipts
            .Where(r => r.Kind == ContractKinds.Delivery && r.Disposition == VerificationDisposition.Passed && r.TargetRef is { Length: > 0 })
            .Select(r => r.TargetRef!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (deliveredTargets.Count == 0) return true;

        var repositoryIds = new List<Guid>(deliveredTargets.Count);

        foreach (var target in deliveredTargets)
        {
            if (!Guid.TryParse(target, out var repositoryId)) return false;

            repositoryIds.Add(repositoryId);
        }

        var live = await _db.Repository.AsNoTracking()
            .CountAsync(r => repositoryIds.Contains(r.Id) && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        return live == repositoryIds.Count;
    }
}

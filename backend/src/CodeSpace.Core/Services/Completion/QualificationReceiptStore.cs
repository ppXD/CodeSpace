using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Completion;

public interface IQualificationReceiptStore
{
    /// <summary>Append one immutable receipt (the hidden qualification runner's mint — Q's later slices). The row is frozen at insert by the table's own trigger.</summary>
    Task AppendAsync(QualificationReceipt receipt, CancellationToken cancellationToken);

    /// <summary>The receipts CURRENTLY backing a (mode, capability) claim at <paramref name="asOf"/> — effective, unexpired, unrevoked. Empty = no measured claim stands.</summary>
    Task<IReadOnlyList<QualificationReceipt>> ListCurrentAsync(string mode, string capabilityKey, DateTimeOffset asOf, CancellationToken cancellationToken);

    /// <summary>Revoke FORWARD-ONLY: stamps <c>RevokedAt</c> once (the table trigger admits exactly this transition). Future gating changes; the receipt's claim about the past is never rewritten. False when the receipt is unknown or already revoked.</summary>
    Task<bool> RevokeAsync(Guid receiptId, CancellationToken cancellationToken);
}

/// <summary>Q1: the qualification receipt ledger — append-only rows the table's own trigger freezes, with the one-way revoke as the sole lawful mutation.</summary>
public sealed class QualificationReceiptStore : IQualificationReceiptStore, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public QualificationReceiptStore(CodeSpaceDbContext db) => _db = db;

    public async Task AppendAsync(QualificationReceipt receipt, CancellationToken cancellationToken)
    {
        _db.QualificationReceipt.Add(receipt);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QualificationReceipt>> ListCurrentAsync(string mode, string capabilityKey, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        await _db.QualificationReceipt.AsNoTracking()
            .Where(r => r.Mode == mode && r.CapabilityKey == capabilityKey
                && r.EffectiveFrom <= asOf && r.ExpiresAt > asOf)
            .OrderByDescending(r => r.EffectiveFrom)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> RevokeAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var receipt = await _db.QualificationReceipt.SingleOrDefaultAsync(r => r.Id == receiptId, cancellationToken).ConfigureAwait(false);

        if (receipt is null || receipt.RevokedAt is not null) return false;

        receipt.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

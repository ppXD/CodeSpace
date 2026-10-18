using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CodeSpace.Core.Services.OAuth;

public sealed class TrackedCredentialEntry : ITrackedCredentialEntry, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public TrackedCredentialEntry(CodeSpaceDbContext db) { _db = db; }

    public void MarkPayloadStored(Credential credential)
    {
        var entry = _db.Entry(credential);

        if (entry.State == EntityState.Detached) return;

        foreach (var column in PayloadWriteColumns(entry)) AcceptCurrentValue(column);
    }

    /// <summary>The columns <see cref="CredentialPayloadWriter"/>'s UPDATE sets.</summary>
    private static IEnumerable<PropertyEntry> PayloadWriteColumns(EntityEntry<Credential> entry)
    {
        yield return entry.Property(c => c.EncryptedPayload);
        yield return entry.Property(c => c.ExpiresDate);
        yield return entry.Property(c => c.LastModifiedDate);
        yield return entry.Property(c => c.LastModifiedBy);
    }

    /// <summary>
    /// Snapshot first, flag second. Clearing the flag alone makes EF put the property back to the value it loaded — the
    /// entity would carry the consumed token again — and a snapshot left behind would have change detection flag the
    /// property again at the next <c>SaveChanges</c>.
    /// </summary>
    private static void AcceptCurrentValue(PropertyEntry column)
    {
        column.OriginalValue = column.CurrentValue;
        column.IsModified = false;
    }
}

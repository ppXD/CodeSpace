using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CodeSpace.Core.Services.Tasks.RoutePreview;

/// <summary>Restore only this nested staging operation after its DB savepoint rolls back. Caller edits already pending at entry retain their values, original values and change flags.</summary>
internal sealed class TaskRouteChangeTrackerCheckpoint
{
    private readonly CodeSpaceDbContext _db;
    private readonly IReadOnlyList<EntrySnapshot> _entries;

    public TaskRouteChangeTrackerCheckpoint(CodeSpaceDbContext db)
    {
        _db = db;
        _entries = db.ChangeTracker.Entries().Select(e => new EntrySnapshot(e, e.State, e.CurrentValues.Clone(), e.OriginalValues.Clone(), e.Properties.ToDictionary(p => p.Metadata.Name, p => (p.IsModified, p.IsTemporary)))).ToList();
    }

    public void Restore()
    {
        var originalEntities = _entries.Select(e => e.Entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var entry in _db.ChangeTracker.Entries().Where(e => !originalEntities.Contains(e.Entity)).ToList()) entry.State = EntityState.Detached;
        foreach (var snapshot in _entries)
        {
            // Added caller entities may have acquired generated keys during the rolled-back SaveChanges. Detach
            // that entry temporarily to restore its original key, then restore the same caller entity and state.
            snapshot.Entry.State = snapshot.State == EntityState.Added ? EntityState.Detached : EntityState.Unchanged;
            snapshot.Entry.CurrentValues.SetValues(snapshot.Current);
            snapshot.Entry.State = snapshot.State;
            snapshot.Entry.OriginalValues.SetValues(snapshot.Original);
            foreach (var property in snapshot.Entry.Properties)
            {
                var flags = snapshot.Properties[property.Metadata.Name];
                property.IsModified = flags.IsModified;
                property.IsTemporary = flags.IsTemporary;
            }
        }
    }

    private sealed record EntrySnapshot(EntityEntry Entry, EntityState State, PropertyValues Current, PropertyValues Original, IReadOnlyDictionary<string, (bool IsModified, bool IsTemporary)> Properties);
}

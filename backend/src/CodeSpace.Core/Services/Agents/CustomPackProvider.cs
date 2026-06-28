using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Resolves a team's synthetic <see cref="PackKind.Custom"/> pack — the single Library home for hand-authored
/// agents/skills. Created lazily on first author. The <c>uq_pack_team_custom</c> singleton index is the guard: the
/// added row is flushed by the CALLER's <c>SaveChangesAsync</c> (so the pack + the authored artifact land in one
/// transaction), and a genuine concurrent first-author loses on that index — the whole command rolls back and the
/// operator's retry finds the winner. No intra-flow catch (a 23505 poisons the ambient transaction anyway).
/// </summary>
public interface ICustomPackProvider
{
    Task<Guid> EnsureForTeamAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken);
}

public sealed class CustomPackProvider : ICustomPackProvider, IScopedDependency
{
    /// <summary>The fixed display name of every team's Custom pack.</summary>
    public const string CustomPackName = "Custom";

    private readonly CodeSpaceDbContext _db;

    public CustomPackProvider(CodeSpaceDbContext db) { _db = db; }

    public async Task<Guid> EnsureForTeamAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var existingId = await _db.Pack
            .Where(p => p.TeamId == teamId && p.Kind == PackKind.Custom && p.DeletedDate == null)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (existingId != null) return existingId.Value;

        var now = DateTimeOffset.UtcNow;
        var pack = new Pack
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Kind = PackKind.Custom,
            Name = CustomPackName,
            Url = null,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };

        _db.Pack.Add(pack);   // not saved here — the caller's SaveChangesAsync flushes pack + artifact atomically
        return pack.Id;
    }
}

using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// EF-backed <see cref="IPackService"/> — the Library/store read model. Counts and artifacts are scoped to the
/// pack's team, to ACTIVE rows (a soft-deleted artifact neither counts nor lists), and to STORE snapshots
/// (Scope=Store) — the Library is the store, so a grandfathered Working bench row never shows here. A pack whose
/// artifacts are ALL Working (imported before the store model) is hidden until a re-import creates store snapshots.
/// The import/sync write path lives on <see cref="PackImportService"/>.
/// </summary>
public sealed class PackService : IPackService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public PackService(CodeSpaceDbContext db) { _db = db; }

    public async Task<IReadOnlyList<PackSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var packs = await _db.Pack.AsNoTracking()
            .Where(p => p.TeamId == teamId && p.DeletedDate == null)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (packs.Count == 0) return Array.Empty<PackSummary>();

        var ids = packs.Select(p => p.Id).ToList();
        var agentCounts = await CountByPackAsync(_db.AgentDefinition.AsNoTracking().Where(a => a.TeamId == teamId && a.PackId != null && ids.Contains(a.PackId.Value) && a.Scope == DefinitionScope.Store && a.DeletedDate == null).Select(a => a.PackId!.Value), cancellationToken).ConfigureAwait(false);
        var skillCounts = await CountByPackAsync(_db.SkillDefinition.AsNoTracking().Where(s => s.TeamId == teamId && s.PackId != null && ids.Contains(s.PackId.Value) && s.Scope == DefinitionScope.Store && s.DeletedDate == null).Select(s => s.PackId!.Value), cancellationToken).ConfigureAwait(false);

        // Only surface packs that own at least one store snapshot — a grandfathered-only pack stays hidden.
        return packs
            .Select(p => ToSummary(p, agentCounts.GetValueOrDefault(p.Id), skillCounts.GetValueOrDefault(p.Id)))
            .Where(s => s.AgentCount + s.SkillCount > 0)
            .ToList();
    }

    public async Task<PackDetail?> GetAsync(Guid teamId, Guid packId, CancellationToken cancellationToken)
    {
        var pack = await _db.Pack.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == packId && p.TeamId == teamId && p.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (pack == null) return null;

        var agents = await _db.AgentDefinition.AsNoTracking()
            .Where(a => a.PackId == packId && a.TeamId == teamId && a.Scope == DefinitionScope.Store && a.DeletedDate == null)
            .Select(a => new PackArtifactSummary { Kind = PackArtifactKind.Agent, Id = a.Id, Slug = a.Slug, Name = a.Name, Description = a.Description, SourcePath = a.SourcePath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var skills = await _db.SkillDefinition.AsNoTracking()
            .Where(s => s.PackId == packId && s.TeamId == teamId && s.Scope == DefinitionScope.Store && s.DeletedDate == null)
            .Select(s => new PackArtifactSummary { Kind = PackArtifactKind.Skill, Id = s.Id, Slug = s.Slug, Name = s.Name, Description = s.Description, SourcePath = s.SourcePath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var artifacts = agents.Concat(skills).OrderBy(a => a.Kind).ThenBy(a => a.Slug, StringComparer.Ordinal).ToList();

        return new PackDetail { Pack = ToSummary(pack, agents.Count, skills.Count), Artifacts = artifacts };
    }

    private static async Task<Dictionary<Guid, int>> CountByPackAsync(IQueryable<Guid> packIds, CancellationToken cancellationToken)
    {
        var rows = await packIds.GroupBy(id => id).Select(g => new { PackId = g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.PackId, r => r.Count);
    }

    private static PackSummary ToSummary(Persistence.Entities.Pack pack, int agentCount, int skillCount) => new()
    {
        Id = pack.Id,
        Kind = pack.Kind,
        Name = pack.Name,
        Url = pack.Url,
        Reference = pack.Reference,
        LastSyncedSha = pack.LastSyncedSha,
        LastSyncedDate = pack.LastSyncedDate,
        AgentCount = agentCount,
        SkillCount = skillCount,
    };
}

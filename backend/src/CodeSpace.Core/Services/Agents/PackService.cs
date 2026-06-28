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

        // Reuse ArtifactQuery so the full detail orders each kind exactly as the paged tab does (slug + Id tie-break,
        // DB collation). agents-before-skills falls out of the concat order — the unified list stays kind-grouped.
        var agents = await ArtifactQuery(teamId, packId, PackArtifactKind.Agent, "").ToListAsync(cancellationToken).ConfigureAwait(false);
        var skills = await ArtifactQuery(teamId, packId, PackArtifactKind.Skill, "").ToListAsync(cancellationToken).ConfigureAwait(false);

        var artifacts = agents.Concat(skills).ToList();

        return new PackDetail { Pack = ToSummary(pack, agents.Count, skills.Count), Artifacts = artifacts };
    }

    public async Task<PagedArtifacts> ListArtifactsAsync(Guid teamId, Guid packId, PackArtifactKind kind, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(pageSize, 1, 100);
        var query = ArtifactQuery(teamId, packId, kind, (search ?? "").Trim().ToLower());

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        var clampedPage = Math.Clamp(page, 0, pageCount - 1);

        var items = await query.Skip(clampedPage * size).Take(size).ToListAsync(cancellationToken).ConfigureAwait(false);

        return new PagedArtifacts { Items = items, Total = total, Page = clampedPage, PageCount = pageCount };
    }

    // The single source of artifact ordering + filtering — both GetAsync (full detail) and ListArtifactsAsync (paged)
    // build on this, so the detail pane and the paged tab present the same store list in the same order. The
    // ThenBy(Id) tie-break is load-bearing: Store snapshots carry NO unique handle (the team-slug index is
    // Working-only; a re-import / grandfathered pack lands duplicate slugs as distinct rows keyed on
    // (pack, source_path)), so OrderBy(Slug) alone is non-deterministic and Skip/Take would drop or repeat tied
    // rows across pages. search is already trimmed + lowercased; slugs are stored lowercase (DeriveSlug), so the
    // slug operand matches case-insensitively without a ToLower.
    private IQueryable<PackArtifactSummary> ArtifactQuery(Guid teamId, Guid packId, PackArtifactKind kind, string search)
    {
        if (kind == PackArtifactKind.Agent)
        {
            var agents = _db.AgentDefinition.AsNoTracking().Where(a => a.PackId == packId && a.TeamId == teamId && a.Scope == DefinitionScope.Store && a.DeletedDate == null);

            if (search != "") agents = agents.Where(a => a.Name.ToLower().Contains(search) || a.Slug.Contains(search));

            return agents.OrderBy(a => a.Slug).ThenBy(a => a.Id).Select(a => new PackArtifactSummary { Kind = PackArtifactKind.Agent, Id = a.Id, Slug = a.Slug, Name = a.Name, Description = a.Description, SourcePath = a.SourcePath });
        }

        var skills = _db.SkillDefinition.AsNoTracking().Where(s => s.PackId == packId && s.TeamId == teamId && s.Scope == DefinitionScope.Store && s.DeletedDate == null);

        if (search != "") skills = skills.Where(s => s.Name.ToLower().Contains(search) || s.Slug.Contains(search));

        return skills.OrderBy(s => s.Slug).ThenBy(s => s.Id).Select(s => new PackArtifactSummary { Kind = PackArtifactKind.Skill, Id = s.Id, Slug = s.Slug, Name = s.Name, Description = s.Description, SourcePath = s.SourcePath });
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

using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// EF-backed <see cref="IPackImportService"/>. Composes the proven pieces — <see cref="IPackSourceFetcher"/>
/// (host-allowlist + clone-to-temp) → <see cref="IPackSourceWalker"/> (recursive agent + skill discovery) → a
/// per-team conflict check — into the unified URL preview. The transient clone is disposed as soon as discovery
/// finishes (the <c>using</c>), so a preview leaves nothing on disk. Slug derivation reuses
/// <see cref="AgentDefinitionService.DeriveSlug"/> (the same handle rule authoring/import already use), and
/// importability mirrors the agent-only preview: parseable + named + no active-slug conflict.
/// </summary>
public sealed class PackImportService : IPackImportService, IScopedDependency
{
    private readonly IPackSourceFetcher _fetcher;
    private readonly IPackSourceWalker _walker;
    private readonly CodeSpaceDbContext _db;

    public PackImportService(IPackSourceFetcher fetcher, IPackSourceWalker walker, CodeSpaceDbContext db)
    {
        _fetcher = fetcher;
        _walker = walker;
        _db = db;
    }

    public async Task<PackPreview> PreviewFromUrlAsync(string url, string? reference, Guid teamId, CancellationToken cancellationToken)
    {
        using var checkout = await _fetcher.FetchAsync(url, reference, cancellationToken).ConfigureAwait(false);

        var pack = await _walker.WalkAsync(checkout.Directory, cancellationToken).ConfigureAwait(false);

        var agentSlugs = await ActiveSlugsAsync(_db.AgentDefinition.AsNoTracking().Where(a => a.TeamId == teamId && a.DeletedDate == null).Select(a => a.Slug), cancellationToken).ConfigureAwait(false);
        var skillSlugs = await ActiveSlugsAsync(_db.SkillDefinition.AsNoTracking().Where(s => s.TeamId == teamId && s.DeletedDate == null).Select(s => s.Slug), cancellationToken).ConfigureAwait(false);

        return new PackPreview
        {
            Reference = reference,
            Agents = pack.Agents.Select(a => BuildAgentItem(a, agentSlugs)).ToList(),
            Skills = pack.Skills.Select(s => BuildSkillItem(s, skillSlugs)).ToList(),
        };
    }

    private static AgentPackPreviewItem BuildAgentItem(ParsedAgentDefinition a, IReadOnlySet<string> existingSlugs)
    {
        var slug = AgentDefinitionService.DeriveSlug(a.Name);
        var hasName = !string.IsNullOrWhiteSpace(a.Name) && slug.Length > 0;
        var conflict = hasName && existingSlugs.Contains(slug);

        return new AgentPackPreviewItem
        {
            SourcePath = a.SourcePath,
            Name = a.Name,
            DerivedSlug = slug,
            Description = a.Description,
            SystemPrompt = a.SystemPrompt,
            Model = a.Model,
            Tools = a.Tools,
            RawFrontmatterJson = a.RawFrontmatterJson,
            Diagnostics = a.Diagnostics,
            SlugConflict = conflict,
            Importable = hasName && !conflict,
        };
    }

    private static SkillPackPreviewItem BuildSkillItem(ParsedSkillDefinition s, IReadOnlySet<string> existingSlugs)
    {
        var slug = AgentDefinitionService.DeriveSlug(s.Name);
        var hasName = !string.IsNullOrWhiteSpace(s.Name) && slug.Length > 0;
        var conflict = hasName && existingSlugs.Contains(slug);

        return new SkillPackPreviewItem
        {
            SourcePath = s.SourcePath,
            Name = s.Name,
            DerivedSlug = slug,
            Description = s.Description,
            Body = s.Body,
            Category = s.Category,
            RawFrontmatterJson = s.RawFrontmatterJson,
            Diagnostics = s.Diagnostics,
            SlugConflict = conflict,
            Importable = hasName && !conflict,
        };
    }

    private static async Task<IReadOnlySet<string>> ActiveSlugsAsync(IQueryable<string> slugs, CancellationToken cancellationToken) =>
        (await slugs.ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
}

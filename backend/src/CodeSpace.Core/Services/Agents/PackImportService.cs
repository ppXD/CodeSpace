using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// EF-backed <see cref="IPackImportService"/>. Composes the proven pieces — <see cref="IPackSourceFetcher"/>
/// (host-allowlist + clone-to-temp) → <see cref="IPackSourceWalker"/> (recursive agent + skill discovery) into the
/// unified URL preview. The transient clone is disposed as soon as discovery finishes (the <c>using</c>), so a
/// preview leaves nothing on disk. Slug derivation reuses <see cref="AgentDefinitionService.DeriveSlug"/> (the same
/// handle rule authoring/import already use). Imports land as STORE snapshots, which carry no unique handle, so
/// importability is simply parseable + named — there is no team-slug conflict to check.
/// </summary>
public sealed partial class PackImportService : IPackImportService, IScopedDependency
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

        // An import lands as a STORE snapshot, which carries no unique handle (the team-slug index is Working-only),
        // so nothing in the team — not a sibling artifact, not a grandfathered bench row — can conflict with it.
        // Importability is therefore just "parseable + named"; there is no handle to check against.
        return new PackPreview
        {
            Reference = reference,
            Agents = pack.Agents.Select(BuildAgentItem).ToList(),
            Skills = pack.Skills.Select(BuildSkillItem).ToList(),
        };
    }

    private static AgentPackPreviewItem BuildAgentItem(ParsedAgentDefinition a)
    {
        var slug = AgentDefinitionService.DeriveSlug(a.Name);
        var hasName = !string.IsNullOrWhiteSpace(a.Name) && slug.Length > 0;

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
            SlugConflict = false,
            Importable = hasName,
        };
    }

    private static SkillPackPreviewItem BuildSkillItem(ParsedSkillDefinition s)
    {
        var slug = AgentDefinitionService.DeriveSlug(s.Name);
        var hasName = !string.IsNullOrWhiteSpace(s.Name) && slug.Length > 0;

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
            SlugConflict = false,
            Importable = hasName,
        };
    }
}

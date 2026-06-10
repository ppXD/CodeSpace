using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Providers.Source;
using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// EF-backed <see cref="IAgentPackImportService"/>. Discovers <c>*.md</c> agent files under a directory of a
/// bound repository via <c>IRepositorySourceService</c> (auth + scope reused for free), parses each with the
/// resolved <see cref="IAgentArtifactParser"/>, and (on commit) writes the selected ones via
/// <c>IAgentDefinitionService.ImportAsync</c>. Preview persists nothing; import re-fetches the selection so
/// the verbatim-frontmatter guarantee stays server-authoritative.
/// </summary>
public sealed class AgentPackImportService : IAgentPackImportService, IScopedDependency
{
    private const string DefaultRootPath = "agents";
    private const string DefaultParserKind = "claude-code";
    private const int FetchParallelism = 8;

    private readonly IRepositorySourceService _source;
    private readonly IAgentArtifactParserRegistry _parsers;
    private readonly IAgentDefinitionService _personas;
    private readonly CodeSpaceDbContext _db;

    public AgentPackImportService(IRepositorySourceService source, IAgentArtifactParserRegistry parsers, IAgentDefinitionService personas, CodeSpaceDbContext db)
    {
        _source = source;
        _parsers = parsers;
        _personas = personas;
        _db = db;
    }

    public async Task<AgentPackPreview> PreviewAsync(Guid repositoryId, string? reference, string? rootPath, Guid teamId, CancellationToken cancellationToken)
    {
        var root = NormalizeRoot(rootPath);
        var parser = _parsers.Resolve(DefaultParserKind);

        var files = await ListAgentFilesAsync(repositoryId, root, reference, cancellationToken).ConfigureAwait(false);

        var parsed = await BoundedParallelMap.RunAsync(files, FetchParallelism,
            async (path, ct) => await FetchAndParseAsync(repositoryId, path, reference, parser, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        var existingSlugs = await ActiveSlugsAsync(teamId, cancellationToken).ConfigureAwait(false);

        var items = files.Select(path => BuildPreviewItem(path, parsed.GetValueOrDefault(path), existingSlugs)).ToList();

        return new AgentPackPreview { Reference = reference, RootPath = root, Items = items };
    }

    public async Task<IReadOnlyList<AgentImportResult>> ImportAsync(Guid repositoryId, string? reference, string? rootPath, IReadOnlyList<string> selectedSourcePaths, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var parser = _parsers.Resolve(DefaultParserKind);
        var results = new List<AgentImportResult>();

        // Sequential: each write is a DB transaction racing the unique-slug index, and the per-path result
        // order mirrors the operator's selection. A pack's selection is a handful of files, not thousands.
        foreach (var path in selectedSourcePaths.Distinct())
            results.Add(await ImportOneAsync(repositoryId, path, reference, parser, teamId, actorUserId, cancellationToken).ConfigureAwait(false));

        return results;
    }

    private async Task<AgentImportResult> ImportOneAsync(Guid repositoryId, string path, string? reference, IAgentArtifactParser parser, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        ParsedAgentDefinition? parsed;
        try
        {
            parsed = await FetchAndParseAsync(repositoryId, path, reference, parser, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentImportResult { SourcePath = path, Outcome = AgentImportOutcome.Failed, Reason = $"Could not fetch the file: {ex.Message}" };
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Name))
            return new AgentImportResult { SourcePath = path, Outcome = AgentImportOutcome.Failed, Reason = "Not a valid agent file (missing name or unreadable content)." };

        try
        {
            var id = await _personas.ImportAsync(teamId, ToInput(parsed), actorUserId, cancellationToken).ConfigureAwait(false);
            return new AgentImportResult { SourcePath = path, Outcome = AgentImportOutcome.Imported, AgentDefinitionId = id };
        }
        catch (InvalidOperationException ex)   // slug already taken — never overwrite an existing/edited persona
        {
            return new AgentImportResult { SourcePath = path, Outcome = AgentImportOutcome.Skipped, Reason = ex.Message };
        }
    }

    /// <summary>List the <c>*.md</c> files directly under the root directory. Throws an actionable error when the directory can't be read; returns an empty list when it exists but has no agent files.</summary>
    private async Task<IReadOnlyList<string>> ListAgentFilesAsync(Guid repositoryId, string root, string? reference, CancellationToken cancellationToken)
    {
        IReadOnlyList<Messages.Dtos.Providers.RemoteTreeEntry> tree;
        try
        {
            tree = await _source.ListTreeAsync(repositoryId, root, reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not read '{root}' at ref '{reference ?? "default"}': {ex.Message}. Check the directory path and the ref.");
        }

        return tree
            .Where(e => e.Type == RemoteTreeEntryType.File && e.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Path)
            .ToList();
    }

    private async Task<ParsedAgentDefinition?> FetchAndParseAsync(Guid repositoryId, string path, string? reference, IAgentArtifactParser parser, CancellationToken cancellationToken)
    {
        var file = await _source.GetFileAsync(repositoryId, path, reference, cancellationToken).ConfigureAwait(false);

        return file.Text is null ? null : parser.Parse(file.Text, path);   // binary / truncated → skip
    }

    private AgentPackPreviewItem BuildPreviewItem(string path, ParsedAgentDefinition? parsed, IReadOnlySet<string> existingSlugs)
    {
        if (parsed is null)
            return new AgentPackPreviewItem { SourcePath = path, Name = "", DerivedSlug = "", Importable = false, Diagnostics = new[] { "Could not read this file (binary, too large, or fetch failed)." } };

        var slug = AgentDefinitionService.DeriveSlug(parsed.Name);
        var hasName = !string.IsNullOrWhiteSpace(parsed.Name) && slug.Length > 0;
        var conflict = hasName && existingSlugs.Contains(slug);

        return new AgentPackPreviewItem
        {
            SourcePath = path,
            Name = parsed.Name,
            DerivedSlug = slug,
            Description = parsed.Description,
            SystemPrompt = parsed.SystemPrompt,
            Model = parsed.Model,
            Tools = parsed.Tools,
            RawFrontmatterJson = parsed.RawFrontmatterJson,
            Diagnostics = parsed.Diagnostics,
            SlugConflict = conflict,
            Importable = hasName && !conflict,
        };
    }

    private async Task<IReadOnlySet<string>> ActiveSlugsAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var slugs = await _db.AgentDefinition.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.DeletedDate == null)
            .Select(a => a.Slug)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return slugs.ToHashSet(StringComparer.Ordinal);
    }

    private static ImportedAgentDefinitionInput ToInput(ParsedAgentDefinition parsed) => new()
    {
        Name = parsed.Name,
        Description = parsed.Description,
        SystemPrompt = parsed.SystemPrompt,
        Model = parsed.Model,
        Tools = parsed.Tools,
        RawFrontmatterJson = parsed.RawFrontmatterJson,
        SourcePath = parsed.SourcePath,
    };

    private static string NormalizeRoot(string? rootPath) =>
        string.IsNullOrWhiteSpace(rootPath) ? DefaultRootPath : rootPath.Trim().Trim('/');
}

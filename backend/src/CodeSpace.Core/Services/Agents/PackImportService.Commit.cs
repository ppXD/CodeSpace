using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The COMMIT half of <see cref="PackImportService"/> (the preview lives in the main file). Re-clones the URL
/// (never trusting the preview's content), re-walks it, resolves the team's <c>Pack</c> for that source, and
/// upserts each selected artifact on its (pack, source-path) sync identity — so a re-sync UPDATES rather than
/// duplicates. Agent + skill row-writing is the import concern's own logic; slug derivation reuses
/// <see cref="AgentDefinitionService.DeriveSlug"/>, and a handle that collides with a DIFFERENT active
/// definition is reported as <see cref="PackImportOutcome.Skipped"/> (an import never overwrites an unrelated
/// persona/skill). Saves per artifact (the selection is a handful of files) so a slug race translates locally.
/// </summary>
public sealed partial class PackImportService
{
    public async Task<PackImportResult> ImportFromUrlAsync(string url, string? reference, IReadOnlyList<string> sourcePaths, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        using var checkout = await _fetcher.FetchAsync(url, reference, cancellationToken).ConfigureAwait(false);

        var discovered = await _walker.WalkAsync(checkout.Directory, cancellationToken).ConfigureAwait(false);

        var pack = await ResolvePackAsync(url, reference, teamId, actorUserId, cancellationToken).ConfigureAwait(false);

        var agentsByPath = IndexByPath(discovered.Agents, a => a.SourcePath);
        var skillsByPath = IndexByPath(discovered.Skills, s => s.SourcePath);

        var items = new List<PackArtifactImportResult>();

        foreach (var path in sourcePaths.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (agentsByPath.TryGetValue(path, out var agent))
                items.Add(await UpsertAgentAsync(pack, agent, teamId, actorUserId, cancellationToken).ConfigureAwait(false));
            else if (skillsByPath.TryGetValue(path, out var skill))
                items.Add(await UpsertSkillAsync(pack, skill, teamId, actorUserId, cancellationToken).ConfigureAwait(false));
            else
                items.Add(new PackArtifactImportResult { SourcePath = path, Kind = null, Outcome = PackImportOutcome.Failed, Reason = "Not found in the pack at this ref — it may have been removed or renamed since the preview." });
        }

        return new PackImportResult { PackId = pack.Id, Items = items };
    }

    /// <summary>Find the team's active pack for this source (one per team+url+subpath, per the unique index) and refresh its ref/sync time, or create it. The URL flow has no subpath (the whole repo).</summary>
    private async Task<Pack> ResolvePackAsync(string url, string? reference, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var existing = await _db.Pack.SingleOrDefaultAsync(p => p.TeamId == teamId && p.Url == url && p.Subpath == null && p.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (existing != null)
        {
            existing.Reference = reference;
            existing.LastSyncedDate = now;
            existing.LastModifiedDate = now;
            existing.LastModifiedBy = actorUserId;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var pack = new Pack
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Kind = DeterminePackKind(url),
            Name = DerivePackName(url),
            Url = url,
            Reference = reference,
            LastSyncedDate = now,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };

        _db.Pack.Add(pack);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return pack;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "uq_pack_team_source"))
        {
            // Race-loss: a concurrent import created the same pack first. Detach our loser and use the winner.
            _db.Entry(pack).State = EntityState.Detached;
            return await _db.Pack.SingleAsync(p => p.TeamId == teamId && p.Url == url && p.Subpath == null && p.DeletedDate == null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PackArtifactImportResult> UpsertAgentAsync(Pack pack, ParsedAgentDefinition parsed, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var existing = await _db.AgentDefinition.SingleOrDefaultAsync(a => a.PackId == pack.Id && a.SourcePath == parsed.SourcePath && a.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (existing != null)
        {
            ApplyAgentContent(existing, parsed);
            existing.LastModifiedDate = now;
            existing.LastModifiedBy = actorUserId;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return AgentResult(parsed.SourcePath, PackImportOutcome.Updated, existing.Id);
        }

        if (string.IsNullOrWhiteSpace(parsed.Name))
            return AgentResult(parsed.SourcePath, PackImportOutcome.Skipped, reason: "The agent has no name — nothing to import.");

        var slug = AgentDefinitionService.DeriveSlug(parsed.Name);

        if (await _db.AgentDefinition.AnyAsync(a => a.TeamId == teamId && a.Slug == slug && a.DeletedDate == null, cancellationToken).ConfigureAwait(false))
            return AgentResult(parsed.SourcePath, PackImportOutcome.Skipped, reason: $"An agent with handle '{slug}' already exists in this team — left untouched.");

        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Origin = AgentDefinitionOrigin.Imported,
            PackId = pack.Id,
            SourcePath = parsed.SourcePath,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };
        ApplyAgentContent(agent, parsed);

        _db.AgentDefinition.Add(agent);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return AgentResult(parsed.SourcePath, PackImportOutcome.Imported, agent.Id);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "uq_agent_definition_team_slug"))
        {
            _db.Entry(agent).State = EntityState.Detached;
            return AgentResult(parsed.SourcePath, PackImportOutcome.Skipped, reason: $"An agent with handle '{slug}' already exists in this team — left untouched.");
        }
    }

    private async Task<PackArtifactImportResult> UpsertSkillAsync(Pack pack, ParsedSkillDefinition parsed, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var existing = await _db.SkillDefinition.SingleOrDefaultAsync(s => s.PackId == pack.Id && s.SourcePath == parsed.SourcePath && s.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (existing != null)
        {
            ApplySkillContent(existing, parsed);
            existing.LastModifiedDate = now;
            existing.LastModifiedBy = actorUserId;

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return SkillResult(parsed.SourcePath, PackImportOutcome.Updated, existing.Id);
        }

        if (string.IsNullOrWhiteSpace(parsed.Name))
            return SkillResult(parsed.SourcePath, PackImportOutcome.Skipped, reason: "The SKILL.md has no name — nothing to import.");

        var slug = AgentDefinitionService.DeriveSlug(parsed.Name);

        if (await _db.SkillDefinition.AnyAsync(s => s.TeamId == teamId && s.Slug == slug && s.DeletedDate == null, cancellationToken).ConfigureAwait(false))
            return SkillResult(parsed.SourcePath, PackImportOutcome.Skipped, reason: $"A skill with handle '{slug}' already exists in this team — left untouched.");

        var skill = new SkillDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Origin = SkillDefinitionOrigin.Imported,
            PackId = pack.Id,
            SourcePath = parsed.SourcePath,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };
        ApplySkillContent(skill, parsed);

        _db.SkillDefinition.Add(skill);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return SkillResult(parsed.SourcePath, PackImportOutcome.Imported, skill.Id);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "uq_skill_definition_team_slug"))
        {
            _db.Entry(skill).State = EntityState.Detached;
            return SkillResult(parsed.SourcePath, PackImportOutcome.Skipped, reason: $"A skill with handle '{slug}' already exists in this team — left untouched.");
        }
    }

    /// <summary>Sets the content columns from the parsed artifact. Never touches slug / origin / pack / source-path (identity) — a re-sync refreshes the body, not the handle.</summary>
    private static void ApplyAgentContent(AgentDefinition agent, ParsedAgentDefinition parsed)
    {
        agent.Name = parsed.Name;
        agent.Description = parsed.Description;
        agent.SystemPrompt = parsed.SystemPrompt;
        agent.Model = NullIfBlank(parsed.Model);
        agent.ToolsJson = parsed.Tools == null ? null : JsonSerializer.Serialize(parsed.Tools, AgentJson.Options);
        agent.RawFrontmatterJson = string.IsNullOrWhiteSpace(parsed.RawFrontmatterJson) ? "{}" : parsed.RawFrontmatterJson;
    }

    private static void ApplySkillContent(SkillDefinition skill, ParsedSkillDefinition parsed)
    {
        skill.Name = parsed.Name;
        skill.Description = parsed.Description;
        skill.Body = parsed.Body;
        skill.Category = NullIfBlank(parsed.Category);
        skill.RawFrontmatterJson = string.IsNullOrWhiteSpace(parsed.RawFrontmatterJson) ? "{}" : parsed.RawFrontmatterJson;
    }

    /// <summary>"owner/repo" from a github/clone URL (path trimmed of slashes + a trailing ".git"); falls back to the host when the path is empty. Pure + internal so it's unit-pinned.</summary>
    internal static string DerivePackName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];

        return path.Length > 0 ? path : uri.Host;
    }

    /// <summary>Github only for the github.com host; any other allowlisted host is a generic git URL. Pure + internal so it's unit-pinned.</summary>
    internal static PackKind DeterminePackKind(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && string.Equals(uri.Host.TrimEnd('.'), "github.com", StringComparison.OrdinalIgnoreCase)
            ? PackKind.Github
            : PackKind.GitUrl;

    private static Dictionary<string, T> IndexByPath<T>(IEnumerable<T> items, Func<T, string> path)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items) map[path(item)] = item;   // one entry per file; last wins defensively
        return map;
    }

    private static bool IsUniqueViolation(DbUpdateException ex, string constraintFragment) =>
        ex.InnerException is Npgsql.PostgresException pg
            && pg.SqlState == "23505"
            && (pg.ConstraintName?.Contains(constraintFragment, StringComparison.Ordinal) ?? false);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static PackArtifactImportResult AgentResult(string sourcePath, PackImportOutcome outcome, Guid? id = null, string? reason = null) =>
        new() { SourcePath = sourcePath, Kind = PackArtifactKind.Agent, Outcome = outcome, DefinitionId = id, Reason = reason };

    private static PackArtifactImportResult SkillResult(string sourcePath, PackImportOutcome outcome, Guid? id = null, string? reason = null) =>
        new() { SourcePath = sourcePath, Kind = PackArtifactKind.Skill, Outcome = outcome, DefinitionId = id, Reason = reason };
}

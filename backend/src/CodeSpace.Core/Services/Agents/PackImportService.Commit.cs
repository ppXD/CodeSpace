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
/// <see cref="AgentDefinitionService.DeriveSlug"/>.
///
/// <para>ATOMIC by design — the whole import is ONE <c>SaveChangesAsync</c> inside the command's ambient
/// transaction (<c>TransactionalBehavior</c>). Known handle collisions are decided IN MEMORY before the save
/// (a handle that already belongs to a DIFFERENT active definition, or a second artifact in the same pack that
/// derives an already-claimed handle, is <see cref="PackImportOutcome.Skipped"/> — never a failed INSERT that
/// would abort the transaction). The only thing that can still throw at the save is a genuine concurrent-request
/// race (another request committed the same source / handle in between): the transaction rolls the whole import
/// back and the operator retries — nothing half-applies.</para>
///
/// <para>The Pack is created/touched ONLY when at least one artifact is imported or updated — an empty selection
/// short-circuits before the clone, and an all-Skipped/all-Failed selection leaves no phantom library and no
/// misleading sync timestamp.</para>
/// </summary>
public sealed partial class PackImportService
{
    public async Task<PackImportResult> ImportFromUrlAsync(string url, string? reference, IReadOnlyList<string> sourcePaths, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var selected = sourcePaths.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

        // Nothing selected → a clean no-op: never clone or touch a Pack for an empty commit.
        if (selected.Count == 0) return new PackImportResult { PackId = Guid.Empty, Items = Array.Empty<PackArtifactImportResult>() };

        using var checkout = await _fetcher.FetchAsync(url, reference, cancellationToken).ConfigureAwait(false);

        var discovered = await _walker.WalkAsync(checkout.Directory, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var (pack, packIsNew) = await ResolvePackTargetAsync(url, reference, teamId, actorUserId, now, cancellationToken).ConfigureAwait(false);

        var agentsByPath = IndexByPath(discovered.Agents, a => a.SourcePath);
        var skillsByPath = IndexByPath(discovered.Skills, s => s.SourcePath);

        // This pack's already-imported STORE snapshots (tracked, so an Update flushes), keyed by source-path = the
        // sync identity. Scoped to Store so a grandfathered WORKING row sharing this pack's (pack, source_path) is
        // never picked up here — otherwise the lookup would match that live bench agent and the upsert would take the
        // UPDATE branch, clobbering its content (and creating no snapshot) instead of adding a fresh Store snapshot
        // beside it. A brand-new pack has none yet, so skip the lookup.
        var existingAgents = packIsNew ? EmptyByPath<AgentDefinition>() : await ExistingByPathAsync(_db.AgentDefinition.Where(a => a.PackId == pack.Id && a.Scope == DefinitionScope.Store && a.DeletedDate == null), a => a.SourcePath!, cancellationToken).ConfigureAwait(false);
        var existingSkills = packIsNew ? EmptyByPath<SkillDefinition>() : await ExistingByPathAsync(_db.SkillDefinition.Where(s => s.PackId == pack.Id && s.Scope == DefinitionScope.Store && s.DeletedDate == null), s => s.SourcePath!, cancellationToken).ConfigureAwait(false);

        // Skill handles → id, loaded once and EXTENDED in memory as new skills are added this batch, so an imported
        // agent's declared skills resolve against this map (existing + same-batch). Store snapshots are no longer
        // subject to the team-slug uniqueness (that index is Working-only now), so an import never SKIPS on a handle
        // collision — a re-imported grandfathered pack must populate the store, not skip itself wholesale.
        var skillSlugToId = await ActiveSkillSlugToIdAsync(teamId, cancellationToken).ConfigureAwait(false);

        var items = new List<PackArtifactImportResult>();
        var newAgentSkills = new List<(Guid AgentId, IReadOnlyList<string> Declared)>();

        foreach (var path in selected)
        {
            if (agentsByPath.TryGetValue(path, out var agent))
            {
                var result = UpsertAgent(pack, agent, existingAgents, teamId, actorUserId, now);
                items.Add(result);

                // Seed bindings from the declared skills only on a FRESH import — a re-sync leaves the (possibly
                // editor-curated) bindings of an existing agent untouched.
                if (result.Outcome == PackImportOutcome.Imported && agent.Skills.Count > 0)
                    newAgentSkills.Add((result.DefinitionId!.Value, agent.Skills));
            }
            else if (skillsByPath.TryGetValue(path, out var skill))
                items.Add(UpsertSkill(pack, skill, existingSkills, skillSlugToId, teamId, actorUserId, now));
            else
                items.Add(new PackArtifactImportResult { SourcePath = path, Kind = null, Outcome = PackImportOutcome.Failed, Reason = "Not found in the pack at this ref — it may have been removed or renamed since the preview." });
        }

        // Only create/touch the Pack when something actually landed — an all-Skipped/all-Failed selection leaves
        // no phantom library and no misleading sync timestamp.
        if (!items.Any(i => i.Outcome is PackImportOutcome.Imported or PackImportOutcome.Updated))
            return new PackImportResult { PackId = packIsNew ? Guid.Empty : pack.Id, Items = items };

        PersistPackSync(pack, packIsNew, reference, actorUserId, now);

        BindDeclaredSkills(newAgentSkills, skillSlugToId, actorUserId, now);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new PackImportResult { PackId = pack.Id, Items = items };
    }

    /// <summary>Resolve the team's active pack for this source (one per team+url+subpath, per the unique index), or build a NEW unsaved one. Read-only: neither mutates nor adds — the actual write is deferred to <see cref="PersistPackSync"/> so a no-op commit never touches a pack. The URL flow has no subpath (the whole repo).</summary>
    private async Task<(Pack Pack, bool IsNew)> ResolvePackTargetAsync(string url, string? reference, Guid teamId, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await _db.Pack.SingleOrDefaultAsync(p => p.TeamId == teamId && p.Url == url && p.Subpath == null && p.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (existing != null) return (existing, false);

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

        return (pack, true);
    }

    /// <summary>Stage the pack write that accompanies a non-empty import: add the new pack, or refresh the existing pack's ref + sync timestamp. Flushed by the single import save alongside the artifact rows.</summary>
    private void PersistPackSync(Pack pack, bool isNew, string? reference, Guid actorUserId, DateTimeOffset now)
    {
        if (isNew)
        {
            _db.Pack.Add(pack);
            return;
        }

        pack.Reference = reference;
        pack.LastSyncedDate = now;
        pack.LastModifiedDate = now;
        pack.LastModifiedBy = actorUserId;
    }

    private PackArtifactImportResult UpsertAgent(Pack pack, ParsedAgentDefinition parsed, IReadOnlyDictionary<string, AgentDefinition> existingByPath, Guid teamId, Guid actorUserId, DateTimeOffset now)
    {
        if (existingByPath.TryGetValue(parsed.SourcePath, out var existing))
        {
            ApplyAgentContent(existing, parsed);   // refresh content; slug/identity stay put — the handle is stable
            existing.LastModifiedDate = now;
            existing.LastModifiedBy = actorUserId;
            return AgentResult(parsed.SourcePath, PackImportOutcome.Updated, existing.Id);
        }

        if (string.IsNullOrWhiteSpace(parsed.Name))
            return AgentResult(parsed.SourcePath, PackImportOutcome.Skipped, reason: "The agent has no name — nothing to import.");

        // A snapshot's handle is NOT unique (team-slug index is Working-only), so two artifacts deriving the same
        // slug — or a re-imported grandfathered pack whose slug already lives on a Working bench row — both land as
        // distinct Store rows keyed on (pack, source_path). No in-memory dedup needed.
        var slug = AgentDefinitionService.DeriveSlug(parsed.Name);

        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Origin = AgentDefinitionOrigin.Imported,
            Scope = DefinitionScope.Store,
            PackId = pack.Id,
            SourcePath = parsed.SourcePath,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };
        ApplyAgentContent(agent, parsed);

        _db.AgentDefinition.Add(agent);
        return AgentResult(parsed.SourcePath, PackImportOutcome.Imported, agent.Id);
    }

    private PackArtifactImportResult UpsertSkill(Pack pack, ParsedSkillDefinition parsed, IReadOnlyDictionary<string, SkillDefinition> existingByPath, Dictionary<string, Guid> claimedSlugToId, Guid teamId, Guid actorUserId, DateTimeOffset now)
    {
        if (existingByPath.TryGetValue(parsed.SourcePath, out var existing))
        {
            ApplySkillContent(existing, parsed);
            existing.LastModifiedDate = now;
            existing.LastModifiedBy = actorUserId;
            return SkillResult(parsed.SourcePath, PackImportOutcome.Updated, existing.Id);
        }

        if (string.IsNullOrWhiteSpace(parsed.Name))
            return SkillResult(parsed.SourcePath, PackImportOutcome.Skipped, reason: "The SKILL.md has no name — nothing to import.");

        // No handle-collision skip: a snapshot's slug is non-unique (team-slug index is Working-only), so a slug
        // already mapped — by a same-batch skill or a grandfathered Working row — still creates its own Store row.
        var slug = AgentDefinitionService.DeriveSlug(parsed.Name);

        var skill = new SkillDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Origin = SkillDefinitionOrigin.Imported,
            Scope = DefinitionScope.Store,
            PackId = pack.Id,
            SourcePath = parsed.SourcePath,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };
        ApplySkillContent(skill, parsed);

        _db.SkillDefinition.Add(skill);
        claimedSlugToId[slug] = skill.Id;   // make this handle resolvable for an agent's declared skills this batch (last-wins on a dup slug)
        return SkillResult(parsed.SourcePath, PackImportOutcome.Imported, skill.Id);
    }

    /// <summary>The team's active STORE skills as a handle→id map — the resolver for a freshly-imported (store-snapshot)
    /// agent's declared <c>skills:</c>, extended in memory as new store skills are added this batch. A store agent's
    /// declared skills bind to store skills (its own pack's snapshots), never to a Working bench skill, so this is
    /// Store-scoped. A snapshot handle is NOT unique, so two store skills may share a slug; the query orders by
    /// (created, id) and the map keeps the newest — a deterministic last-wins, so the seed is stable across identical
    /// imports (the unscoped/unordered map would clobber by arbitrary DB row order once Working/Store coexist).</summary>
    private async Task<Dictionary<string, Guid>> ActiveSkillSlugToIdAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _db.SkillDefinition.AsNoTracking()
            .Where(s => s.TeamId == teamId && s.Scope == DefinitionScope.Store && s.DeletedDate == null)
            .OrderBy(s => s.CreatedDate).ThenBy(s => s.Id)
            .Select(s => new { s.Slug, s.Id })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in rows) map[row.Slug] = row.Id;   // Store-scoped; on a dup handle the newest (ordered) row wins — deterministic
        return map;
    }

    /// <summary>Bind each freshly-imported agent to the team skills its frontmatter declared (resolving each handle via DeriveSlug against the same-batch handle→id map); a declared handle that matches no team skill is skipped, a duplicate is bound once.</summary>
    private void BindDeclaredSkills(IReadOnlyList<(Guid AgentId, IReadOnlyList<string> Declared)> newAgentSkills, IReadOnlyDictionary<string, Guid> skillSlugToId, Guid actorUserId, DateTimeOffset now)
    {
        foreach (var (agentId, declared) in newAgentSkills)
            foreach (var slug in declared.Select(AgentDefinitionService.DeriveSlug).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal))
                if (skillSlugToId.TryGetValue(slug, out var skillId))
                    _db.AgentSkillBinding.Add(new AgentSkillBinding { Id = Guid.NewGuid(), AgentDefinitionId = agentId, SkillDefinitionId = skillId, CreatedDate = now, CreatedBy = actorUserId });
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

    /// <summary>Github only for the github.com host (trailing-dot normalized); any other allowlisted host is a generic git URL. Pure + internal so it's unit-pinned.</summary>
    internal static PackKind DeterminePackKind(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && string.Equals(uri.Host.TrimEnd('.'), "github.com", StringComparison.OrdinalIgnoreCase)
            ? PackKind.Github
            : PackKind.GitUrl;

    private static async Task<Dictionary<string, T>> ExistingByPathAsync<T>(IQueryable<T> query, Func<T, string> path, CancellationToken cancellationToken)
    {
        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var row in rows) map[path(row)] = row;   // one active row per (pack, source) — the unique index guarantees it
        return map;
    }

    private static Dictionary<string, T> EmptyByPath<T>() => new(StringComparer.Ordinal);

    private static Dictionary<string, T> IndexByPath<T>(IEnumerable<T> items, Func<T, string> path)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items) map[path(item)] = item;   // one entry per file; last wins defensively
        return map;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static PackArtifactImportResult AgentResult(string sourcePath, PackImportOutcome outcome, Guid? id = null, string? reason = null) =>
        new() { SourcePath = sourcePath, Kind = PackArtifactKind.Agent, Outcome = outcome, DefinitionId = id, Reason = reason };

    private static PackArtifactImportResult SkillResult(string sourcePath, PackImportOutcome outcome, Guid? id = null, string? reason = null) =>
        new() { SourcePath = sourcePath, Kind = PackArtifactKind.Skill, Outcome = outcome, DefinitionId = id, Reason = reason };
}

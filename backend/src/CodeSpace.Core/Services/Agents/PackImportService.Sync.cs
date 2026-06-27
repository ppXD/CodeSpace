using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The SYNC half of <see cref="PackImportService"/> — the store's Sync button. Re-clones the pack's SAVED url+ref
/// (the same allowlist-guarded fetch the import uses), re-walks it, and REFRESHES every already-imported artifact
/// in place: its content is re-applied only when a projected field actually changed (so the result honestly
/// splits up-to-date vs updated), and the handle never moves. Discovered artifacts NOT yet imported are returned
/// as a <see cref="PackPreview"/> for the operator to select + add — a sync never auto-imports anything new.
///
/// <para>The whole refresh is ONE <c>SaveChangesAsync</c> inside the command's ambient transaction; a concurrent
/// sync of the same pack loses on the xmin token and rolls back for retry, never half-applies.</para>
/// </summary>
public sealed partial class PackImportService
{
    public async Task<PackSyncResult> SyncAsync(Guid teamId, Guid packId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var pack = await _db.Pack.SingleOrDefaultAsync(p => p.Id == packId && p.TeamId == teamId && p.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Pack {packId} not found or not accessible.");

        if (string.IsNullOrWhiteSpace(pack.Url))
            throw new PackImportException("This pack has no remote source to sync from.");

        using var checkout = await _fetcher.FetchAsync(pack.Url, pack.Reference, cancellationToken).ConfigureAwait(false);

        var discovered = await _walker.WalkAsync(checkout.Directory, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        // Already-imported artifacts (tracked, so a refresh flushes), keyed by source-path = the sync identity.
        var existingAgents = await ExistingByPathAsync(_db.AgentDefinition.Where(a => a.PackId == packId && a.TeamId == teamId && a.DeletedDate == null), a => a.SourcePath!, cancellationToken).ConfigureAwait(false);
        var existingSkills = await ExistingByPathAsync(_db.SkillDefinition.Where(s => s.PackId == packId && s.TeamId == teamId && s.DeletedDate == null), s => s.SourcePath!, cancellationToken).ConfigureAwait(false);

        var upToDate = 0;
        var updated = 0;

        foreach (var parsed in discovered.Agents)
        {
            if (!existingAgents.TryGetValue(parsed.SourcePath, out var row)) continue;

            if (AgentContentEquals(row, parsed)) { upToDate++; continue; }

            ApplyAgentContent(row, parsed);
            row.LastModifiedDate = now;
            row.LastModifiedBy = actorUserId;
            updated++;
        }

        foreach (var parsed in discovered.Skills)
        {
            if (!existingSkills.TryGetValue(parsed.SourcePath, out var row)) continue;

            if (SkillContentEquals(row, parsed)) { upToDate++; continue; }

            ApplySkillContent(row, parsed);
            row.LastModifiedDate = now;
            row.LastModifiedBy = actorUserId;
            updated++;
        }

        // Discovered but not yet imported → a preview to select + add (conflict-checked against the team).
        var agentSlugs = await ActiveSlugsAsync(_db.AgentDefinition.AsNoTracking().Where(a => a.TeamId == teamId && a.DeletedDate == null).Select(a => a.Slug), cancellationToken).ConfigureAwait(false);
        var skillSlugs = await ActiveSlugsAsync(_db.SkillDefinition.AsNoTracking().Where(s => s.TeamId == teamId && s.DeletedDate == null).Select(s => s.Slug), cancellationToken).ConfigureAwait(false);

        var newAgents = discovered.Agents.Where(a => !existingAgents.ContainsKey(a.SourcePath)).Select(a => BuildAgentItem(a, agentSlugs)).ToList();
        var newSkills = discovered.Skills.Where(s => !existingSkills.ContainsKey(s.SourcePath)).Select(s => BuildSkillItem(s, skillSlugs)).ToList();

        pack.LastSyncedDate = now;
        pack.LastModifiedDate = now;
        pack.LastModifiedBy = actorUserId;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new PackSyncResult
        {
            PackId = packId,
            Reference = pack.Reference,
            UpToDate = upToDate,
            Updated = updated,
            NewArtifacts = new PackPreview { Reference = pack.Reference, Agents = newAgents, Skills = newSkills },
        };
    }

    /// <summary>True when the persisted agent matches the parsed artifact on its PROJECTED fields, so re-applying would be a no-op. RawFrontmatter is excluded — jsonb round-trips reorder keys, so comparing it would falsely report "updated"; a refresh re-applies it anyway whenever a projected field changes.</summary>
    private static bool AgentContentEquals(AgentDefinition row, ParsedAgentDefinition parsed) =>
        row.Name == parsed.Name
        && row.Description == parsed.Description
        && row.SystemPrompt == parsed.SystemPrompt
        && row.Model == NullIfBlank(parsed.Model)
        && ToolsEqual(row.ToolsJson, parsed.Tools);

    private static bool SkillContentEquals(SkillDefinition row, ParsedSkillDefinition parsed) =>
        row.Name == parsed.Name
        && row.Description == parsed.Description
        && row.Body == parsed.Body
        && row.Category == NullIfBlank(parsed.Category);

    /// <summary>Compares the tool allow-list as DESERIALIZED lists (not jsonb strings, which round-trip with different formatting). Null (harness default) vs [] (no tools) is a real difference.</summary>
    private static bool ToolsEqual(string? rowJson, IReadOnlyList<string>? parsed)
    {
        var rowList = string.IsNullOrWhiteSpace(rowJson) ? null : JsonSerializer.Deserialize<List<string>>(rowJson, AgentJson.Options);

        if (rowList is null || parsed is null) return rowList is null && parsed is null;

        return rowList.SequenceEqual(parsed);
    }
}

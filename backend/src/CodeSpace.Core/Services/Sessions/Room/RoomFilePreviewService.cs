using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// Default <see cref="IRoomFilePreviewService"/>. Locates the file among the turn's agent runs (joined by the run id,
/// so it covers a supervisor turn's spawned agents AND a plain agent.code turn), resolves that agent's captured unified
/// diff (inline or offloaded), and hands the file's section to the pure <see cref="UnifiedPatchReader"/> — reconstructing
/// full content for an added file, the diff for a modified one, a notice for binary/unavailable. All copy is authored
/// here; the frontend renders by <c>Kind</c>. READ-ONLY.
/// </summary>
public sealed class RoomFilePreviewService : IRoomFilePreviewService, IScopedDependency
{
    private const int MaxAgentsScanned = 200;
    private const int MaxPreviewBytes = 512 * 1024;

    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactOffloader _offloader;

    public RoomFilePreviewService(CodeSpaceDbContext db, IArtifactOffloader offloader)
    {
        _db = db;
        _offloader = offloader;
    }

    public async Task<RoomFilePreview?> PreviewAsync(Guid runId, string path, Guid teamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var owned = await _db.WorkflowRun.AsNoTracking().AnyAsync(r => r.Id == runId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        if (!owned) return null;   // foreign / missing run — indistinguishable not-found

        var target = path.Trim();
        var sourceUrl = await DeliveryUrlAsync(runId, cancellationToken).ConfigureAwait(false);

        var patchRef = await LocateFilePatchAsync(runId, teamId, target, cancellationToken).ConfigureAwait(false);
        if (patchRef is null) return Unavailable(target, sourceUrl, "This file isn't part of the turn's change set.");

        var patch = await _offloader.ResolveAsync(teamId, patchRef.Value.Inline, patchRef.Value.ArtifactId, cancellationToken).ConfigureAwait(false);

        var view = UnifiedPatchReader.Read(patch, target);
        if (view is null) return Unavailable(target, sourceUrl, "This file's change is too large to reconstruct for an inline preview — open it in the pull request.");

        return Project(view, sourceUrl);
    }

    /// <summary>
    /// Scan the turn's ACCEPTED agent runs (a Failed / cancelled agent's rejected diff was never delivered), newest
    /// first, and return the patch reference of the repo that changed <paramref name="path"/> — the newest accepted
    /// writer wins (a retry's re-run supersedes the original). Newest-first ordering also keeps the <see cref="MaxAgentsScanned"/>
    /// window on the LATEST agents, so a late agent's file isn't sliced off.
    /// </summary>
    private async Task<PatchRef?> LocateFilePatchAsync(Guid runId, Guid teamId, string path, CancellationToken cancellationToken)
    {
        var results = await _db.AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.TeamId == teamId && r.ResultJson != null
                && (r.Status == AgentRunStatus.Succeeded || r.Status == AgentRunStatus.NeedsReview))
            .OrderByDescending(r => r.CreatedDate).ThenByDescending(r => r.Id)
            .Select(r => r.ResultJson!)
            .Take(MaxAgentsScanned)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var json in results)
            if (MatchFile(Deserialize(json), path) is { } patchRef) return patchRef;

        return null;
    }

    private static AgentRunResult? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<AgentRunResult>(json, AgentJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>The patch reference (inline text or offloaded id) of the result's repo that changed <paramref name="path"/> — per-repo first, then the single-repo top level.</summary>
    private static PatchRef? MatchFile(AgentRunResult? result, string path)
    {
        if (result is null) return null;

        foreach (var repo in result.RepositoryResults)
            if (repo.ChangedFiles.Contains(path, StringComparer.Ordinal))
                return new PatchRef(repo.Patch, repo.PatchArtifactId);

        return result.ChangedFiles.Contains(path, StringComparer.Ordinal) ? new PatchRef(result.Patch, result.PatchArtifactId) : null;
    }

    private RoomFilePreview Project(PatchFileView view, string? sourceUrl)
    {
        if (view.IsBinary)
            return new RoomFilePreview { Path = view.Path, Kind = "binary", ChangeKind = view.Change.ToString(), SourceUrl = sourceUrl, Note = "Binary file — inline preview isn't available. Open it in the pull request." };

        var isContent = view.Change == PatchFileChange.Added && view.PostImage != null;
        var (text, size, truncated) = Cap(isContent ? view.PostImage! : view.DiffText);

        return new RoomFilePreview
        {
            Path = view.Path,
            Kind = isContent ? "text" : "diff",
            ChangeKind = view.Change.ToString(),
            Text = text,
            SizeBytes = size,
            Truncated = truncated,
            SourceUrl = sourceUrl,
            Note = truncated ? "Preview truncated — download or open in the pull request for the full file." : null,
        };
    }

    /// <summary>Bound the preview body to <see cref="MaxPreviewBytes"/> (a preview, not a byte-exact stream); report the real size + whether it was cut.</summary>
    private static (string Text, long Size, bool Truncated) Cap(string body)
    {
        var bytes = Encoding.UTF8.GetByteCount(body);

        if (bytes <= MaxPreviewBytes) return (body, bytes, false);

        var capped = body[..Math.Min(body.Length, MaxPreviewBytes)];
        return (capped, bytes, capped.Length < body.Length);
    }

    private static RoomFilePreview Unavailable(string path, string? sourceUrl, string note) =>
        new() { Path = path, Kind = "unavailable", SourceUrl = sourceUrl, Note = note };

    /// <summary>The turn's delivered PR url (the fallback link for a binary / unavailable file), or null when it opened none.</summary>
    private async Task<string?> DeliveryUrlAsync(Guid runId, CancellationToken cancellationToken)
    {
        var nodes = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId)
            .Select(n => new { n.OutputsJson, n.InputsJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return nodes.Select(n => RoomDeliveryParser.Parse(n.OutputsJson, n.InputsJson)).FirstOrDefault(d => d != null)?.Url;
    }

    private readonly record struct PatchRef(string? Inline, Guid? ArtifactId);
}

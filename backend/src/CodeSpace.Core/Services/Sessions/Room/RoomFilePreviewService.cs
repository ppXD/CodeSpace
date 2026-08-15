using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// Default <see cref="IRoomFilePreviewService"/>. Locates the file among the turn's agent runs (joined by the run id,
/// so it covers a supervisor turn's spawned agents AND a plain agent.run turn), resolves that agent's captured unified
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

    public async Task<RoomFilePreview?> PreviewAsync(Guid runId, string path, Guid teamId, Guid? agentRunId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var owned = await _db.WorkflowRun.AsNoTracking().AnyAsync(r => r.Id == runId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        if (!owned) return null;   // foreign / missing run — indistinguishable not-found

        var target = path.Trim();
        var sourceUrl = await DeliveryUrlAsync(runId, cancellationToken).ConfigureAwait(false);

        var patchRef = await LocateFilePatchAsync(runId, teamId, target, agentRunId, cancellationToken).ConfigureAwait(false);
        if (patchRef is null) return Unavailable(target, sourceUrl, RoomFileUnavailableReason.NotInChangeSet, "This file isn't part of the turn's change set.");

        var patch = await ResolvePatchAsync(teamId, patchRef.Value, cancellationToken).ConfigureAwait(false);
        if (patch is PatchResolution.Unavailable unavailable)
            return Unavailable(target, sourceUrl, Reason(unavailable.Kind), StorageNote(unavailable.Kind, sourceUrl != null));

        var view = UnifiedPatchReader.Read(((PatchResolution.Found)patch).Text, target);
        if (view is null)
            return Unavailable(target, sourceUrl, RoomFileUnavailableReason.ReconstructionUnavailable, WithSourceFallback("This file's saved change cannot be reconstructed for a safe inline preview.", sourceUrl != null));

        return Project(view, sourceUrl);
    }

    /// <summary>
    /// Return the patch reference of the repo that changed <paramref name="path"/>. <paramref name="agentRunId"/> is a
    /// PREFERRED scope (per-agent attribution — open an agent, preview ITS file, any terminal status): try that agent's
    /// own version first. But a RESULT-card attribution is a last-writer-wins guess from a separately-capped per-agent
    /// map, so it can point at an agent whose durable change set doesn't carry the path — when the scoped lookup MISSES,
    /// fall through (never give up on a legitimately-produced file). The fallback scans the turn's ACCEPTED agent runs
    /// (a Failed / cancelled agent's rejected diff was never delivered) newest first — the newest accepted writer wins
    /// (a retry supersedes the original); newest-first also keeps the <see cref="MaxAgentsScanned"/> window on the
    /// LATEST agents, so a late agent's file isn't sliced off.
    /// </summary>
    private async Task<PatchRef?> LocateFilePatchAsync(Guid runId, Guid teamId, string path, Guid? agentRunId, CancellationToken cancellationToken)
    {
        var query = _db.AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.TeamId == teamId && r.ResultJson != null);

        if (agentRunId is { } id
            && MatchFile(Deserialize(await query.Where(r => r.Id == id).Select(r => r.ResultJson!).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? ""), path) is { } scoped)
            return scoped;

        var results = await query
            .Where(r => r.Status == AgentRunStatus.Succeeded || r.Status == AgentRunStatus.NeedsReview)
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

    /// <summary>
    /// Resolve the captured patch text (inline, or the offloaded blob) into a typed availability fact. Storage metadata,
    /// missing bytes, corruption, authorization and transient backend failures remain distinct; none is normal "expiry".
    /// Cancellation and programming faults still surface.
    /// </summary>
    private async Task<PatchResolution> ResolvePatchAsync(Guid teamId, PatchRef patchRef, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(patchRef.Inline) && patchRef.ArtifactId is null)
            return new PatchResolution.Unavailable(ArtifactContentUnavailableKind.MetadataMissing);

        try
        {
            return new PatchResolution.Found(await _offloader.ResolveRequiredAsync(teamId, patchRef.Inline, patchRef.ArtifactId, cancellationToken).ConfigureAwait(false));
        }
        catch (ArtifactContentUnavailableException exception)
        {
            return new PatchResolution.Unavailable(exception.Kind);
        }
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
            return new RoomFilePreview { Path = view.Path, Kind = "binary", ChangeKind = view.Change.ToString(), SourceUrl = sourceUrl, Note = WithSourceFallback("Binary file — inline preview isn't available.", sourceUrl != null) };

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
            Note = truncated ? WithSourceFallback("Preview truncated.", sourceUrl != null) : null,
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

    private static RoomFilePreview Unavailable(string path, string? sourceUrl, RoomFileUnavailableReason reason, string note) =>
        new() { Path = path, Kind = "unavailable", SourceUrl = sourceUrl, Note = note, UnavailableReason = reason };

    private static RoomFileUnavailableReason Reason(ArtifactContentUnavailableKind kind) => kind switch
    {
        ArtifactContentUnavailableKind.MetadataMissing => RoomFileUnavailableReason.MetadataMissing,
        ArtifactContentUnavailableKind.PhysicalObjectMissing => RoomFileUnavailableReason.PhysicalObjectMissing,
        ArtifactContentUnavailableKind.IntegrityFailure => RoomFileUnavailableReason.IntegrityFailure,
        ArtifactContentUnavailableKind.AccessDenied => RoomFileUnavailableReason.AccessDenied,
        _ => RoomFileUnavailableReason.BackendUnavailable,
    };

    private static string StorageNote(ArtifactContentUnavailableKind kind, bool hasSource) => WithSourceFallback(kind switch
    {
        ArtifactContentUnavailableKind.MetadataMissing => "The saved patch metadata is unavailable.",
        ArtifactContentUnavailableKind.PhysicalObjectMissing => "The saved patch's stored bytes are missing from the configured artifact backend.",
        ArtifactContentUnavailableKind.IntegrityFailure => "The saved patch failed integrity verification and cannot be opened safely.",
        ArtifactContentUnavailableKind.AccessDenied => "The configured artifact backend denied access to the saved patch.",
        _ => "The configured artifact backend is temporarily unavailable.",
    }, hasSource);

    private static string WithSourceFallback(string note, bool hasSource) => hasSource ? note + " Open the delivered pull request to view the file." : note;

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

    private abstract record PatchResolution
    {
        private PatchResolution() { }
        public sealed record Found(string Text) : PatchResolution;
        public sealed record Unavailable(ArtifactContentUnavailableKind Kind) : PatchResolution;
    }
}

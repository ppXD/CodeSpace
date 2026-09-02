using System.Buffers;
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
    private const int MaxManifestMatches = 4000;
    private const int MaxPreviewBytes = 512 * 1024;

    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactOffloader _offloader;

    public RoomFilePreviewService(CodeSpaceDbContext db, IArtifactOffloader offloader)
    {
        _db = db;
        _offloader = offloader;
    }

    public Task<RoomFilePreview?> PreviewAsync(Guid runId, string path, Guid teamId, Guid? agentRunId, CancellationToken cancellationToken) =>
        PreviewAsync(runId, new RoomFileIdentity { Path = path, AgentRunId = agentRunId }, teamId, cancellationToken);

    public async Task<RoomFilePreview?> PreviewAsync(Guid runId, RoomFileIdentity identity, Guid teamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identity.Path)) return null;

        var owned = await _db.WorkflowRun.AsNoTracking().AnyAsync(r => r.Id == runId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        if (!owned) return null;   // foreign / missing run — indistinguishable not-found

        var requested = identity with { Path = identity.Path.Trim(), RepositoryAlias = string.IsNullOrEmpty(identity.RepositoryAlias) ? null : identity.RepositoryAlias };
        var sourceUrl = await DeliveryUrlAsync(runId, cancellationToken).ConfigureAwait(false);

        var location = await LocateFilePatchAsync(runId, teamId, requested, cancellationToken).ConfigureAwait(false);
        if (location is FileLocation.Ambiguous)
            return Unavailable(requested, sourceUrl, RoomFileUnavailableReason.AmbiguousRepository, "More than one repository changed this path. Open the file from a repository-attributed row.");
        if (location is not FileLocation.Found found)
            return Unavailable(requested, sourceUrl, RoomFileUnavailableReason.NotInChangeSet, "This file isn't part of the selected repository's change set.");

        var patch = await ResolvePatchAsync(teamId, found.Candidate.Patch, cancellationToken).ConfigureAwait(false);
        if (patch is PatchResolution.Unavailable unavailable)
            return Unavailable(found.Candidate.Identity, sourceUrl, Reason(unavailable.Kind),
                StorageNote(unavailable.Kind, unavailable.Detail, sourceUrl != null));

        var view = UnifiedPatchReader.Read(((PatchResolution.Found)patch).Text, requested.Path);
        if (view is null)
            return Unavailable(found.Candidate.Identity, sourceUrl, RoomFileUnavailableReason.ReconstructionUnavailable, WithSourceFallback("This file's saved change cannot be reconstructed for a safe inline preview.", sourceUrl != null));

        return Project(view, found.Candidate.Identity, sourceUrl);
    }

    /// <summary>
    /// Return the patch reference of the repo that changed the requested path. <see cref="RoomFileIdentity.AgentRunId"/> is a
    /// preferred scope for a legacy path-only read and an exact scope when repository identity is present: try that agent's
    /// own version first. But a RESULT-card attribution is a last-writer-wins guess from a separately-capped per-agent
    /// map, so it can point at an agent whose durable change set doesn't carry the path — when the scoped lookup MISSES,
    /// fall through (never give up on a legitimately-produced file). The fallback scans the turn's ACCEPTED agent runs
    /// (a Failed / cancelled agent's rejected diff was never delivered) newest first — the newest accepted writer wins
    /// (a retry supersedes the original); newest-first also keeps the <see cref="MaxAgentsScanned"/> window on the
    /// LATEST agents, so a late agent's file isn't sliced off.
    /// </summary>
    private async Task<FileLocation> LocateFilePatchAsync(Guid runId, Guid teamId, RoomFileIdentity requested, CancellationToken cancellationToken)
    {
        var query = _db.AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.TeamId == teamId && r.ResultJson != null);

        if (requested.AgentRunId is { } id)
        {
            var scoped = await query.Where(r => r.Id == id)
                .Select(r => new AgentResultRow(r.Id, r.ResultJson!))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (scoped is not null)
            {
                var manifests = await ReadManifestMatchesAsync(new[] { id }, requested, cancellationToken).ConfigureAwait(false);
                if (manifests.Truncated) return new FileLocation.Ambiguous();

                var selected = Select(Candidates(scoped, manifests.Rows, requested.Path), requested);
                if (selected is not FileLocation.Missing) return selected;
            }

            // A repository-attributed click is an exact identity, not a hint. Never fall through to a sibling agent.
            if (HasRepositoryScope(requested)) return new FileLocation.Missing();
        }

        var results = await query
            .Where(r => r.Status == AgentRunStatus.Succeeded || r.Status == AgentRunStatus.NeedsReview)
            .OrderByDescending(r => r.CreatedDate).ThenByDescending(r => r.Id)
            .Select(r => new AgentResultRow(r.Id, r.ResultJson!))
            .Take(MaxAgentsScanned)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var manifestMatches = await ReadManifestMatchesAsync(results.Select(r => r.AgentRunId).ToList(), requested, cancellationToken).ConfigureAwait(false);
        if (manifestMatches.Truncated) return new FileLocation.Ambiguous();

        var manifestsByAgent = manifestMatches.Rows.ToLookup(m => m.AgentRunId);
        var candidates = results.SelectMany(row => Candidates(row, manifestsByAgent[row.AgentRunId].ToList(), requested.Path)).ToList();

        // Preserve the legacy attribution-hint contract: when a path-only scoped agent did not actually carry the
        // file, search the accepted turn without that stale agent hint. Repository-attributed requests returned above.
        var fallback = HasRepositoryScope(requested) ? requested : requested with { AgentRunId = null };
        return Select(candidates, fallback);
    }

    /// <summary>One bounded manifest query for every candidate agent; the selected patch alone is hydrated later.</summary>
    private async Task<ManifestMatches> ReadManifestMatchesAsync(IReadOnlyCollection<Guid> agentRunIds, RoomFileIdentity requested, CancellationToken cancellationToken)
    {
        if (agentRunIds.Count == 0) return new ManifestMatches(Array.Empty<ManifestMatch>(), false);

        var pathNeedle = JsonSerializer.Serialize(new[] { requested.Path }, AgentJson.Options);
        var query = _db.PublishManifest.AsNoTracking()
            .Where(m => m.Kind == PublishManifestKind.Agent && m.AgentRunId != null && agentRunIds.Contains(m.AgentRunId.Value)
                && m.ChangedFilesJson != null && EF.Functions.JsonContains(m.ChangedFilesJson, pathNeedle));

        if (requested.RepositoryId is { } repositoryId) query = query.Where(m => m.RepositoryId == repositoryId);
        if (requested.RepositoryAlias is { Length: > 0 } alias) query = query.Where(m => m.RepositoryAlias == alias);

        var rows = await query
            .OrderByDescending(m => m.LastModifiedDate).ThenBy(m => m.Id)
            .Select(m => new ManifestMatch(m.AgentRunId!.Value, m.RepositoryId, m.RepositoryAlias, m.PatchArtifactId, m.PatchLossReason))
            .Take(MaxManifestMatches + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Count > MaxManifestMatches
            ? new ManifestMatches(rows.Take(MaxManifestMatches).ToList(), true)
            : new ManifestMatches(rows, false);
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
            return new PatchResolution.Unavailable(ArtifactContentUnavailableKind.MetadataMissing, Detail: null);

        try
        {
            return new PatchResolution.Found(await _offloader.ResolveRequiredAsync(teamId, patchRef.Inline, patchRef.ArtifactId, cancellationToken).ConfigureAwait(false));
        }
        catch (ArtifactContentUnavailableException exception)
        {
            return new PatchResolution.Unavailable(exception.Kind, exception.Detail);
        }
    }

    /// <summary>Build identity-bearing candidates per agent. Per-repo results always win; top-level is legacy/single-repo only.</summary>
    private static IReadOnlyList<FileCandidate> Candidates(AgentResultRow row, IReadOnlyList<ManifestMatch> manifests, string path)
    {
        var result = Deserialize(row.ResultJson);
        var candidates = new List<FileCandidate>();

        if (result?.RepositoryResults is { Count: > 0 } repositories)
        {
            var consumed = new HashSet<ManifestMatch>();

            foreach (var repository in repositories.Where(repository => repository.ChangedFiles.Contains(path, StringComparer.Ordinal)))
            {
                var matches = manifests.Where(manifest => string.Equals(manifest.RepositoryAlias, repository.Alias, StringComparison.Ordinal)
                    && (manifest.RepositoryId is null || repository.RepositoryId is null || manifest.RepositoryId == repository.RepositoryId)).ToList();
                var manifest = matches.Count == 1 ? matches[0] : null;
                if (manifest is not null) consumed.Add(manifest);

                candidates.Add(new FileCandidate(
                    new RoomFileIdentity { Path = path, AgentRunId = row.AgentRunId, RepositoryId = repository.RepositoryId ?? manifest?.RepositoryId, RepositoryAlias = repository.Alias },
                    PatchFrom(repository.Patch, repository.PatchArtifactId, manifest, resultLossReason: null)));
            }

            foreach (var manifest in manifests.Where(manifest => !consumed.Contains(manifest)))
                candidates.Add(ManifestCandidate(row.AgentRunId, path, manifest));

            return candidates;
        }

        if (result?.ChangedFiles.Contains(path, StringComparer.Ordinal) == true)
        {
            if (manifests.Count == 0)
                candidates.Add(new FileCandidate(new RoomFileIdentity { Path = path, AgentRunId = row.AgentRunId }, new PatchRef(result.Patch, result.PatchArtifactId, result.PatchLossReason)));
            else
                candidates.AddRange(manifests.Select(manifest => new FileCandidate(
                    Identity(row.AgentRunId, path, manifest), PatchFrom(result.Patch, result.PatchArtifactId, manifest, result.PatchLossReason))));
        }
        else
        {
            candidates.AddRange(manifests.Select(manifest => ManifestCandidate(row.AgentRunId, path, manifest)));
        }

        return candidates;
    }

    private static FileCandidate ManifestCandidate(Guid agentRunId, string path, ManifestMatch manifest) =>
        new(Identity(agentRunId, path, manifest), new PatchRef("", manifest.PatchArtifactId, manifest.PatchLossReason));

    private static RoomFileIdentity Identity(Guid agentRunId, string path, ManifestMatch manifest) =>
        new() { Path = path, AgentRunId = agentRunId, RepositoryId = manifest.RepositoryId, RepositoryAlias = manifest.RepositoryAlias };

    private static PatchRef PatchFrom(string? inline, Guid? artifactId, ManifestMatch? manifest, string? resultLossReason) =>
        manifest?.PatchArtifactId is { } manifestArtifactId ? new PatchRef("", manifestArtifactId, manifest.PatchLossReason) : new PatchRef(inline, artifactId, manifest?.PatchLossReason ?? resultLossReason);

    private static FileLocation Select(IReadOnlyList<FileCandidate> candidates, RoomFileIdentity requested)
    {
        var matches = candidates.Where(candidate =>
            string.Equals(candidate.Identity.Path, requested.Path, StringComparison.Ordinal)
            && (requested.AgentRunId is null || candidate.Identity.AgentRunId == requested.AgentRunId)
            && (requested.RepositoryId is null || candidate.Identity.RepositoryId == requested.RepositoryId)
            && (requested.RepositoryAlias is null || string.Equals(candidate.Identity.RepositoryAlias, requested.RepositoryAlias, StringComparison.Ordinal)))
            .ToList();

        if (matches.Count == 0) return new FileLocation.Missing();

        // One agent cannot truthfully own two carriers for the same repository/path identity. Across DIFFERENT
        // agents, however, the rows are versions and their newest-first query order deliberately picks the latest.
        if (matches.GroupBy(candidate => new CandidateIdentity(candidate.Identity.AgentRunId, candidate.Identity.RepositoryId, candidate.Identity.RepositoryAlias)).Any(group => group.Count() > 1))
            return new FileLocation.Ambiguous();

        var locations = matches.Select(candidate => new RepositoryLocation(candidate.Identity.RepositoryId, candidate.Identity.RepositoryAlias)).Distinct().Take(2).Count();
        return locations > 1 ? new FileLocation.Ambiguous() : new FileLocation.Found(matches[0]);
    }

    private static bool HasRepositoryScope(RoomFileIdentity identity) => identity.RepositoryId is not null || identity.RepositoryAlias is { Length: > 0 };

    private RoomFilePreview Project(PatchFileView view, RoomFileIdentity identity, string? sourceUrl)
    {
        if (view.IsBinary)
            return new RoomFilePreview { Path = view.Path, Identity = identity, Kind = "binary", ChangeKind = view.Change.ToString(), SourceUrl = sourceUrl, Note = WithSourceFallback("Binary file — inline preview isn't available.", sourceUrl != null) };

        var isContent = view.Change == PatchFileChange.Added && view.PostImage != null;
        var (text, size, truncated) = Cap(isContent ? view.PostImage! : view.DiffText);

        return new RoomFilePreview
        {
            Path = view.Path,
            Identity = identity,
            Kind = isContent ? "text" : "diff",
            ChangeKind = view.Change.ToString(),
            Text = text,
            SizeBytes = size,
            Truncated = truncated,
            SourceUrl = sourceUrl,
            Note = truncated ? WithSourceFallback("Preview truncated.", sourceUrl != null) : null,
        };
    }

    /// <summary>Bound the preview body to <see cref="MaxPreviewBytes"/> UTF-8 bytes (a preview, not a byte-exact stream); report the complete encoded size and cut only between Unicode scalar values.</summary>
    internal static (string Text, long Size, bool Truncated) Cap(string body)
    {
        var bytes = Encoding.UTF8.GetByteCount(body);

        if (bytes <= MaxPreviewBytes) return (body, bytes, false);

        var remaining = body.AsSpan();
        var previewBytes = 0;
        var previewChars = 0;

        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var charsConsumed);
            if (status != OperationStatus.Done || rune.Utf8SequenceLength > MaxPreviewBytes - previewBytes) break;

            previewBytes += rune.Utf8SequenceLength;
            previewChars += charsConsumed;
            remaining = remaining[charsConsumed..];
        }

        return (body[..previewChars], bytes, true);
    }

    private static RoomFilePreview Unavailable(RoomFileIdentity identity, string? sourceUrl, RoomFileUnavailableReason reason, string note) =>
        new() { Path = identity.Path, Identity = identity, Kind = "unavailable", SourceUrl = sourceUrl, Note = note, UnavailableReason = reason };

    private static RoomFileUnavailableReason Reason(ArtifactContentUnavailableKind kind) => kind switch
    {
        ArtifactContentUnavailableKind.MetadataMissing => RoomFileUnavailableReason.MetadataMissing,
        ArtifactContentUnavailableKind.PhysicalObjectMissing => RoomFileUnavailableReason.PhysicalObjectMissing,
        ArtifactContentUnavailableKind.IntegrityFailure => RoomFileUnavailableReason.IntegrityFailure,
        ArtifactContentUnavailableKind.AccessDenied => RoomFileUnavailableReason.AccessDenied,
        _ => RoomFileUnavailableReason.BackendUnavailable,
    };

    private static string StorageNote(ArtifactContentUnavailableKind kind, string? detail, bool hasSource) => WithSourceFallback(kind switch
    {
        ArtifactContentUnavailableKind.MetadataMissing => "The saved patch metadata is unavailable.",
        ArtifactContentUnavailableKind.PhysicalObjectMissing => "The saved patch's stored bytes are missing from the configured artifact backend.",
        ArtifactContentUnavailableKind.IntegrityFailure => "The saved patch failed integrity verification and cannot be opened safely.",
        ArtifactContentUnavailableKind.AccessDenied => "The configured artifact backend denied access to the saved patch.",
        _ => "The configured artifact backend is temporarily unavailable.",
    } + (detail is { Length: > 0 } ? $" Ledger: {detail}." : ""), hasSource);

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

    private readonly record struct PatchRef(string? Inline, Guid? ArtifactId, string? LossReason = null);
    private sealed record AgentResultRow(Guid AgentRunId, string ResultJson);
    private sealed record ManifestMatch(Guid AgentRunId, Guid? RepositoryId, string RepositoryAlias, Guid? PatchArtifactId, string? PatchLossReason);
    private sealed record ManifestMatches(IReadOnlyList<ManifestMatch> Rows, bool Truncated);
    private sealed record FileCandidate(RoomFileIdentity Identity, PatchRef Patch);
    private sealed record CandidateIdentity(Guid? AgentRunId, Guid? RepositoryId, string? RepositoryAlias);
    private sealed record RepositoryLocation(Guid? RepositoryId, string? RepositoryAlias);

    private abstract record FileLocation
    {
        private FileLocation() { }
        public sealed record Found(FileCandidate Candidate) : FileLocation;
        public sealed record Missing : FileLocation;
        public sealed record Ambiguous : FileLocation;
    }

    private abstract record PatchResolution
    {
        private PatchResolution() { }
        public sealed record Found(string Text) : PatchResolution;
        public sealed record Unavailable(ArtifactContentUnavailableKind Kind, string? Detail) : PatchResolution;
    }
}

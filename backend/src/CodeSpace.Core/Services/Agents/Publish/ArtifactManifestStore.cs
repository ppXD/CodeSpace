using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Publish;

public interface IArtifactManifestStore
{
    /// <summary>
    /// Capture the run's DECLARED deliverable paths (a non-<c>TestsPass</c> acceptance's <c>Command</c> list is
    /// literally the workspace-relative deliverable list) from the workspace into the CAS store, minting one typed
    /// <see cref="ArtifactManifest"/> row per path — idempotent per <c>(attempt, epoch, path)</c>: a re-capture of
    /// the same coordinates supersedes the prior row (a pointer, never a rewrite). Best-effort by contract: a
    /// missing/escaping path is skipped (the ACCEPTANCE oracle is the one that fails the run over it — this layer
    /// only preserves what exists), and the caller treats any throw as a capture hiccup, never a run failure.
    /// Returns how many artifacts were captured — what was OWED is not this method's to answer. The capture promise
    /// states the declared list at intent time and its facts re-derive that count from the SAME acceptance, so a
    /// shortfall stays visible on an attempt whose capture never ran at all, which no answer from here could cover.
    /// Every non-file refusal is accounted for with a warning naming the cause. A real file has no capture-size
    /// ceiling at this seam: it streams through the artifact store, so there is no bound-exceeded loss to record.
    /// </summary>
    Task<int> CaptureDeclaredAsync(AgentTask task, string workspaceDirectory, Guid agentRunId, Guid? workflowRunId, Guid teamId, long fenceEpoch, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArtifactManifest>> ListForAgentRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArtifactManifest>> ListForWorkflowRunAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);
}

/// <summary>
/// DC-4 slice 1: the typed-artifact ledger — the first path that puts an agent-produced FILE into the store as
/// itself (every prior write site stored byproducts: patches, transcripts, evidence blobs). Capture rides the
/// same hardened containment the graders use (<see cref="WorkspaceArtifactGuard"/> — <c>../</c>, absolute paths
/// and escaping symlinks all read as missing), and the run's own declared paths — no workspace walker, no new
/// security surface. Exact files stream through a re-readable source, so capture memory is fixed instead of
/// proportional to deliverable size.
/// </summary>
public sealed class ArtifactManifestStore : IArtifactManifestStore, IScopedDependency
{
    /// <summary>Legacy byte-guard threshold kept for compatibility tests; durable manifest capture is streaming and does not impose this former heap bound.</summary>
    public const long MaxArtifactBytes = 4 * 1024 * 1024;

    /// <summary>The holder this store's retention declarations name. Diagnostic only — the reaper checks every reference site regardless of what a declaration claims.</summary>
    public const string HolderKind = "artifact_manifest";

    /// <summary>Historical capture-source value retained for readers of pre-streaming bound-exceeded rows; this store no longer produces new gaps.</summary>
    public const string CompletenessCaptureSource = "artifact-manifest-store";

    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactStreamRetentionWriter _retention;
    private readonly ILogger<ArtifactManifestStore> _logger;

    public ArtifactManifestStore(CodeSpaceDbContext db, IArtifactStreamRetentionWriter retention, ILogger<ArtifactManifestStore> logger)
    {
        _db = db;
        _retention = retention;
        _logger = logger;
    }

    public async Task<int> CaptureDeclaredAsync(AgentTask task, string workspaceDirectory, Guid agentRunId, Guid? workflowRunId, Guid teamId, long fenceEpoch, CancellationToken cancellationToken)
    {
        var paths = DeclaredDeliverablePaths(task);

        if (paths.Count == 0) return 0;

        var captured = 0;

        foreach (var path in paths)
        {
            if (!WorkspaceArtifactGuard.TryResolveFileWithin(workspaceDirectory, path, out var file, out var failure))
            {
                NoticeSkip(new DeclaredDeliverableSkip(agentRunId, path, failure!.Value));
                continue;
            }

            // Declaring write (see IArtifactRetentionWriter): content_artifact_id below is the ONLY reference this
            // method writes, and it is written AFTER the bytes land, so a throw in between leaves bytes nothing ever
            // pointed at. The declaration is what lets the retention reaper reclaim exactly those. A dedup hit declares
            // nothing — the bytes are then shared with a producer whose references are not enumerable — so this call is
            // safe to make unconditionally.
            ArtifactStreamRetentionWrite write;
            using (var source = new WorkspaceArtifactSource(file))
                write = await _retention.PutDeclaredAsync(Declaration(teamId, source, path, agentRunId), cancellationToken).ConfigureAwait(false);
            var artifactId = write.ArtifactId;

            await UpsertAsync(new ArtifactManifest
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                AgentRunId = agentRunId,
                WorkflowRunId = workflowRunId,
                FenceEpoch = fenceEpoch,
                Kind = KindFor(path),
                LogicalPath = path,
                ContentArtifactId = artifactId,
                Sha256 = write.Sha256,
                SizeBytes = write.SizeBytes,
                ContentType = ContentTypeFor(path),
            }, cancellationToken).ConfigureAwait(false);

            captured++;
        }

        return captured;
    }

    /// <summary>The declaring write's request for one captured deliverable. The holder it names is the <c>artifact_manifest</c> row the caller writes next.</summary>
    private static ArtifactStreamRetentionWriteRequest Declaration(Guid teamId, WorkspaceArtifactSource source, string path, Guid agentRunId) =>
        new(new ArtifactStreamWriteRequest(teamId, ContentTypeFor(path), source), ArtifactRetentionClass.ArtifactManifestContent, HolderKind, agentRunId);

    /// <summary>The workspace-relative deliverable list a non-<c>TestsPass</c> acceptance declares — <c>TestsPass</c> (or an absent kind, which defaults to it) carries an ARGV, never paths, so it declares nothing capturable.</summary>
    internal static IReadOnlyList<string> DeclaredDeliverablePaths(AgentTask task) =>
        task.Acceptance is { Kind: not null and not Messages.Agents.Benchmark.BenchmarkGradingKind.TestsPass } spec
            ? spec.Command.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList()
            : Array.Empty<string>();

    /// <summary>
    /// Missing and non-file paths are the acceptance oracle's verdict to give, not a capture loss, so each stays a
    /// warning. Size is deliberately absent: a file that resolves here takes the streaming path regardless of length.
    /// </summary>
    private void NoticeSkip(DeclaredDeliverableSkip skip) =>
        _logger.LogWarning("Agent run {RunId}: declared deliverable '{Path}' not captured — {Failure}: {Cause}", skip.AgentRunId, skip.Path, skip.Failure, CauseOf(skip.Failure));

    /// <summary>
    /// What the resolver's refusal actually was, one sentence per reachable arm. <c>OverCap</c> remains in the shared
    /// byte-reader enum for bounded graders, but this streaming capture never asks that reader to impose a cap.
    /// </summary>
    private static string CauseOf(WorkspaceArtifactReadFailure failure) => failure switch
    {
        WorkspaceArtifactReadFailure.Missing => "nothing readable exists at that path inside the workspace",
        WorkspaceArtifactReadFailure.NotAFile => "the path resolves to a directory, which is not readable content",
        _ => "the workspace guard refused to read it",
    };

    /// <summary>Idempotent per <c>(attempt, epoch, path)</c>: an existing CURRENT row for the same coordinates is superseded by the fresh one — a pointer, never a rewrite (the #1352 discipline), so history stays intact and consumers follow the unsuperseded row.</summary>
    private async Task UpsertAsync(ArtifactManifest fresh, CancellationToken cancellationToken)
    {
        var prior = await _db.ArtifactManifest
            .Where(m => m.AgentRunId == fresh.AgentRunId && m.FenceEpoch == fresh.FenceEpoch && m.LogicalPath == fresh.LogicalPath && m.SupersededByManifestId == null)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // Same coordinates, same bytes ⇒ the exactly-once no-op (a re-compose/re-capture lands on the first row).
        if (prior is not null && prior.Sha256 == fresh.Sha256) return;

        // TWO steps, retire-then-install: the current-rows-only unique index means the fresh row can't insert
        // while the prior is still current, and EF's statement ordering inside one SaveChanges is not a contract.
        // fresh.Id is pre-generated, so the pointer written first stays consistent; a crash between the steps
        // leaves a visibly dangling pointer (no current row) — fail-visible, and the next capture self-heals it.
        if (prior is not null)
        {
            prior.SupersededByManifestId = fresh.Id;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _db.ArtifactManifest.Add(fresh);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ArtifactManifest>> ListForAgentRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.ArtifactManifest.AsNoTracking()
            .Where(m => m.AgentRunId == agentRunId && m.TeamId == teamId)
            .OrderBy(m => m.LogicalPath)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ArtifactManifest>> ListForWorkflowRunAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.ArtifactManifest.AsNoTracking()
            .Where(m => m.WorkflowRunId == workflowRunId && m.TeamId == teamId)
            .OrderBy(m => m.LogicalPath)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Extension → kind, honest-default <see cref="ArtifactManifestKind.Other"/> — a kind is a routing hint for consumers, never a verdict.</summary>
    internal static ArtifactManifestKind KindFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".txt" or ".rst" or ".html" or ".pdf" or ".docx" => ArtifactManifestKind.Document,
        ".svg" or ".mmd" or ".drawio" or ".puml" or ".png" => ArtifactManifestKind.Diagram,
        ".csv" or ".tsv" or ".json" or ".jsonl" or ".parquet" or ".xlsx" => ArtifactManifestKind.Dataset,
        _ => ArtifactManifestKind.Other,
    };

    /// <summary>Extension → MIME, best-effort (the CAS store does not validate it against the bytes).</summary>
    internal static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" => "text/markdown",
        ".txt" or ".rst" or ".puml" or ".mmd" => "text/plain",
        ".html" => "text/html",
        ".pdf" => "application/pdf",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".csv" => "text/csv",
        ".tsv" => "text/tab-separated-values",
        ".json" or ".drawio" => "application/json",
        ".jsonl" => "application/x-ndjson",
        ".parquet" => "application/vnd.apache.parquet",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// One declared path the capture did not take, and the coordinates the notice needs to say so.
    /// <para>Rule 18.1 sends data classes to <c>Messages</c>; this one deliberately stays. It crosses no seam — it is
    /// this class's own parameter bundle, the sanctioned way to keep <see cref="NoticeSkipAsync"/> inside the
    /// five-parameter cap, and it is private and single-use. Publishing it would widen the message contract with a
    /// type no consumer can name, which is the cost the rule exists to avoid, not incur.</para>
    /// </summary>
    private sealed record DeclaredDeliverableSkip(Guid AgentRunId, string Path, WorkspaceArtifactReadFailure Failure);

    /// <summary>
    /// Re-reads the exact handle the workspace guard admitted, never its mutable path. Each pass gets an independent
    /// positional cursor; local and routed writers still verify the admitted digest before committing placement, so a
    /// same-inode content mutation between passes also fails closed.
    /// </summary>
    private sealed class WorkspaceArtifactSource : IArtifactWriteSource, IDisposable
    {
        private WorkspaceArtifactFile? _file;

        public WorkspaceArtifactSource(WorkspaceArtifactFile file)
        {
            _file = file;
            LengthBytes = file.LengthBytes;
        }

        public long LengthBytes { get; }

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_file?.OpenRead() ?? throw new ObjectDisposedException(nameof(WorkspaceArtifactSource)));
        }

        public void Dispose() => Interlocked.Exchange(ref _file, null)?.Dispose();
    }
}

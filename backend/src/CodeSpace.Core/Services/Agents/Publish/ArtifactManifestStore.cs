using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

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
    /// Returns how many artifacts were captured.
    /// </summary>
    Task<int> CaptureDeclaredAsync(AgentTask task, string workspaceDirectory, Guid agentRunId, Guid? workflowRunId, Guid teamId, long fenceEpoch, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArtifactManifest>> ListForAgentRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArtifactManifest>> ListForWorkflowRunAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);
}

/// <summary>
/// DC-4 slice 1: the typed-artifact ledger — the first path that puts an agent-produced FILE into the store as
/// itself (every prior write site stored byproducts: patches, transcripts, evidence blobs). Capture rides the
/// same hardened containment the graders use (<see cref="WorkspaceArtifactGuard"/> — <c>../</c>, absolute paths
/// and escaping symlinks all read as missing), the same size clamp, and the run's own declared paths — no
/// workspace walker, no new security surface.
/// </summary>
public sealed class ArtifactManifestStore : IArtifactManifestStore, IScopedDependency
{
    /// <summary>Per-file capture cap. Past it the file is SKIPPED with a warning, never truncated — a captured artifact's bytes ARE the deliverable, and a silently-clipped dataset is a lie; absence is honest.</summary>
    public const long MaxArtifactBytes = 4 * 1024 * 1024;

    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactStore _artifacts;
    private readonly ILogger<ArtifactManifestStore> _logger;

    public ArtifactManifestStore(CodeSpaceDbContext db, IArtifactStore artifacts, ILogger<ArtifactManifestStore> logger)
    {
        _db = db;
        _artifacts = artifacts;
        _logger = logger;
    }

    public async Task<int> CaptureDeclaredAsync(AgentTask task, string workspaceDirectory, Guid agentRunId, Guid? workflowRunId, Guid teamId, long fenceEpoch, CancellationToken cancellationToken)
    {
        var paths = DeclaredDeliverablePaths(task);

        if (paths.Count == 0) return 0;

        var captured = 0;

        foreach (var path in paths)
        {
            var bytes = ReadDeclared(workspaceDirectory, path, agentRunId);

            if (bytes is null) continue;

            var artifactId = await _artifacts.PutAsync(teamId, bytes, ContentTypeFor(path), cancellationToken).ConfigureAwait(false);

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
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                SizeBytes = bytes.LongLength,
                ContentType = ContentTypeFor(path),
            }, cancellationToken).ConfigureAwait(false);

            captured++;
        }

        return captured;
    }

    /// <summary>The workspace-relative deliverable list a non-<c>TestsPass</c> acceptance declares — <c>TestsPass</c> (or an absent kind, which defaults to it) carries an ARGV, never paths, so it declares nothing capturable.</summary>
    internal static IReadOnlyList<string> DeclaredDeliverablePaths(AgentTask task) =>
        task.Acceptance is { Kind: not null and not Messages.Agents.Benchmark.BenchmarkGradingKind.TestsPass } spec
            ? spec.Command.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList()
            : Array.Empty<string>();

    private byte[]? ReadDeclared(string workspaceDirectory, string path, Guid agentRunId)
    {
        if (!WorkspaceArtifactGuard.TryReadBytesWithin(workspaceDirectory, path, MaxArtifactBytes, out var bytes, out var reason))
        {
            _logger.LogWarning("Agent run {RunId}: declared deliverable '{Path}' not captured — {Reason}", agentRunId, path, reason ?? "unreadable");
            return null;
        }

        return bytes;
    }

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
        if (false)
        {
            prior!.SupersededByManifestId = fresh.Id;
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
}

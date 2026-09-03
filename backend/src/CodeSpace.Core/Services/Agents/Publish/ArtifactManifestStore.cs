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

    /// <summary>
    /// C2 — the DECLARED capture's sibling for everything a repo-less run wrote that its contract never named: a
    /// BOUNDED, deterministic walk of the scratch world that captures text/document files the same way, minting the
    /// same typed rows through the same containment guard. A run only knows what it declared, and the deliverable
    /// worth keeping was routinely the one nobody declared — before this it died with the scratch directory.
    ///
    /// <para>Bounded on purpose (<see cref="ArtifactManifestStore.MaxUndeclaredCaptureFiles"/>,
    /// <see cref="ArtifactManifestStore.MaxUndeclaredCaptureBytes"/>, <see cref="ArtifactManifestStore.MaxUndeclaredScanEntries"/>,
    /// <see cref="ArtifactManifestStore.MaxUndeclaredScanSeconds"/>): an
    /// unbounded walk of a directory an agent had shell access to is a way to fill the artifact store, not a
    /// capability. The limits are VISIBLE rather than silent — the returned outcome reports what the walk refused,
    /// which the caller commits into the capture promise's facts. DECLARED paths are skipped here entirely: they
    /// keep <see cref="CaptureDeclaredAsync"/>'s exact semantics, including its shortfall accounting.</para>
    /// </summary>
    Task<UndeclaredCaptureOutcome> CaptureUndeclaredAsync(AgentTask task, string workspaceDirectory, Guid agentRunId, Guid? workflowRunId, Guid teamId, long fenceEpoch, CancellationToken cancellationToken);

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

    /// <summary>C2 — how many UNDECLARED scratch files one walk may capture. Rule 8: pinned by test, because raising it silently is how an artifact store fills up and lowering it silently is how a deliverable disappears.</summary>
    public const int MaxUndeclaredCaptureFiles = 32;

    /// <summary>C2 — the total byte budget one walk's UNDECLARED captures share. A single file bigger than what is left is refused whole (never clipped — a truncated deliverable is a lie). Rule 8: pinned by test.</summary>
    public const long MaxUndeclaredCaptureBytes = 8L * 1024 * 1024;

    /// <summary>C2 — how many filesystem ENTRIES (directories included) one walk will even LOOK at before it stops scanning. Separate from the capture cap so a pathological tree cannot turn the walk itself into the cost. Rule 8: pinned by test.</summary>
    public const int MaxUndeclaredScanEntries = 20000;

    /// <summary>C2 — the wall-clock ceiling on one walk. A tree can be deep and slow without being large, and a capture step must never become the reason a run's completion hangs. Rule 8: pinned by test.</summary>
    public const int MaxUndeclaredScanSeconds = 10;

    /// <summary>
    /// C2 — the ONLY extensions an undeclared walk will take: text and the document formats <see cref="KindFor"/>
    /// already knows how to type. A report an agent wrote as <c>report.pdf</c> or <c>summary.docx</c> is exactly the
    /// deliverable this walk exists to keep — leaving those out meant the walk captured NOTHING for a real report,
    /// and an empty world grades as "the agent produced nothing". Archives, executables and unknown binaries stay
    /// refused: a walk nobody asked for should not spend a run's byte budget on bytes no grader will open. A DECLARED
    /// path is never filtered by this list (an agent asked for that one by name). Rule 8: pinned by test.
    /// </summary>
    public static readonly IReadOnlyList<string> CapturableUndeclaredExtensions = new[] { ".csv", ".docx", ".html", ".json", ".jsonl", ".md", ".mmd", ".pdf", ".png", ".puml", ".rst", ".svg", ".tsv", ".txt", ".xlsx", ".xml", ".yaml", ".yml" };

    /// <summary>
    /// C2 — directory names the walk never descends. Every one is a build output or a dependency tree: thousands of
    /// files an agent did not author, which would exhaust the scan and byte budgets and crowd out the one report the
    /// walk exists to keep. Rule 8: pinned by test.
    /// </summary>
    public static readonly IReadOnlyList<string> SkippedWalkDirectories = new[] { ".git", "bin", "dist", "node_modules", "obj", "target", "vendor" };

    private static readonly HashSet<string> Capturable = new(CapturableUndeclaredExtensions, StringComparer.Ordinal);
    private static readonly HashSet<string> SkippedDirectories = new(SkippedWalkDirectories, StringComparer.OrdinalIgnoreCase);

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

        var coordinates = new CaptureCoordinates(agentRunId, workflowRunId, teamId, fenceEpoch);
        var captured = 0;

        foreach (var path in paths)
            if (await CaptureOneAsync(coordinates, workspaceDirectory, path, cancellationToken).ConfigureAwait(false)) captured++;

        return captured;
    }

    public async Task<UndeclaredCaptureOutcome> CaptureUndeclaredAsync(AgentTask task, string workspaceDirectory, Guid agentRunId, Guid? workflowRunId, Guid teamId, long fenceEpoch, CancellationToken cancellationToken)
    {
        var declared = DeclaredDeliverablePaths(task).ToHashSet(StringComparer.Ordinal);
        var coordinates = new CaptureCoordinates(agentRunId, workflowRunId, teamId, fenceEpoch);

        var captured = 0;
        var refused = 0;
        var spent = 0L;

        foreach (var candidate in Walk(workspaceDirectory, cancellationToken))
        {
            if (declared.Contains(candidate.Path)) continue;

            if (!IsCapturableUndeclared(candidate.Path) || captured >= MaxUndeclaredCaptureFiles || spent + candidate.LengthBytes > MaxUndeclaredCaptureBytes)
            {
                refused++;
                continue;
            }

            if (!await CaptureOneAsync(coordinates, workspaceDirectory, candidate.Path, cancellationToken).ConfigureAwait(false))
            {
                refused++;
                continue;
            }

            captured++;
            spent += candidate.LengthBytes;
        }

        if (refused > 0)
            _logger.LogWarning("Agent run {RunId}: the scratch walk captured {Captured} undeclared file(s) and left {Refused} — a non-text extension, a dotfile, or the walk's own {FileCap}-file / {ByteCap}-byte ceiling", agentRunId, captured, refused, MaxUndeclaredCaptureFiles, MaxUndeclaredCaptureBytes);

        return new UndeclaredCaptureOutcome { Captured = captured, Refused = refused };
    }

    /// <summary>
    /// Capture ONE workspace-relative path — the single write path both the declared list and the undeclared walk go
    /// through, so containment, the declaring write's ordering, the typed row's shape and the skip notice have one
    /// implementation. False = nothing was captured (the guard refused it); the notice names why.
    /// </summary>
    private async Task<bool> CaptureOneAsync(CaptureCoordinates coordinates, string workspaceDirectory, string path, CancellationToken cancellationToken)
    {
        if (!WorkspaceArtifactGuard.TryResolveFileWithin(workspaceDirectory, path, out var file, out var failure))
        {
            NoticeSkip(new DeclaredDeliverableSkip(coordinates.AgentRunId, path, failure!.Value));
            return false;
        }

        // Declaring write (see IArtifactRetentionWriter): content_artifact_id below is the ONLY reference this
        // method writes, and it is written AFTER the bytes land, so a throw in between leaves bytes nothing ever
        // pointed at. The declaration is what lets the retention reaper reclaim exactly those. A dedup hit declares
        // nothing — the bytes are then shared with a producer whose references are not enumerable — so this call is
        // safe to make unconditionally.
        ArtifactStreamRetentionWrite write;
        using (var source = new WorkspaceArtifactSource(file))
            write = await _retention.PutDeclaredAsync(Declaration(coordinates.TeamId, source, path, coordinates.AgentRunId), cancellationToken).ConfigureAwait(false);
        var artifactId = write.ArtifactId;

        await UpsertAsync(new ArtifactManifest
        {
            Id = Guid.NewGuid(),
            TeamId = coordinates.TeamId,
            AgentRunId = coordinates.AgentRunId,
            WorkflowRunId = coordinates.WorkflowRunId,
            FenceEpoch = coordinates.FenceEpoch,
            Kind = KindFor(path),
            LogicalPath = path,
            ContentArtifactId = artifactId,
            Sha256 = write.Sha256,
            SizeBytes = write.SizeBytes,
            ContentType = ContentTypeFor(path),
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// The scratch world's files as workspace-relative paths, ordered ORDINALLY so the bounded selection is
    /// deterministic — a cap that took a different subset per run would make "what did this attempt keep?"
    /// unanswerable. Symlinked files and directories are skipped at enumeration (the recursion never descends a
    /// reparse point) and <see cref="CaptureOneAsync"/>'s guard independently re-clamps every component, so an
    /// escape has to beat both. Stops scanning at <see cref="MaxUndeclaredScanEntries"/> entries or <see cref="MaxUndeclaredScanSeconds"/> seconds, whichever comes first.
    /// </summary>
    internal static IReadOnlyList<ScratchFile> Walk(string root, CancellationToken cancellationToken = default)
    {
        // An OWN traversal, not Directory.EnumerateFiles(RecurseSubdirectories): that API can only bound what it
        // RETURNS, so a directory-only tree (or one node_modules) is descended in full before it yields a single
        // file — the walk pays for a tree the agent did not author, and the budget is spent on files no grader will
        // open. Here every entry, directory included, costs one against the scan cap, and a skipped directory costs
        // exactly one instead of everything beneath it.
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };

        var deadline = DateTimeOffset.UtcNow.AddSeconds(MaxUndeclaredScanSeconds);
        var files = new List<ScratchFile>();
        var pending = new Queue<string>();
        var entries = 0;

        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested || entries >= MaxUndeclaredScanEntries || DateTimeOffset.UtcNow >= deadline) break;

            var directory = pending.Dequeue();

            foreach (var entry in EnumerateEntries(directory, options))
            {
                if (++entries >= MaxUndeclaredScanEntries) break;

                var name = Path.GetFileName(entry);

                if (Directory.Exists(entry))
                {
                    if (!SkippedDirectories.Contains(name)) pending.Enqueue(entry);
                    continue;
                }

                files.Add(new ScratchFile(Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/'), LengthOf(entry)));
            }
        }

        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>A directory that vanished or turned unreadable mid-walk contributes nothing — the walk is best-effort by contract, and the capture guard is the authority on what still exists.</summary>
    private static IEnumerable<string> EnumerateEntries(string directory, EnumerationOptions options)
    {
        try { return Directory.EnumerateFileSystemEntries(directory, "*", options).ToList(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>A file that vanished between enumeration and measurement reads as zero-length — the capture's own guard is the authority on whether it still exists, and it fails closed there.</summary>
    private static long LengthOf(string full)
    {
        try { return new FileInfo(full).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Whether an UNDECLARED walked path is one this store will take: no dotfile / dot-directory anywhere in it (a
    /// harness's own config and cache live there — <c>.git</c>, <c>.codex</c>, <c>.claude</c>, and a credential file
    /// is exactly the thing a walk must never lift), and a text/document extension off the pinned allowlist.
    /// </summary>
    internal static bool IsCapturableUndeclared(string relativePath)
    {
        foreach (var component in relativePath.Split('/'))
            if (component.StartsWith('.')) return false;

        return Capturable.Contains(Path.GetExtension(relativePath).ToLowerInvariant());
    }

    /// <summary>The declaring write's request for one captured deliverable. The holder it names is the <c>artifact_manifest</c> row the caller writes next.</summary>
    private static ArtifactStreamRetentionWriteRequest Declaration(Guid teamId, WorkspaceArtifactSource source, string path, Guid agentRunId) =>
        new(new ArtifactStreamWriteRequest(teamId, ContentTypeFor(path), source), ArtifactRetentionClass.ArtifactManifestContent, HolderKind, agentRunId);

    /// <summary>The workspace-relative deliverable list a non-<c>TestsPass</c> acceptance declares — <c>TestsPass</c> (or an absent kind, which defaults to it) carries an ARGV, never paths, so it declares nothing capturable. The kind rule itself lives on <see cref="AgentAcceptanceContract.GradesFromDeliverables"/>, the single place every tier of the repo-less lane reads it.</summary>
    internal static IReadOnlyList<string> DeclaredDeliverablePaths(AgentTask task) =>
        AgentAcceptanceContract.GradesFromDeliverables(task.Acceptance)
            ? task.Acceptance!.Command.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList()
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

    /// <summary>The attempt coordinates every captured row is stamped with — this class's own parameter bundle (the same Rule 18.1 exemption <see cref="DeclaredDeliverableSkip"/> documents), keeping <see cref="CaptureOneAsync"/> inside the five-parameter cap.</summary>
    private sealed record CaptureCoordinates(Guid AgentRunId, Guid? WorkflowRunId, Guid TeamId, long FenceEpoch);

    /// <summary>One file the scratch walk saw, and what it would cost the byte budget. Internal (not private) so the walk's ordering and limits are unit-pinned without a database.</summary>
    internal sealed record ScratchFile(string Path, long LengthBytes);

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

using System.Text.Json;
using System.Text;
using Autofac;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Messages.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Runtime;

public sealed class WorkflowSensitivePayloadStore : IWorkflowSensitivePayloadStore, IScopedDependency
{
    internal const string NodeOutputsKind = "node.outputs.v1";
    internal const string HolderKind = "workflow_run_sensitive_record_payload";
    private const string CiphertextContentType = "application/vnd.codespace.encrypted-workflow-payload";

    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowSensitivePayloadProtector _protector;
    private readonly IArtifactStore _artifacts;
    private readonly ILifetimeScope _lifetimeScope;

    public WorkflowSensitivePayloadStore(CodeSpaceDbContext db, IWorkflowSensitivePayloadProtector protector, IArtifactStore artifacts, ILifetimeScope lifetimeScope)
    {
        _db = db;
        _protector = protector;
        _artifacts = artifacts;
        _lifetimeScope = lifetimeScope;
    }

    public async Task SaveNodeOutputsAsync(Guid recordId, Guid runId, Guid teamId, IReadOnlyDictionary<string, JsonElement> outputs, CancellationToken cancellationToken)
    {
        var ciphertext = _protector.Protect(JsonSerializer.Serialize(outputs));
        var bytes = Encoding.UTF8.GetBytes(ciphertext);

        Guid? artifactId = bytes.Length > ArtifactStoreConfig.InlineThresholdBytes
            ? await DeclareCiphertextAsync(recordId, teamId, bytes, cancellationToken).ConfigureAwait(false)
            : null;

        _db.WorkflowRunSensitiveRecordPayload.Add(new WorkflowRunSensitiveRecordPayload
        {
            RecordId = recordId,
            RunId = runId,
            TeamId = teamId,
            PayloadKind = NodeOutputsKind,
            Ciphertext = artifactId is null ? ciphertext : null,
            CiphertextArtifactId = artifactId,
            CiphertextSizeBytes = bytes.LongLength,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Store the encrypted bytes and mint their retention declaration on an ISOLATED lifetime scope — a scope of its
    /// own means a <c>CodeSpaceDbContext</c> of its own, so this write commits on its own connection instead of
    /// enlisting in whatever transaction the caller opened around the sidecar row.
    ///
    /// <para>That isolation is the whole point, and it is ordering, not optimism. The bytes reach the storage provider
    /// before the sidecar INSERT is attempted, and no provider participates in a Postgres rollback — so if the
    /// declaration rode the caller's transaction, a rollback at the sidecar write (the 0168 trigger's bound-record
    /// check, the 0170 cross-team artifact check, a deadlock, a connection fault) would revert the declaration while
    /// the ciphertext object stayed durable. An undeclared artifact is unreapable BY DESIGN, so that outcome is a
    /// permanent secret-bearing object on the operator's storage. Committing the declaration first inverts the failure:
    /// a rollback leaves declared bytes that no oracle site references, which is precisely what
    /// <c>ArtifactRetentionReaper</c> exists to collect. The caller's own commit then adds the reference that keeps
    /// them.</para>
    ///
    /// <para>A dedup hit declares nothing, and is safe to ignore for the same reason it is in
    /// <c>ArtifactManifestStore</c>: those bytes were already durable under another producer's write, so this call did
    /// not create them and cannot enumerate their references.</para>
    /// </summary>
    private async Task<Guid> DeclareCiphertextAsync(Guid recordId, Guid teamId, byte[] bytes, CancellationToken cancellationToken)
    {
        var request = new ArtifactRetentionWriteRequest(teamId, bytes, CiphertextContentType, ArtifactRetentionClass.SensitiveRecordPayload, HolderKind, recordId);

        await using var isolated = _lifetimeScope.BeginLifetimeScope();
        var write = await isolated.Resolve<IArtifactRetentionWriter>().PutDeclaredAsync(request, cancellationToken).ConfigureAwait(false);

        return write.ArtifactId;
    }

    public async Task<IReadOnlyDictionary<string, JsonElement>?> ReadNodeOutputsAsync(Guid recordId, Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var stored = await _db.WorkflowRunSensitiveRecordPayload.AsNoTracking()
            .Where(payload => payload.RecordId == recordId && payload.RunId == runId && payload.TeamId == teamId && payload.PayloadKind == NodeOutputsKind)
            .Select(payload => new { payload.Ciphertext, payload.CiphertextArtifactId, payload.CiphertextSizeBytes })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null) return null;

        string ciphertext;
        if (stored.Ciphertext is not null) ciphertext = stored.Ciphertext;
        else
        {
            var artifact = await _artifacts.GetBytesAsync(teamId, stored.CiphertextArtifactId!.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Encrypted recovery artifact {stored.CiphertextArtifactId} is unavailable for workflow record {recordId}.");
            if (artifact.Bytes.LongLength != stored.CiphertextSizeBytes)
                throw new InvalidOperationException($"Encrypted recovery artifact {stored.CiphertextArtifactId} has an unexpected length for workflow record {recordId}.");
            ciphertext = Encoding.UTF8.GetString(artifact.Bytes);
        }

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(_protector.Unprotect(ciphertext))
            ?? new Dictionary<string, JsonElement>();
    }
}

using System.Text.Json;
using System.Text;
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

    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowSensitivePayloadProtector _protector;
    private readonly IArtifactStore _artifacts;
    private readonly IArtifactRetentionWriter _retention;

    public WorkflowSensitivePayloadStore(CodeSpaceDbContext db, IWorkflowSensitivePayloadProtector protector, IArtifactStore artifacts, IArtifactRetentionWriter retention)
    {
        _db = db;
        _protector = protector;
        _artifacts = artifacts;
        _retention = retention;
    }

    public async Task SaveNodeOutputsAsync(Guid recordId, Guid runId, Guid teamId, IReadOnlyDictionary<string, JsonElement> outputs, CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.Serialize(outputs);
        var ciphertext = _protector.Protect(plaintext);
        var bytes = Encoding.UTF8.GetBytes(ciphertext);
        Guid? artifactId = null;
        if (bytes.Length > ArtifactStoreConfig.InlineThresholdBytes)
        {
            var write = await _retention.PutDeclaredAsync(new ArtifactRetentionWriteRequest(teamId, bytes, "application/vnd.codespace.encrypted-workflow-payload",
                ArtifactRetentionClass.SensitiveRecordPayload, "workflow_run_sensitive_record_payload", recordId), cancellationToken).ConfigureAwait(false);
            artifactId = write.ArtifactId;
        }
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

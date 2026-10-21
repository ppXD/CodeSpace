using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

public sealed class ExternalTriggerDefinitionSync(CodeSpaceDbContext db, IExternalTriggerStore store) : IScopedDependency
{
    public async Task SyncAsync(Guid workflowId, Guid teamId, WorkflowDefinition definition, Guid actorId, CancellationToken cancellationToken)
    {
        var nodes = definition.Nodes.Where(n => n.TypeKey == ExternalTriggerSlug.TypeKey).ToArray();
        var nodeIds = nodes.Select(n => n.Id).ToArray();
        var retired = await db.WorkflowExternalTrigger.Where(t => t.WorkflowId == workflowId && t.TeamId == teamId && t.DeletedDate == null && !nodeIds.Contains(t.NodeId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in retired)
        {
            row.DeletedDate = DateTimeOffset.UtcNow;
            row.KeyCiphertext = string.Empty;
        }

        // Release the partial unique slug before a replacement claims it. The command transaction keeps both atomic.
        if (retired.Count != 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var node in nodes)
        {
            var slug = node.Config.ValueKind == JsonValueKind.Object && node.Config.TryGetProperty("slug", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            await store.EnsureForNodeAsync(workflowId, teamId, node.Id, slug, actorId, cancellationToken).ConfigureAwait(false);
        }
    }
}

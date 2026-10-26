using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

public sealed class ExternalTriggerDefinitionSync(CodeSpaceDbContext db, IExternalTriggerStore store) : IScopedDependency
{
    public static WorkflowDefinition Normalize(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(node => node.TypeKey == ExternalTriggerSlug.TypeKey
            && node.Config.ValueKind == JsonValueKind.Object && node.Config.TryGetProperty("slug", out _)
            ? node with { Config = JsonSerializer.SerializeToElement(node.Config.EnumerateObject()
                .Where(property => property.Name != "slug").ToDictionary(property => property.Name, property => property.Value)) }
            : node).ToArray(),
    };

    /// <summary>
    /// Save-time schema validation runs before any workflow row is written, so an unsupported keyword cannot leave
    /// a half-created workflow behind.
    /// </summary>
    public static void ValidateSchemas(WorkflowDefinition definition)
    {
        var schemaErrors = definition.Nodes
            .Where(n => n.TypeKey == ExternalTriggerSlug.TypeKey)
            .SelectMany(node => node.Config.ValueKind == JsonValueKind.Object
                && node.Config.TryGetProperty("inputSchema", out var schema)
                ? ExternalTriggerPayloadValidator.ValidateSchema(schema)
                : [])
            .ToArray();
        if (schemaErrors.Length != 0)
            throw new WorkflowValidationException(schemaErrors);
    }

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

        foreach (var node in nodes)
            await store.EnsureForNodeAsync(workflowId, teamId, node.Id, actorId, cancellationToken).ConfigureAwait(false);
    }
}

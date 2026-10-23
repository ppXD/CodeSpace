using System.Text.Json;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

/// <summary>
/// Enforces inputSchema as the single input declaration so ingress validation and {{input.*}} cannot drift.
/// Apply before definition validation, serialization and hashing to freeze the same contract the engine resolves.
/// </summary>
public static class ExternalTriggerInputProjection
{
    public static IReadOnlyList<WorkflowVariable> Apply(IReadOnlyList<WorkflowVariable> declared, WorkflowDefinition definition)
    {
        var node = definition.Nodes.FirstOrDefault(n => n.TypeKey == ExternalTriggerSlug.TypeKey);
        if (node is null) return declared;

        if (node.Config.ValueKind != JsonValueKind.Object
            || !node.Config.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return Array.Empty<WorkflowVariable>();

        var required = schema.TryGetProperty("required", out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var inputs = new List<WorkflowVariable>();
        foreach (var property in properties.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out _)) continue;

            inputs.Add(new WorkflowVariable
            {
                Name = property.Name,
                Label = value.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null,
                Description = value.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null,
                Schema = value.Clone(),
                Default = value.TryGetProperty("default", out var fallback) ? fallback.Clone() : null,
                Required = required.Contains(property.Name),
            });
        }

        return inputs;
    }
}

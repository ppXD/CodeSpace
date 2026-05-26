using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>
/// Tiny helper to construct JSON-schema documents from C# code. Plugin authors who want
/// rich schemas can author raw JSON and parse it into a JsonElement — this is for the
/// 80% of nodes with simple "object with N typed properties" shapes.
/// </summary>
public static class SchemaBuilder
{
    public static JsonElement EmptyObject() => Parse("""{"type":"object","properties":{},"additionalProperties":false}""");

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

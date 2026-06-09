using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Shared System.Text.Json settings for the agent layer's persisted contracts (<c>AgentTask</c> →
/// task_jsonb, <c>AgentRunResult</c> → result_jsonb). Web defaults (camelCase, case-insensitive read)
/// + string enums, so persisted JSON is stable, human-readable, and matches what an API would
/// serialize. Kept concern-local (mirrors the workflow layer's own options) so the agent layer owns
/// its serialization rather than depending on another concern for it.
/// </summary>
public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

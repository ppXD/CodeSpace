using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// What a <c>PublishManifest</c> row describes: one subtask's own produced artifact (<see cref="Agent"/>, keyed on
/// its <c>AgentRunId</c>), or the run-level merge of several subtasks' work into one branch (<see cref="Integration"/>,
/// keyed on the owning <c>WorkflowRunId</c> instead — no single agent run owns it).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PublishManifestKind
{
    Agent,
    Integration,
}

using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// Mirrors <c>AgentRunResult.AcceptancePassed</c> onto the manifest row at the moment it is written, so a reader
/// (a dependent subtask's staging check, the room's result card) never has to re-join <c>agent_run</c> to know
/// whether an objective acceptance contract graded this artifact. <see cref="NotApplicable"/> is the common case —
/// most subtasks carry no acceptance contract at all.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PublishAcceptanceState
{
    NotApplicable,
    Passed,
    Failed,
}

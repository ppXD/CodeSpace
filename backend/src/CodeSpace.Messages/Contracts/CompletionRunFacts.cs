using CodeSpace.Messages.Enums;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Contracts;

/// <summary>
/// The run-grain facts the completion reducer reads BESIDE the envelopes (F0) — how the run ended, stripped of any
/// kind knowledge. Composed from durable signals only (the terminal status row, the tape's recorded stop reason);
/// never from a renderer's interpretation. The reducer combines these with the requirement/receipt envelopes into
/// the one <see cref="CompletionAssessment"/>.
/// </summary>
public sealed record CompletionRunFacts
{
    /// <summary>The run's own terminal status — only ever a terminal value (<c>Success</c>/<c>Failure</c>/<c>Cancelled</c>); an in-flight run has no completion facts.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required WorkflowRunStatus TerminalStatus { get; init; }

    /// <summary>The recorded forced-stop reason (<c>SupervisorStopReasons</c> vocabulary) when the run was stopped by a server-side fuse; null for a run that ended of its own accord.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ForcedStopReason { get; init; }

    /// <summary>Whether the run reached an ORDERLY terminal (a recorded terminal decision / a completed engine fold) — false means the engine died out from under it (<see cref="ExecutionDisposition.Crashed"/>).</summary>
    public required bool HadOrderlyTerminal { get; init; }
}

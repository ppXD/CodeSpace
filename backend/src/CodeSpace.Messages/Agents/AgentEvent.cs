using System.Text.Json;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// One normalized step in an agent run — the unit of the live log. Every harness maps its native
/// stream (Codex JSONL, Claude stream-json, Aider/OpenCode stdout) into this SAME shape, so the UI,
/// audit, and HITL surfaces read one vocabulary regardless of which harness produced it.
///
/// This is the harness-level event (kind + human-readable line + optional structured payload). The
/// durable, ordered log (sequence number, timestamp, run id) is the AgentRun event record (B0.3),
/// which stamps those on persist — keeping the harness free of persistence concerns.
/// </summary>
public sealed record AgentEvent
{
    public required AgentEventKind Kind { get; init; }

    /// <summary>Human-readable one-line rendering for the live stream.</summary>
    public required string Text { get; init; }

    /// <summary>Structured payload (tool args, changed path, command, test counts, …) when the native event carried one.</summary>
    public JsonElement? Data { get; init; }
}

/// <summary>
/// The normalized event vocabulary. New kinds are added here (additive) as harnesses surface new
/// step types; an adapter that can't classify a line maps it to <see cref="Warning"/> rather than
/// dropping it, so the log is never silently lossy.
/// </summary>
public enum AgentEventKind
{
    Queued,
    Started,
    AssistantMessage,
    Reasoning,
    PlanUpdate,
    ToolCall,
    CommandExecuted,
    FileChanged,
    TestOutput,
    ApprovalRequested,
    ApprovalResolved,
    Warning,
    Error,
    FinalSummary,
    Completed,
}

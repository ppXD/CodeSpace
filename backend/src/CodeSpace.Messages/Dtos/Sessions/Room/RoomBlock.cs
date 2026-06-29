using System.Text.Json.Serialization;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Sessions.Room;

/// <summary>
/// One render-ready unit of the session transcript, discriminated by <c>type</c>. The frontend switches on
/// <c>type</c> and renders; an unknown type degrades to a generic fallback, so a NEW block kind ships as another
/// <see cref="JsonDerivedTypeAttribute"/> with ZERO frontend change. Every block carries a stable <see cref="Id"/>
/// (the frontend keys + diffs on it) and a monotonic <see cref="Seq"/> (for streaming deltas).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UserMessageBlock), "user_message")]
[JsonDerivedType(typeof(AssistantTurnBlock), "assistant_turn")]
[JsonDerivedType(typeof(ExecutionMapBlock), "execution_map")]
[JsonDerivedType(typeof(NarrativeStepBlock), "narrative_step")]
[JsonDerivedType(typeof(AgentGroupBlock), "agent_group")]
[JsonDerivedType(typeof(DecisionBlock), "decision")]
[JsonDerivedType(typeof(DiagnosticBlock), "diagnostic")]
public abstract record RoomBlock
{
    /// <summary>Stable id (e.g. <c>turn-1</c>, <c>turn-1:map</c>, <c>turn-1:step-3</c>, <c>decision-{guid}</c>) — the frontend keys + diffs on it.</summary>
    public required string Id { get; init; }

    /// <summary>Monotonic ordinal (the run's append-only change watermark at projection time) — lets a delta add / update this block by id.</summary>
    public required long Seq { get; init; }
}

/// <summary>The user's message that opened a turn — the run's launch goal, shown as a chat bubble.</summary>
public sealed record UserMessageBlock : RoomBlock
{
    public required string Text { get; init; }
    public DateTimeOffset? At { get; init; }
}

/// <summary>
/// One assistant turn = the AI's reply for a run: a backend-authored <see cref="Summary"/> headline, the
/// <see cref="Map"/> (execution stepper on top), the streamed inner <see cref="Blocks"/> (narrative / agents /
/// decisions / diagnostics, in render order), and the capability-aware <see cref="Actions"/>. Rerun / replay are
/// ATTEMPTS of this turn (<see cref="RunId"/> = the latest attempt shown; <see cref="TurnRunId"/> = the turn's identity),
/// never new turns.
/// </summary>
public sealed record AssistantTurnBlock : RoomBlock
{
    public required int TurnIndex { get; init; }
    public required Guid TurnRunId { get; init; }
    public required Guid RunId { get; init; }
    public required WorkflowRunStatus Status { get; init; }

    /// <summary>A backend-authored natural-language headline for the turn (the AI's reply summary). Null until there's enough to say.</summary>
    public string? Summary { get; init; }

    public ExecutionMapBlock? Map { get; init; }

    public required IReadOnlyList<RoomBlock> Blocks { get; init; }
    public required IReadOnlyList<RoomAction> Actions { get; init; }

    public DateTimeOffset? At { get; init; }
}

/// <summary>The tidy node-graph stepper on top of a turn — backend-ordered phases as labeled steps, each with a status.</summary>
public sealed record ExecutionMapBlock : RoomBlock
{
    public required IReadOnlyList<ExecutionMapStep> Steps { get; init; }
}

public sealed record ExecutionMapStep
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required ExecutionStepStatus Status { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionStepStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Blocked,
}

/// <summary>
/// One line of the AI narrating its work — backend-authored natural language (what it's doing, the public rationale,
/// a status). NEVER chain-of-thought: only the intentional, user-facing "I'll do X / I did Y" voice.
/// </summary>
public sealed record NarrativeStepBlock : RoomBlock
{
    public required string Text { get; init; }
    public NarrativeTone Tone { get; init; } = NarrativeTone.Info;
    public DateTimeOffset? At { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NarrativeTone
{
    Info,
    Success,
    Error,
}

/// <summary>A group of agents the AI dispatched in one step — rendered as live terminal cards.</summary>
public sealed record AgentGroupBlock : RoomBlock
{
    public required string Title { get; init; }
    public required IReadOnlyList<RoomAgentCard> Agents { get; init; }
}

public sealed record RoomAgentCard
{
    public required Guid AgentRunId { get; init; }
    public required string Label { get; init; }
    public string? Role { get; init; }

    /// <summary>The agent's lifecycle status as a stable string (the <c>AgentRunStatus</c> name).</summary>
    public required string Status { get; init; }

    public string? Model { get; init; }
    public int? Tokens { get; init; }
    public decimal? CostUsd { get; init; }
    public int? FilesChanged { get; init; }

    /// <summary>The agent's own one-line RESULT takeaway (what it concluded) — shown on the collapsed card before any raw log. Null before the result lands / when it produced none.</summary>
    public string? Summary { get; init; }

    /// <summary>The agent's latest PUBLIC activity line (e.g. "running tests · 12 passing", "editing auth.ts") — never reasoning. Null when idle / unknown.</summary>
    public string? LatestLine { get; init; }
}

/// <summary>A decision the AI needs answered — rendered as an inline answerable card (the wait it parked on).</summary>
public sealed record DecisionBlock : RoomBlock
{
    public required Guid DecisionId { get; init; }
    public required string Question { get; init; }

    /// <summary>The answer shape verbatim (<c>confirm</c> / <c>choose_one</c> / <c>choose_many</c> / <c>free_text</c> / <c>approve_action</c>).</summary>
    public required string Shape { get; init; }

    public IReadOnlyList<RoomDecisionOption>? Options { get; init; }
    public string? Risk { get; init; }
    public DateTimeOffset? Deadline { get; init; }
}

public sealed record RoomDecisionOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }

    /// <summary>True when choosing this option has a real-world side effect (e.g. "merge now") — the renderer can warn before submit.</summary>
    public bool SideEffecting { get; init; }
}

/// <summary>
/// A natural-language status / error line — REPLACES raw engine text ("Node 'sup' failed") with a readable explanation
/// of what actually went wrong, so the user never sees canvas / engine jargon.
/// </summary>
public sealed record DiagnosticBlock : RoomBlock
{
    public NarrativeTone Tone { get; init; } = NarrativeTone.Error;
    public required string Text { get; init; }
}

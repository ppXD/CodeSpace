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
[JsonDerivedType(typeof(StatBlock), "stat")]
[JsonDerivedType(typeof(PlanChecklistBlock), "plan_checklist")]
[JsonDerivedType(typeof(DeliveryBlock), "delivery")]
[JsonDerivedType(typeof(DecisionBlock), "decision")]
[JsonDerivedType(typeof(DiagnosticBlock), "diagnostic")]
[JsonDerivedType(typeof(FinalAnswerBlock), "final_answer")]
[JsonDerivedType(typeof(LiveActivityBlock), "live_activity")]
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

    /// <summary>The turn's wall-clock so far — final once terminal, else live elapsed at projection time. Null before it starts.</summary>
    public long? DurationMs { get; init; }

    /// <summary>The turn's rerun / replay ATTEMPTS, oldest → newest — the header's "N attempts" timeline, each openable to view that attempt's run. EMPTY for a never-rerun turn (a lone attempt needs no history).</summary>
    public IReadOnlyList<RoomTurnAttempt> Attempts { get; init; } = Array.Empty<RoomTurnAttempt>();
}

/// <summary>One attempt of a turn (the original + each rerun / replay fork) — a row in the turn's attempt timeline. <see cref="RunId"/> is the run to open to view that attempt; <see cref="IsCurrent"/> marks the one the turn currently shows (the latest).</summary>
public sealed record RoomTurnAttempt
{
    public required Guid RunId { get; init; }

    /// <summary>1-based ordinal within the turn (1 = the original run).</summary>
    public required int AttemptNumber { get; init; }

    public required WorkflowRunStatus Status { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>True for the attempt the turn currently shows (the newest) — rendered as "shown", not an open link.</summary>
    public required bool IsCurrent { get; init; }
}

/// <summary>The tidy node-graph stepper on top of a turn — backend-ordered lifecycle stages as labeled steps, each with a status + an optional one-word detail.</summary>
public sealed record ExecutionMapBlock : RoomBlock
{
    public required IReadOnlyList<ExecutionMapStep> Steps { get; init; }
}

public sealed record ExecutionMapStep
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required ExecutionStepStatus Status { get; init; }

    /// <summary>A short per-step detail under the label — e.g. "8s", "3 agents", "passed", "PR #128", "auth error", "2 of 3". Null when there's nothing to add.</summary>
    public string? Detail { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionStepStatus
{
    Pending,
    Queued,
    Running,
    Done,
    Failed,
    Blocked,
    Skipped,
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

    /// <summary>The TITLE of the planned subtask this agent was assigned (the model's decomposition) — display only. Null for a non-supervisor / homogeneous spawn.</summary>
    public string? AssignedSubtask { get; init; }

    public string? Model { get; init; }
    public int? Tokens { get; init; }
    public decimal? CostUsd { get; init; }
    public int? FilesChanged { get; init; }

    /// <summary>This agent's OWN changed-file paths (bounded) — per-agent file attribution so a reader sees WHICH agent produced a file (open the agent to preview its exact version), not just the turn-level union. Empty for an agent that changed nothing.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>Side-effecting tool calls the agent made — the card meta "3 files · 6 tool calls · 41s". Null when the agent row is absent (0 is a real "made none").</summary>
    public int? ToolCount { get; init; }

    /// <summary>The agent's run wall-clock in milliseconds — final once terminal, else live elapsed at projection time. Null before it starts.</summary>
    public long? DurationMs { get; init; }

    /// <summary>The agent's own one-line RESULT takeaway (what it concluded) — shown on the collapsed card before any raw log. Null before the result lands / when it produced none.</summary>
    public string? Summary { get; init; }

    /// <summary>The agent's latest PUBLIC activity line (e.g. "running tests · 12 passing", "editing auth.ts") — never reasoning. Null when idle / unknown.</summary>
    public string? LatestLine { get; init; }

    /// <summary>The workflow node + iteration this agent ran as — the cell key. Lets the opened terminal fetch this cell's ATTEMPT history (rerun-from-here) and switch between attempts, exactly like the Activity view. Null for a supervisor-spawned agent (no workflow node), where there is no cell to switch.</summary>
    public string? NodeId { get; init; }
    public string? IterationKey { get; init; }
}

/// <summary>
/// A collapsible STAT row — a labeled count with an optional inline detail + an expandable item list. ONE generic block
/// renders every stat the design shows ("Planned 3 subtasks", "Changed 6 files · +148 −32", "14 tool calls · read·edit·test",
/// "Reasoning"): the projector fills <see cref="Kind"/> (icon), <see cref="Label"/>, <see cref="Detail"/>, and
/// <see cref="Items"/> — the frontend never switches on kind for copy. Empty <see cref="Items"/> → the row isn't expandable.
/// </summary>
public sealed record StatBlock : RoomBlock
{
    /// <summary>Open kind string for the icon + grouping (e.g. <c>subtasks</c> / <c>files</c> / <c>tools</c> / <c>reasoning</c>) — never switched on for copy.</summary>
    public required string Kind { get; init; }
    public required string Label { get; init; }

    /// <summary>An inline summary detail shown on the row (e.g. "+148 −32", "read · edit · test"). Null when none.</summary>
    public string? Detail { get; init; }

    /// <summary>The expandable items (subtask titles, file paths, tool calls, reasoning lines). Empty → the row isn't expandable.</summary>
    public IReadOnlyList<StatItem> Items { get; init; } = Array.Empty<StatItem>();
}

public sealed record StatItem
{
    public required string Text { get; init; }

    /// <summary>Optional trailing detail per item (e.g. a file's "+12 −3", a tool call's kind). Null when none.</summary>
    public string? Detail { get; init; }

    /// <summary>Optional tone for the item (e.g. a failed subtask reads danger). Null = neutral.</summary>
    public NarrativeTone? Tone { get; init; }
}

/// <summary>
/// The run's durable plan as a LIVE CHECKLIST — the whole current plan version with per-item execution state
/// derived from the tape (triad S2b). Emitted once per turn (first inner block) when the run persisted a
/// <c>work_plan</c>; replaces the per-round "Plan · N subtasks" stat rows (the checklist subsumes them). The
/// backend owns every string (states, detail copy, dependency ordinals, acceptance labels); the frontend only
/// renders. Questions/assumptions are READ-ONLY here — the interactive confirm card is the plan-gate slice.
/// </summary>
public sealed record PlanChecklistBlock : RoomBlock
{
    public required string Label { get; init; }

    /// <summary>This plan's version within the run (1-based; re-plans / edit-loop re-entries bump it).</summary>
    public required int Version { get; init; }

    /// <summary>The plan's confirmation lifecycle value (<c>WorkPlanStatuses</c>) — an open string.</summary>
    public required string Status { get; init; }

    /// <summary>The header summary — always at least the item count, plus every non-zero non-pending state (e.g. "5 items · 2 done · 1 running").</summary>
    public string? Detail { get; init; }

    public required IReadOnlyList<PlanChecklistItem> Items { get; init; }

    /// <summary>Producer-recorded defaults chosen where the goal was ambiguous. Empty → the row isn't shown.</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();

    /// <summary>Planner-authored operator questions (choose-a-direction) — read-only display in this block. Empty → none.</summary>
    public IReadOnlyList<RoomPlanQuestion> Questions { get; init; } = Array.Empty<RoomPlanQuestion>();

    /// <summary>True when earlier plan versions exist (a re-plan superseded them) — the "v1 superseded" affordance.</summary>
    public bool HasPriorVersions { get; init; }
}

/// <summary>One checkable line of the plan checklist — the item's contract plus its derived execution state.</summary>
public sealed record PlanChecklistItem
{
    /// <summary>1-based position in the plan — the number the reader sees and dependency ordinals reference.</summary>
    public required int Ordinal { get; init; }

    /// <summary>The plan-local item id (stable across versions of the same item).</summary>
    public required string ItemId { get; init; }

    public required string Title { get; init; }

    /// <summary>Open item kind chip (e.g. "research" / "code"). Null → no chip.</summary>
    public string? Kind { get; init; }

    /// <summary>The derived execution state — a <c>WorkPlanItemStates</c> value (open vocabulary; unknown renders neutral).</summary>
    public required string State { get; init; }

    /// <summary>The 1-based ordinals of the items this one depends on — rendered as "after #1, #3". Empty → independent.</summary>
    public IReadOnlyList<int> DependsOn { get; init; } = Array.Empty<int>();

    /// <summary>The objective acceptance chip text (the check argv, or the deliverable paths). Null → no objective contract.</summary>
    public string? AcceptanceLabel { get; init; }

    /// <summary>The oracle kind behind the chip ("TestsPass" / "ArtifactPresent") — picks the chip icon. Null when no contract.</summary>
    public string? AcceptanceKind { get; init; }

    /// <summary>The latest attempt's objective verdict: true = passed (green chip), false = rejected (red chip), null = ungraded (neutral chip).</summary>
    public bool? AcceptancePassed { get; init; }

    /// <summary>The grader's one-line verdict detail (shown on hover / expand). Null when ungraded.</summary>
    public string? AcceptanceDetail { get; init; }

    /// <summary>Subjective per-item criteria chips (reviewed, never executed). Empty → none.</summary>
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = Array.Empty<string>();

    /// <summary>The LATEST attempt's agent run — the "Details" deep-link into its terminal. Null before any attempt.</summary>
    public Guid? AgentRunId { get; init; }

    /// <summary>How many execution attempts the item has had — the "×2 attempts" badge when &gt; 1.</summary>
    public int Attempts { get; init; }
}

/// <summary>A planner-authored operator question, rendered read-only in the checklist (the interactive form is the plan-confirmation gate).</summary>
public sealed record RoomPlanQuestion
{
    public required string Id { get; init; }
    public required string Question { get; init; }
    public IReadOnlyList<RoomPlanQuestionOption> Options { get; init; } = Array.Empty<RoomPlanQuestionOption>();
    public bool AllowFreeText { get; init; }
}

public sealed record RoomPlanQuestionOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }

    /// <summary>True on the planner's recommended default — the option an unattended run proceeds with.</summary>
    public bool Recommended { get; init; }
}

/// <summary>
/// A delivered change set — the PR / branch the turn produced. Generic over the provider; the projector fills it from the
/// run's open-change-set result. Rendered as the "View PR" card.
/// </summary>
public sealed record DeliveryBlock : RoomBlock
{
    public required string Title { get; init; }

    /// <summary>The change-set reference chip (e.g. "#128"). Null when none.</summary>
    public string? Reference { get; init; }

    public string? BranchHead { get; init; }
    public string? BranchBase { get; init; }

    /// <summary>A short checks status label (e.g. "checks passed"). Null when unknown.</summary>
    public string? Checks { get; init; }
    public bool? ChecksOk { get; init; }

    /// <summary>The external URL to open the change set. Null when none.</summary>
    public string? Url { get; init; }
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

    /// <summary>A short headline for the failure (e.g. "Authentication failed"). Null → render <see cref="Text"/> alone.</summary>
    public string? Title { get; init; }

    public required string Text { get; init; }

    /// <summary>Capability-aware remediation actions (e.g. fix-credentials / rerun / retry-from-step). Empty → none.</summary>
    public IReadOnlyList<RoomAction> Actions { get; init; } = Array.Empty<RoomAction>();

    /// <summary>The raw engine error, hidden behind "Show raw error". Null when there's nothing rawer than <see cref="Text"/>.</summary>
    public string? RawDetail { get; init; }
}

/// <summary>
/// The turn's RICH final result — the closing text plus typed attachments (file links, PR links, and — when a run ever
/// produces them — images), each rendered distinctly. Emitted LAST on a terminal turn that produced a result.
/// </summary>
public sealed record FinalAnswerBlock : RoomBlock
{
    /// <summary>The closing answer text (the supervisor's stop summary). Null when the result is attachment-only.</summary>
    public string? Text { get; init; }

    /// <summary>Typed result attachments — file links, the PR, images. Empty when the turn produced only text.</summary>
    public IReadOnlyList<AnswerAttachment> Attachments { get; init; } = Array.Empty<AnswerAttachment>();
}

/// <summary>One typed attachment of a <see cref="FinalAnswerBlock"/> — the frontend renders each <see cref="Kind"/> distinctly.</summary>
public sealed record AnswerAttachment
{
    public required AnswerAttachmentKind Kind { get; init; }
    public required string Label { get; init; }

    /// <summary>Open link (the PR url, or a file's view url). Null when only a preview/download exists.</summary>
    public string? Url { get; init; }

    /// <summary>Inline-preview url (an image src, or a file preview endpoint). Null when not previewable.</summary>
    public string? PreviewUrl { get; init; }

    /// <summary>Download url. Null when not downloadable.</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>For a file: the run id of the agent that PRODUCED it — so the preview opens THAT agent's exact version (per-agent attribution), disambiguating a file that a round-1 agent produced from the final answer. Null when unattributed / not a file.</summary>
    public Guid? AgentRunId { get; init; }

    /// <summary>For a file: a short label of the producing agent (its role / subtask) — the "· from &lt;agent&gt;" provenance cue so a reader never mistakes an intermediate agent's file for the final deliverable. Null when unattributed.</summary>
    public string? Producer { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnswerAttachmentKind
{
    /// <summary>An image rendered inline. DEFINED but currently UNPOPULATED — no run output exposes user-facing images yet (a later artifact-capture source lights this up).</summary>
    Image,

    /// <summary>A changed / produced file — a path with optional preview + download.</summary>
    FileLink,

    /// <summary>The delivered pull request — an open link.</summary>
    Pr,
}

/// <summary>
/// A live "working…" line pinned at the bottom of an ACTIVE turn — the running agents' latest PUBLIC activity + a folded
/// thinking tail (never raw chain-of-thought). Emitted only while the turn is active; its absence means the turn settled.
/// </summary>
public sealed record LiveActivityBlock : RoomBlock
{
    public required string Text { get; init; }

    /// <summary>The agent whose activity this line reflects — lets the frontend deep-link its terminal. Null when turn-level.</summary>
    public Guid? AgentRunId { get; init; }
}

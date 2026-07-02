using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Plans;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// The per-turn facts the projector gathers from the substrate (the decision tape, the agents' results, the PR node)
/// and hands to the PURE <see cref="RoomNarrative"/>. Keeping the I/O here and the rendering pure means the narrative
/// engine stays unit-testable and the gathering stays one bounded, focused-turn read set (no N+1). All members default
/// empty, so a turn with nothing to say projects exactly as before — <see cref="Empty"/> is the inert baseline.
/// </summary>
public sealed record RoomTurnFacts
{
    /// <summary>The supervisor ROUNDS in tape order — the projector segments the decision tape on each Plan; the narrative renders one Plan / Agents / Operation group PER round (never lumped). Empty for a non-supervisor turn.</summary>
    public IReadOnlyList<RoomRound> Rounds { get; init; } = Array.Empty<RoomRound>();

    /// <summary>The run's durable plan as a live checklist (contract + derived per-item state) — the narrative's plan tracker; when present it REPLACES the per-round plan stat rows. Null when the run persisted no plan (pre-plan runs project exactly as before).</summary>
    public WorkPlanChecklist? Checklist { get; init; }

    /// <summary>The turn's rich final answer (closing text + typed attachments) — emitted last on a terminal turn. Null while in-progress / when there's nothing to deliver.</summary>
    public RoomFinalAnswer? FinalAnswer { get; init; }

    /// <summary>Each running agent's latest PUBLIC activity line, keyed by run id — feeds the live "working…" indicator (never raw CoT). Empty when nothing is running.</summary>
    public IReadOnlyDictionary<Guid, string> LatestLines { get; init; } = new Dictionary<Guid, string>();

    /// <summary>The plan's subtask titles (bounded ≤ 20 by schema) — the flat fallback lead's floor; the render source is <see cref="Rounds"/>.</summary>
    public IReadOnlyList<string> Subtasks { get; init; } = Array.Empty<string>();

    /// <summary>The distinct changed-file paths across the turn's agents — the "Changed N files" row.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>+added / −removed line totals across the turn, when captured (a true data-gap today → null → the row omits "+X −Y").</summary>
    public int? Additions { get; init; }
    public int? Deletions { get; init; }

    /// <summary>Total side-effecting tool calls across the turn's agents — the "N tool calls" row. Null when unknown.</summary>
    public int? ToolCalls { get; init; }

    /// <summary>The per-kind tool-call histogram across the turn's agents (e.g. read · edit · test) — the Tools row's detail + items. Empty when unknown.</summary>
    public IReadOnlyList<ToolKindCount> ToolHistogram { get; init; } = Array.Empty<ToolKindCount>();

    /// <summary>Each spawned agent's one-line result summary, keyed by its run id — the agent cards' takeaway and the lead fallback when there's no stop summary. Empty when none produced one.</summary>
    public IReadOnlyDictionary<Guid, string> AgentSummaries { get; init; } = new Dictionary<Guid, string>();

    /// <summary>Each agent's OWN changed-file paths, keyed by its run id (bounded per agent) — the per-agent file attribution the card renders, so a reader sees WHICH agent produced a file rather than the provenance-blind turn-level union. Empty for an agent that changed nothing.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> AgentFiles { get; init; } = new Dictionary<Guid, IReadOnlyList<string>>();

    /// <summary>How many reasoning entries the turn produced — the "Reasoning" row's count.</summary>
    public int ReasoningCount { get; init; }

    /// <summary>The turn's reasoning step texts (bounded), surfaced when the Reasoning row is expanded. Empty when none produced.</summary>
    public IReadOnlyList<string> ReasoningSteps { get; init; } = Array.Empty<string>();

    /// <summary>The objective acceptance verdict (the Review stage): true = passed, false = failed, null = not graded.</summary>
    public bool? AcceptancePassed { get; init; }

    /// <summary>The delivered change set (the PR card), when the turn opened one.</summary>
    public RoomDelivery? Delivery { get; init; }

    /// <summary>The raw engine error, surfaced behind "Show raw error" on a failure diagnostic. Null when there's nothing rawer than the humanized text.</summary>
    public string? RawError { get; init; }

    /// <summary>The supervisor's RETRY beats in tape order — one per retry decision ("Supervisor retried a subtask"). The narrative renders each as a step so the room shows the recovery flow, not just the surviving agents. Empty when the turn had no retries.</summary>
    public IReadOnlyList<RoomRetryStep> RetrySteps { get; init; } = Array.Empty<RoomRetryStep>();

    /// <summary>The supervisor's RE-SPAWN waves in tape order — each additional Spawn decision that re-dispatched an already-spawned subtask (a second/third wave). The authored phase group anchors only each subtask's FIRST attempt, so a later wave (and its failed agent) is otherwise dropped; the narrative renders each as its own chronological wave, mirroring <see cref="RetrySteps"/>. Empty when every subtask was spawned once.</summary>
    public IReadOnlyList<RoomRespawnStep> RespawnSteps { get; init; } = Array.Empty<RoomRespawnStep>();

    public static readonly RoomTurnFacts Empty = new();
}

/// <summary>One supervisor RETRY beat — the tape sequence it landed at, its user-facing line, the fresh agent it staged, and the model's STRUCTURED rationale (why it retried + the evidence it acted on) when authored. Rendered as a narrative step (with the rationale as its detail) + that agent's own card, so a reader sees the decision, not just that it retried. <see cref="AgentRunId"/> is null for a no-op retry; <see cref="Rationale"/> is null when the model gave none.</summary>
public sealed record RoomRetryStep(long Sequence, string Text, Guid? AgentRunId, string? Rationale = null);

/// <summary>One supervisor RE-SPAWN wave — the tape sequence the additional Spawn decision landed at and the agent-run ids it staged for already-spawned subtasks (wave ≥ 2). Rendered as a narrative step ("Supervisor spawned N agents again") + that wave's agent cards, so the room shows EVERY spawn wave the run really ran, not just the first. Empty <see cref="AgentRunIds"/> can't occur (a wave with no re-spawned agent isn't emitted).</summary>
public sealed record RoomRespawnStep(long Sequence, IReadOnlyList<Guid> AgentRunIds);

/// <summary>One bucket of the tool-call histogram — a tool kind and how many times the turn's agents called it.</summary>
public sealed record ToolKindCount(string Kind, int Count);

/// <summary>
/// One supervisor ROUND — a Plan decision and every decision up to the next Plan, in tape order. The projector segments
/// the raw decision tape on <c>Kind == Plan</c> (a re-plan opens a new round); the narrative renders one Plan-stat + this
/// round's agent group + the closing operation per round.
/// </summary>
public sealed record RoomRound
{
    /// <summary>1-based round number.</summary>
    public required int Index { get; init; }

    /// <summary>THIS round's plan subtask titles (not the whole run's) — the "Plan · N subtasks" row.</summary>
    public IReadOnlyList<string> Subtasks { get; init; } = Array.Empty<string>();

    /// <summary>This round's spawned / retried / resolved agent run ids, in tape (staging) order.</summary>
    public IReadOnlyList<Guid> AgentRunIds { get; init; } = Array.Empty<Guid>();

    /// <summary>The round's closing supervisor operation (merge / resolve / ask-human / stop), translated to friendly copy. Null while the round is still spawning.</summary>
    public RoomOperation? Operation { get; init; }
}

/// <summary>A supervisor operation translated to a user-facing one-liner — the "Merging results" / "Deciding: X" narration between rounds.</summary>
public sealed record RoomOperation(string Kind, string Text, NarrativeTone Tone);

/// <summary>The turn's rich final result — the closing text plus typed attachments (files / PR / images).</summary>
public sealed record RoomFinalAnswer
{
    public string? Text { get; init; }
    public IReadOnlyList<RoomAttachment> Attachments { get; init; } = Array.Empty<RoomAttachment>();
}

/// <summary>One typed final-answer attachment (a file, the PR, or an image).</summary>
public sealed record RoomAttachment(AnswerAttachmentKind Kind, string Label, string? Url, string? PreviewUrl, string? DownloadUrl);

/// <summary>The PR / change set a turn produced — joined from the run's open-PR node. Provider-agnostic.</summary>
public sealed record RoomDelivery
{
    public required string Title { get; init; }
    public string? Reference { get; init; }
    public string? BranchHead { get; init; }
    public string? BranchBase { get; init; }
    public string? Checks { get; init; }
    public bool? ChecksOk { get; init; }
    public string? Url { get; init; }
}

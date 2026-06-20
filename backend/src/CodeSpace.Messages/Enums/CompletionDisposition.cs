namespace CodeSpace.Messages.Enums;

/// <summary>
/// Why a terminal agent run landed where it did — the "what happens next" overlay on top of <see cref="AgentRunStatus"/>
/// (Slice A completion contract). <see cref="Completed"/> is the normal terminal: a clean <see cref="AgentRunStatus.Succeeded"/>,
/// OR a <see cref="AgentRunStatus.Failed"/>/<see cref="AgentRunStatus.Cancelled"/>/<see cref="AgentRunStatus.TimedOut"/> whose
/// status already IS the final word. The other three accompany <see cref="AgentRunStatus.NeedsReview"/> and name WHY a human is
/// needed: a decision the run raised is still unanswered (<see cref="NeedsDecision"/>), an ambiguous ending that reads as a
/// question (<see cref="NeedsReview"/>), or a hard block it couldn't get past (<see cref="Blocked"/>). Lives in Messages so the
/// result DTO + the persistence entity both reference it without a backwards layer dependency.
/// </summary>
public enum CompletionDisposition
{
    /// <summary>The run reached terminal normally — a clean success or a failure whose status is the final word. No human overlay.</summary>
    Completed,

    /// <summary>The run ended with a decision it raised still unanswered — re-graded to <see cref="AgentRunStatus.NeedsReview"/> (Slice A1 GATE). The decision id rides on <c>AgentRunResult.PendingDecisionId</c>.</summary>
    NeedsDecision,

    /// <summary>The run's final output reads as an unresolved question / un-raised options — re-graded to <see cref="AgentRunStatus.NeedsReview"/> (Slice A2 safety net).</summary>
    NeedsReview,

    /// <summary>The run hit a hard block it couldn't get past (a CLI prompt it can't answer, a wall it can't cross) — re-graded to <see cref="AgentRunStatus.NeedsReview"/> (Slice C).</summary>
    Blocked,
}

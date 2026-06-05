namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Outcome of resolving an interactive card's Action wait. Lets the caller
/// (<see cref="CodeSpace.Core.Services.Chat.MessageInteractionService"/>) tell a decision it should
/// RECORD from one it must REJECT to avoid the card diverging from the workflow's actual decision —
/// the distinction a plain bool couldn't carry.
/// </summary>
public enum ActionResumeResult
{
    /// <summary>This click resolved a pending wait and re-dispatched the run — the decision drove the workflow.</summary>
    Resumed,

    /// <summary>
    /// No wait exists for this token in the team — a post-and-continue card, or a run that already ended /
    /// was abandoned. There is no workflow decision to diverge from, so the caller still records the click
    /// (the card is a living thread decoupled from any run).
    /// </summary>
    NoWait,

    /// <summary>
    /// A wait existed but was already resolved — a deadline timed out, or another responder won the race.
    /// The workflow's decision is already set; the caller MUST NOT overwrite the card with this (late)
    /// click, or the displayed resolution would contradict what the workflow actually did.
    /// </summary>
    AlreadyResolved,
}

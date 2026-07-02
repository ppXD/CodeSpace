using CodeSpace.Messages.Plans;

namespace CodeSpace.Core.Services.Plans;

/// <summary>
/// Answers a run's pending plan-confirmation card (triad S3) — the STRUCTURED front door the plan-checklist
/// UI posts to, riding the SAME durable Action-wait resume path as the conversation card's Answer button
/// (one wait, first answer wins, whichever surface it arrives from). Locating the card is tape-derived
/// (the newest ask_human decision must be an unanswered confirmation card), so the endpoint can never
/// resume an unrelated wait.
/// </summary>
public interface IWorkPlanConfirmationService
{
    /// <summary>
    /// Answer the run's pending confirmation card: approve releases execution; a non-approve answer carries
    /// the operator's revision <paramref name="feedback"/> (required in that case) for the supervisor to fold
    /// into a revised plan version. Null when the run has no pending confirmation card (absent / foreign run,
    /// not parked, a content question, or the card was already answered). Team-scoped.
    /// </summary>
    Task<WorkPlanConfirmationOutcome?> AnswerAsync(Guid workflowRunId, Guid teamId, Guid actorUserId, bool approve, string? feedback, CancellationToken cancellationToken);
}

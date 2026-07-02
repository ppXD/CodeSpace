namespace CodeSpace.Messages.Plans;

/// <summary>
/// The result of answering a run's pending plan-confirmation card (triad S3). <c>Resumed</c> is true when
/// this answer resolved the park and re-dispatched the run; false when the wait was already resolved by a
/// concurrent answer (the card is first-answer-wins — the caller's click arrived second and changed nothing).
/// <c>Approved</c> echoes the caller's verdict so the UI can render the immediate state without a refetch.
/// </summary>
public sealed record WorkPlanConfirmationOutcome
{
    public required bool Resumed { get; init; }

    public required bool Approved { get; init; }
}

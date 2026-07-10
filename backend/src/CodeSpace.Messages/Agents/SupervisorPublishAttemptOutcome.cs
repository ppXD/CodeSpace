namespace CodeSpace.Messages.Agents;

/// <summary>
/// The COMPACT, decider-visible result of a supervisor <c>publish</c> decision's attempt to open a pull request
/// against the run's genuinely published branch(es) (DC-2). A pure data noun (Rule 18.1): a read of the
/// <c>publish</c> block a publish decision records, built by <c>SupervisorOutcome.ReadPublishAttempt</c>.
///
/// <para><see cref="AnySucceeded"/> is the bar <c>SupervisorPublishGate</c> re-checks on the NEXT stop attempt: at
/// least one opened pull request lets the run finish (a multi-repo run's partial failure is still a real delivery,
/// mirroring <c>ChangeSetService</c>'s own per-repo failure isolation); zero opened out of at least one target
/// substitutes an <c>ask_human</c> naming <see cref="Reasons"/> rather than retrying forever.</para>
/// </summary>
public sealed record SupervisorPublishAttemptOutcome
{
    /// <summary>Whether AT LEAST ONE targeted repository's pull request opened successfully.</summary>
    public required bool AnySucceeded { get; init; }

    /// <summary>Each failed repository's one-line reason (empty when every target opened, or there were no targets at all).</summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

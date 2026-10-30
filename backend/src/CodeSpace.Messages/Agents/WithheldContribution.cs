namespace CodeSpace.Messages.Agents;

/// <summary>
/// One unit of a run's produced work the integration step left OUT of the reviewable candidate on purpose — a pure
/// data noun (Rule 18.1). <see cref="Label"/> is the unit id the integration outcome names its contributions by
/// (<c>{node}#{iteration}</c>), so a reader can line a withheld unit up against the ones that landed;
/// <see cref="Reason"/> is the verdict that withheld it in fixed words, never free text: its acceptance grade
/// (<c>acceptance Failed</c> / <c>acceptance waived by the supervisor (not delivered)</c>), read off the ledger row, or
/// its output review's (<c>output review flagged it</c> / <c>output review did not approve it</c>), read off the run's result.
/// </summary>
public sealed record WithheldContribution(string Label, string Reason);

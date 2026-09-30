namespace CodeSpace.Messages.Agents;

/// <summary>
/// One unit of a run's produced work the integration step left OUT of the reviewable candidate on purpose — a pure
/// data noun (Rule 18.1). <see cref="Label"/> is the unit id the integration outcome names its contributions by
/// (<c>{node}#{iteration}</c>), so a reader can line a withheld unit up against the ones that landed;
/// <see cref="Reason"/> is the verdict that withheld it (<c>acceptance Failed</c> / <c>acceptance Waived</c>), read off the
/// ledger row and never free text.
/// </summary>
public sealed record WithheldContribution(string Label, string Reason);

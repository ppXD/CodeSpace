namespace CodeSpace.Messages.Agents;

/// <summary>
/// One contribution that LANDED on the integrated candidate although its own agent run did not succeed — a pure data
/// noun (Rule 18.1). <see cref="Label"/> is the unit id the integration outcome names its contributions by
/// (<c>{node}#{iteration}</c>); <see cref="AcceptancePassed"/> says whether its own acceptance check had passed before
/// the run failed, which decides what the diff on the branch is: verified work whose run failed afterwards, or a partial
/// nothing checked.
/// </summary>
public sealed record LandedFailedSubtask(string Label, bool AcceptancePassed);

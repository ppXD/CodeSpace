namespace CodeSpace.Messages.Agents;

/// <summary>
/// DC-1 (Universal Delivery Contract) — what this run should produce BEYOND the code change itself, compiled
/// once from the operator's own configuration and/or the model's plan-time proposal. A pure data noun (Rule
/// 18.1): every field is independently nullable so "unset" (nobody named an opinion) is distinguishable from an
/// explicit value, including an explicit <c>false</c> — an operator's "don't open a PR" must survive a model
/// that proposes the opposite (<see cref="Supervisor.SupervisorDeliveryClamp"/> is the enforcement of that
/// precedence; this record only carries the data).
///
/// <para>Deliberately narrow at DC-1: a single deliverable kind (the pull request), since it discharges the
/// concrete run-96695645 gap (accepted, published work with no PR and no way to get one without a human manually
/// clicking Room's Open-PR button). Later Arc DC slices generalize this into a typed multi-kind artifact ledger —
/// this noun's shape should not need to change, only grow new sibling fields.</para>
/// </summary>
public sealed record DeliverySpec
{
    /// <summary>Whether this run should automatically open a pull request once its work is published. Null = nobody expressed an opinion (byte-identical to no contract at all); false = an explicit "don't" that must survive even a model proposing true.</summary>
    public bool? OpenPullRequest { get; init; }

    /// <summary>The branch a requested pull request should target. Null = the bound repository's own default branch.</summary>
    public string? TargetBranch { get; init; }
}

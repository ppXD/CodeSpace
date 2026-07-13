namespace CodeSpace.Messages.Contracts;

/// <summary>
/// The requirement-kind REGISTRY (F0 / v4.1) — the only place a contract kind's wire key is minted. The completion
/// kernel (the reducer) never switches on a kind's SEMANTICS: it reads only the envelopes
/// (<see cref="RequirementEnvelope"/> / <see cref="ReceiptEnvelope"/>), and each kind's payload lives in its own
/// type outside the kernel. Registering a new kind = a new const here + its payload type + a pinning test + an
/// entry in the v4.1 appendix (the Rule-8 ritual) — never an edit to the reducer.
/// </summary>
public static class ContractKinds
{
    /// <summary>A unit/run's OBJECTIVE acceptance oracle (tests-pass argv, rubric, schema…) — the "is the work correct" contract.</summary>
    public const string Acceptance = "acceptance";

    /// <summary>The run's delivery obligation (open a pull request, land on a branch) — the "did the work ARRIVE" contract.</summary>
    public const string Delivery = "delivery";

    /// <summary>The run's expected output SHAPE (a git change, a typed artifact, explicitly nothing) — the "what kind of thing must exist" contract.</summary>
    public const string Output = "output";
}

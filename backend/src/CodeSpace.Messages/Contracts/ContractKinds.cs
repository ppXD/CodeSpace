namespace CodeSpace.Messages.Contracts;

/// <summary>
/// The requirement-kind REGISTRY (F0 / v4.1) — the only place a contract kind's wire key is minted. The completion
/// kernel (the reducer) never switches on a kind's SEMANTICS: it reads only the envelopes
/// (<see cref="RequirementEnvelope"/> / <see cref="ReceiptEnvelope"/>), and each kind's payload lives in its own
/// type outside the kernel. Registering a new kind = a new const here + its payload type + a pinning test + an
/// entry in the v4.1 appendix (the Rule-8 ritual) + a dimension route in the reducer — the kernel's five
/// dimensions are a FIXED projection, so a kind the reducer cannot route degrades the run's Outcome to
/// <c>Unknown</c> (fail-loud parking) rather than being silently invisible.
/// </summary>
public static class ContractKinds
{
    /// <summary>The kinds the completion kernel routes onto assessment dimensions. A REQUIRED requirement of any other kind cannot be projected — the reducer degrades the Outcome to Unknown so the obligation can never be silently dropped.</summary>
    public static readonly IReadOnlyList<string> Routed = new[] { Acceptance, Delivery, Output };

    /// <summary>A unit/run's OBJECTIVE acceptance oracle (tests-pass argv, rubric, schema…) — the "is the work correct" contract.</summary>
    public const string Acceptance = "acceptance";

    /// <summary>The run's delivery obligation (open a pull request, land on a branch) — the "did the work ARRIVE" contract.</summary>
    public const string Delivery = "delivery";

    /// <summary>The run's expected output SHAPE (a git change, a typed artifact, explicitly nothing) — the "what kind of thing must exist" contract.</summary>
    public const string Output = "output";
}

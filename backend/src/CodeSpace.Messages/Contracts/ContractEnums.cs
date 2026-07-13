using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Contracts;

/// <summary>Whether an unmet requirement blocks the terminal (F0). <see cref="Optional"/> never blocks — it only lowers the recorded dimension.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Requiredness
{
    Required,
    Optional,
}

/// <summary>
/// WHO stands behind a contract field or a receipt's verdict (F0). The model is ALWAYS <see cref="ModelProposal"/> —
/// a model may propose, never authorize; <c>NoOutputExpected</c> and every waiver require <see cref="Operator"/> or
/// <see cref="ServerPolicy"/>; heuristics (verb inference and kin) only ever LINT, they never mint authority.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractAuthority
{
    Operator,
    ServerPolicy,
    ModelProposal,
}

/// <summary>
/// The TYPED verification verdict (F0) — replaces the lossy <c>bool?</c> + detail-prefix convention everywhere a
/// consumer must not conflate "the check ran and failed" with "the check machinery never functioned".
/// <para>Mapping from today's signals: <c>AcceptancePassed=true</c> (an oracle that RAN) → <see cref="Passed"/>;
/// <c>false</c> + an infra-classed detail (<c>AgentAcceptanceContract.IsInfraFailure</c>) → <see cref="InfraUnknown"/>;
/// <c>false</c> otherwise → <see cref="Failed"/>; a null (never graded) → <see cref="Unknown"/>.
/// <see cref="NotApplicable"/> is reserved for the vacuous-pass reclassification (today a vacuous pass writes
/// <c>Passed=true</c>; reclassifying it SHIFTS IsSolved and is its own test-pinned PR, never a rider).
/// <see cref="Waived"/> is a human-authorized "forgo verification" — NEVER equal to <see cref="Passed"/> at any
/// objective-truth read point (the amend-acceptance FATAL-1 invariant).</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerificationDisposition
{
    Passed,
    Failed,
    NotApplicable,
    InfraUnknown,
    HumanReviewRequired,
    Waived,
    Unknown,
}

/// <summary>What KIND of output the run is expected to produce (F0). <see cref="NoOutputExpected"/> is only valid under <see cref="ContractAuthority.Operator"/>/<see cref="ContractAuthority.ServerPolicy"/> authority.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputExpectation
{
    GitChange,
    TypedArtifact,
    NoOutputExpected,
    HumanReviewRequired,
}

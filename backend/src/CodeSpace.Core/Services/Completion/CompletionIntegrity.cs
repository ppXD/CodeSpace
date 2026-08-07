using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// P1 (fail-close): the integrity violations that make an Enforced SUCCESS claim unstampable — evidence whose
/// identity or contract vocabulary cannot be verified must park for a human, never fold silently under a green.
/// Three sources, every one already carried by the compose (nothing is re-derived here):
/// <list type="bullet">
/// <item>an IDENTITY-CLASS admission warning — an identity-less receipt (<see cref="ReceiptRejectionCodes.MissingIdentity"/>)
/// was admitted into the fold under Legacy/Shadow tolerance; Lock Clause 3 names it Enforced-fatal, and THIS is that
/// refusal (the admission membrane itself stays mode-blind by design). Other warnings are deliberately NOT
/// violations: an unevidenced pass (<see cref="ReceiptRejectionCodes.MissingEvidence"/>) is already capped at
/// InfraUnknown by admission, so its honest degradation reaches the decision on its own;</item>
/// <item>an adapter CONTRACT ERROR — a ghost attempt / positional break the projection surfaced rather than silently
/// truncated (its own contract: "surfaced per policy mode downstream; Enforced → park");</item>
/// <item>an UNSUPPORTED contract schema version on any staked requirement — an obligation whose vocabulary this
/// code does not speak cannot be verified, so it can never back a Success.</item>
/// </list>
/// Hard rejections are NOT violations: a superseded attempt's receipt or an orphan ref is dropped from the fold
/// entirely, so the obligation it failed to answer stays owed and the decision already reflects it.
/// Pure and deterministic. The terminal authority and the shadow's would-be decision share this ONE predicate, so
/// the accumulated parity evidence predicts exactly what Enforced will do — a rule enforced in one place and
/// mirrored in none.
/// </summary>
public static class CompletionIntegrity
{
    /// <summary>The contract schema version this reducer/admission vocabulary speaks — bump WITH the reader, never alone.</summary>
    public const string SupportedContractSchemaVersion = "1";

    public static IReadOnlyList<string> Violations(IReadOnlyList<ReceiptRejection> rejections, IReadOnlyList<string> contractErrors, IReadOnlyList<RequirementEnvelope> requirements)
    {
        var violations = new List<string>();

        foreach (var rejection in rejections)
            if (rejection.Code == ReceiptRejectionCodes.MissingIdentity)
                violations.Add($"identity-less receipt for '{rejection.Receipt.RequirementRef}' was folded under Shadow tolerance — Enforced refuses evidence carrying no WorkUnitRef (Lock Clause 3)");

        foreach (var error in contractErrors)
            violations.Add($"contract error: {error}");

        foreach (var requirement in requirements)
            if (requirement.ContractSchemaVersion != SupportedContractSchemaVersion)
                violations.Add($"requirement '{requirement.RequirementRef}' carries unsupported contract schema version '{requirement.ContractSchemaVersion}' — an obligation this code cannot verify can never back a Success");

        return violations;
    }
}

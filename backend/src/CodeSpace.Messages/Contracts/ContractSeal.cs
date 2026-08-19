namespace CodeSpace.Messages.Contracts;

/// <summary>
/// The verifier + model identities a qualification round ran under — the claim is only as strong as the bundle
/// that judged it. Properties are required-but-nullable: a mint always RECORDS the selection verbatim (an unset
/// harness is an honest null, never an omission), while legacy ad-hoc json missing the keys parses to no bundle
/// at all.
/// </summary>
public sealed record VerifierBundle
{
    public required string? Harness { get; init; }

    public required string? Model { get; init; }

    public Guid? ModelCredentialId { get; init; }
}

/// <summary>
/// Q5 — layer 2 of the qualification identity split: WHAT was measured and WHO judged it — the capability
/// surface, the sealed suite's content digest (the generation: a later suite edit can never claim this seal's
/// number), and the verifier bundle. Composed at read time from the backing receipt's own columns, so the seal
/// can never drift from the row it summarizes.
/// </summary>
public sealed record ContractSeal
{
    public required string CapabilityKey { get; init; }

    /// <summary>The hidden suite's content digest — WHICH tasks earned the standing.</summary>
    public required string SuiteDigest { get; init; }

    /// <summary>The judging identities — null when the backing receipt predates the typed bundle (legacy ad-hoc json).</summary>
    public VerifierBundle? VerifierBundle { get; init; }
}

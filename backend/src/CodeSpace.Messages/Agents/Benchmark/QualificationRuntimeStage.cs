namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// The point in a paid paired qualification campaign at which the live runtime was compared against the campaign's
/// frozen <see cref="QualificationRuntimeManifest"/>. Every member is a place where the campaign either DOES paid
/// work or SEALS it into a capability claim — the only places where a substituted runtime could pool two different
/// experiments into one census.
///
/// <para>The stage travels on <c>RuntimeManifestDriftException</c> so an operator reading the refusal knows WHERE
/// the substitution was caught, which decides what they do next: a refusal at <see cref="Admission"/> or
/// <see cref="Execution"/> means no cell was paid for on the wrong runtime, while one at <see cref="Seal"/> means a
/// complete campaign is waiting on the host it was measured on.</para>
///
/// <para>Deliberately NOT a member: replaying a complete campaign's already-sealed observation rows. That path
/// makes no model call and mints no new measurement — the evidence it reduces was gated at <see cref="Admission"/>
/// and <see cref="Execution"/> when it was produced — so refusing it would strand a fully-paid campaign on a host
/// whose runtime moved after its last cell.</para>
/// </summary>
public enum QualificationRuntimeStage
{
    /// <summary>Committing one cell's immutable identity, immediately before it becomes payable.</summary>
    Admission,

    /// <summary>Entering one cell's execution, before the launch that makes the first model call.</summary>
    Execution,

    /// <summary>Adopting an interrupted campaign that will EXECUTE at least one absent cell.</summary>
    Resume,

    /// <summary>Sealing a campaign this process executed into its terminal, immutable result.</summary>
    Seal,
}

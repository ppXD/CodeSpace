using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One model on a <see cref="ModelCredential"/>'s maintained list — the credential-rooted unit of "a model this
/// team can actually run". FK-rooted under the credential ON PURPOSE: a model cannot exist without a backing
/// credential, so "a model with no key" is structurally impossible rather than a runtime guard. The team's
/// usable model pool is the union of its active credentials' <see cref="Enabled"/> rows.
///
/// <para>Carries the per-model capability boundary — the single <see cref="SupportsStructuredOutput"/> flag the
/// scheduler gates on (the decider/planner need structured output; a free-text run does not). A capability is the
/// MODEL's, not the credential's (one key backs models of differing capability) nor the harness's (the harness only
/// constrains which models it can invoke). A new orthogonal capability arrives as a new flag here only when a real
/// reader lands — never speculatively.</para>
/// </summary>
public class ModelCredentialModel : IEntity<Guid>
{
    public Guid Id { get; set; }

    /// <summary>The backing credential (FK, cascade-delete: revoking a key removes its models).</summary>
    public Guid ModelCredentialId { get; set; }

    /// <summary>The wire model id the harness/decider passes (e.g. "claude-sonnet-4-5", "gpt-5.4-codex"). Unique PER credential, not globally.</summary>
    public string ModelId { get; set; } = default!;

    /// <summary>Operator-friendly label; null → show <see cref="ModelId"/>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Whether the operator typed this model (<see cref="ModelSource.Manual"/>) or a refresh reflected it (<see cref="ModelSource.Reflected"/>). The refresh upsert preserves Manual rows.</summary>
    public ModelSource Source { get; set; } = ModelSource.Manual;

    /// <summary>Capability: the model can return structured / JSON-schema output — the decider/planner eligibility gate (the one capability the scheduler reads).</summary>
    public bool SupportsStructuredOutput { get; set; }

    /// <summary>Operator soft-hide without deleting — a disabled row is not part of the usable pool. Defaults true (a freshly added model is usable).</summary>
    public bool Enabled { get; set; } = true;

    public ModelCredential Credential { get; set; } = default!;
}

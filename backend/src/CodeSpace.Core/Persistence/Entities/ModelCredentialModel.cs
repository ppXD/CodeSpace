using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One model on a <see cref="ModelCredential"/>'s maintained list — the credential-rooted unit of "a model this
/// team can actually run". FK-rooted under the credential ON PURPOSE: a model cannot exist without a backing
/// credential, so "a model with no key" is structurally impossible rather than a runtime guard. The team's
/// usable model pool is the union of its active credentials' <see cref="Enabled"/> rows.
///
/// <para>The pool is capability-GENERIC: it carries no "supports X" flag. Structured output is the client's job
/// (<c>IStructuredLLMClient</c> degrades a model that doesn't honour forced tool-use to a prompt-only JSON floor), so
/// any enabled credentialed model is selectable for any in-process node — a genuinely-incapable model fails at the
/// call, never as a pre-filter. (Dify's model-node model: no per-model capability gate.)</para>
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

    /// <summary>Operator soft-hide without deleting — a disabled row is not part of the usable pool. Defaults true (a freshly added model is usable).</summary>
    public bool Enabled { get; set; } = true;

    public ModelCredential Credential { get; set; } = default!;
}

using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One row in the unified <c>variable</c> table. Replaces the old <c>TeamSecret</c> entity
/// and the workflow-JSON <c>variables[]</c> / <c>environment[]</c> arrays. Two orthogonal
/// axes:
///
///   • <see cref="Scope"/>     — who owns the variable (<see cref="VariableScope.Team"/>
///                                or <see cref="VariableScope.Workflow"/>).
///                                <see cref="ScopeId"/> points to that owner's id.
///   • <see cref="ValueType"/> — what kind of value it is. <see cref="VariableValueType.Secret"/>
///                                routes through <see cref="ValueEncrypted"/>; everything
///                                else routes through <see cref="ValuePlain"/>.
///
/// The DB CHECK constraint <c>chk_variable_value_exclusive</c> guarantees exactly one
/// value column is populated, matching the ValueType. Application code does NOT need to
/// defensively null-check both columns — the constraint makes inconsistent rows impossible.
///
/// <para><see cref="TeamId"/> is denormalised even for Workflow scope so tenant-filter
/// sweeps (the X-Team-Id middleware checks) work without joining workflow.</para>
/// </summary>
public class Variable : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public VariableScope Scope { get; set; }

    /// <summary>
    /// Polymorphic owner FK. <see cref="VariableScope.Team"/> → team.id;
    /// <see cref="VariableScope.Workflow"/> → workflow.id. No DB-level FK — application
    /// layer enforces referential integrity at write time.
    /// </summary>
    public Guid ScopeId { get; set; }

    /// <summary>
    /// Owning team. Always set, even for Workflow scope (denormalised from workflow.team_id).
    /// Drives tenant filtering without forcing a JOIN.
    /// </summary>
    public Guid TeamId { get; set; }

    /// <summary>The <c>{{wf.&lt;Name&gt;}}</c> / <c>{{team.&lt;Name&gt;}}</c> reference. Unique per active row per (Scope, ScopeId).</summary>
    public string Name { get; set; } = default!;

    public VariableValueType ValueType { get; set; }

    /// <summary>
    /// JSON-encoded plaintext value when <see cref="ValueType"/> is NOT
    /// <see cref="VariableValueType.Secret"/>. Null for secrets. Stored as JSON so non-string
    /// types round-trip without loss (Number stays numeric, Object/Array preserve structure).
    /// </summary>
    public string? ValuePlain { get; set; }

    /// <summary>
    /// AES-256-GCM envelope <c>[nonce(12) || ciphertext || tag(16)]</c> when
    /// <see cref="ValueType"/> is <see cref="VariableValueType.Secret"/>. Null otherwise.
    /// </summary>
    public byte[]? ValueEncrypted { get; set; }

    /// <summary>Optional one-line operator hint. Not consumed by the engine.</summary>
    public string? Description { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }

    public Team Team { get; set; } = default!;
}

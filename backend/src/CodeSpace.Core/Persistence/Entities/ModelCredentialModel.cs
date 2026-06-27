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

    /// <summary>The operator's preferred model for an "auto" run (no pinned model) — the resolver orders default-marked rows first in the pool pick. At most one per credential (the service clears the others when one is set). Defaults false.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Cached, advisory coding-capability tier inferred by the brain from <see cref="ModelId"/> (an ALLOCATION HINT surfaced in the capability catalog, never a selection gate). Null / <see cref="ModelCapabilityTier.Unknown"/> = not yet tiered, or an opaque id the brain couldn't recognise (the later-probe hook).</summary>
    public ModelCapabilityTier? CapabilityTier { get; set; }

    /// <summary>When <see cref="CapabilityTier"/> was last written — the staleness gate so re-tiering is a cached fact, never a per-launch call. Null = never tiered.</summary>
    public DateTimeOffset? LastTieredAt { get; set; }

    /// <summary>Cached, advisory REACHABILITY of this model's endpoint (a SEPARATE axis from <see cref="CapabilityTier"/>): a recurring probe pings each self-hosted Custom-gateway model — true = the endpoint responded (any HTTP status, even auth/rate-limit), false = a genuine no-response transport failure (connection refused / reset / timeout). NULL = never probed (vendor models are not pinged) = ASSUMED AVAILABLE. A SOFT auto-pick hint (an unpinned pick prefers available != false, falling back to all if none), never a hard pool filter; pins / dispatch / catalog ignore it.</summary>
    public bool? Available { get; set; }

    /// <summary>When <see cref="Available"/> was last probed — the staleness gate (a 30-minute re-probe window; availability is volatile, so unlike <see cref="LastTieredAt"/> it RE-evaluates). Null = never probed.</summary>
    public DateTimeOffset? LastPingedAt { get; set; }

    /// <summary>Cached, advisory capability tier OBJECTIVELY probed for an OPAQUE id (<see cref="CapabilityTier"/> == <see cref="ModelCapabilityTier.Unknown"/>): the model DEMONSTRATES capability on a fixed in-code micro-battery, graded in code (never self-rated). Kept SEPARATE from <see cref="CapabilityTier"/> so the brain-vs-probe provenance stays legible. Caps at <see cref="ModelCapabilityTier.Strong"/> (never Frontier — a small battery can't separate the two) and only ever UPGRADES (monotonic). The auto pick reads the EFFECTIVE tier = this ?? <see cref="CapabilityTier"/>, so a probed Strong lifts a capable opaque model off last place. Null = not probed.</summary>
    public ModelCapabilityTier? ProbedCapabilityTier { get; set; }

    /// <summary>When <see cref="ProbedCapabilityTier"/> was last probed — the staleness gate (a days-long re-probe window; an opaque alias's backing model rarely changes). Null = never probed.</summary>
    public DateTimeOffset? LastProbedCapabilityAt { get; set; }

    public ModelCredential Credential { get; set; } = default!;
}

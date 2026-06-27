using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// Picks an in-process LLM call's model + its backing credential ENTIRELY from the team's credentialed-model POOL (the
/// <c>ModelCredentialModel</c> rows S1–S3 build) — the one mechanism every in-process caller shares: the supervisor
/// decider, the workflow planner, the <c>llm.complete</c> node, the supervisor synthesis. A qualifying row is an
/// ENABLED model under an ACTIVE credential of the requested provider, bounded by the operator's allowed-models pool
/// (null/empty = all the team's models), pinned to one model when the caller has one, else by the precedence ladder
/// (operator <c>IsDefault</c> #746 → cached capability tier #762 → model id → row id). The pool is
/// provider+credential GENERIC — the tier is an advisory ORDERING hint, never a selection gate: structured
/// output is the client's job (<c>IStructuredLLMClient</c> degrades a model that doesn't honour forced tool-use to its
/// prompt-only JSON floor), so any credentialed model is selectable and a genuinely-incapable one fails at call time,
/// never as a pre-filter. The chosen row's backing credential is decrypted just-in-time. <c>null</c> = nothing
/// qualifies → the caller fails closed. NO env "system credential" fallback and NO hardcoded system default — the
/// operator's per-credential <c>IsDefault</c> is the only default, and it orders an unpinned pick (the brain plane now
/// honours it the same way the agent plane does).
/// </summary>
public interface IModelPoolSelector
{
    /// <summary>
    /// Select a model + credential for <paramref name="provider"/> (the client the caller will invoke), bounded by
    /// <paramref name="allowedModels"/> (null/empty = all), pinned to <paramref name="pinnedModel"/> if set, else the
    /// operator's per-credential default (<c>IsDefault</c>) first, then a deterministic order (model id, then row id).
    /// Null when nothing qualifies. Provider + model-id matching is case-insensitive (parity with the agent-side resolver + the spawn clamp).
    /// </summary>
    Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve ONE credentialed-model row the operator picked by id (the supervisor's brain model) → its model id + the
    /// decrypted backing credential. Team-scoped, must be ENABLED under an ACTIVE credential. <c>null</c> when the row is
    /// missing / disabled / revoked / not the team's → the caller fails closed. Unambiguous by construction: a row id
    /// names exactly one (model, credential) pair, so the same model id under two credentials is never a guess.
    /// </summary>
    Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve a supervisor-dispatched agent's effective model NAME (the L4 model authors a name, not a row id) to a
    /// credentialed-model row → its canonical model id + the BACKING credential id (the agent plane resolves the key
    /// from that id at execution; this never decrypts). Bounded to the operator's allowed pool when
    /// <paramref name="allowedRowIds"/> is non-empty; null/empty = ALL the team's credentialed models. The row must be
    /// ENABLED under an ACTIVE credential. <c>null</c> = the name is not a credentialed model in the (bounded) pool →
    /// the spawn fails closed. So a dispatched agent can only run a model the team credentialed, on that model's own key.
    /// </summary>
    Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken);

    /// <summary>
    /// List the team's credentialed pool models (model id + provider) the brain MAY dispatch — bounded to
    /// <paramref name="allowedRowIds"/> (null/empty = ALL the team's enabled rows under active credentials). For
    /// rendering the capability catalog into the supervisor/planner prompt so the model authors a provider-compatible
    /// (harness, model) pair on purpose, not blind. De-duplicated by (model id, provider).
    /// </summary>
    Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken);

    /// <summary>
    /// Auto-pick ONE enabled credentialed-model ROW id to run the supervisor's BRAIN on, when the operator pinned none
    /// (the Deep/Auto lane). Bounded to <paramref name="eligibleProviders"/> — the providers a structured-LLM client
    /// actually serves — so a self-resolved brain never trades <c>NoBrainModelStop</c> for <c>NoModelStop</c>. Returns
    /// the row id (the decider resolves the brain BY row id) of the FIRST match in precedence order — operator
    /// <c>IsDefault</c> → cached capability tier (strongest first) → model id / row id — so a starred eligible model wins,
    /// else the strongest, and a replay re-derives the SAME brain. Null when no enabled row under an active credential has
    /// an eligible provider (the honest fail-closed floor — nothing to run the brain on).
    /// </summary>
    Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken);

    /// <summary>
    /// Validate an OPERATOR-PINNED brain model (the Launch "Brain model" chip → one <c>ModelCredentialModel</c> row) for
    /// the Deep/Auto supervisor: returns <paramref name="modelCredentialModelId"/> verbatim IFF it is a real ENABLED row
    /// under an ACTIVE team credential whose provider a structured-LLM client serves (in <paramref name="eligibleProviders"/>);
    /// else <c>null</c>. Same enabled/active/structured-eligible guards as <see cref="SelectBrainRowIdAsync"/>, so a pinned
    /// brain and an auto brain are interchangeable — a missing / disabled / revoked / cross-team / non-structured pin
    /// resolves to <c>null</c> and the caller falls back to the auto ladder rather than baking a brain the decider would
    /// <c>NoBrainModelStop</c> on (a working brain beats a turn-1 stop). Pure read; never decrypts.
    /// </summary>
    Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken);

    /// <summary>
    /// The PROVIDER tag of the model an UN-pinned agent would auto-run — the team's top ENABLED pool row in the credential
    /// resolver's precedence order (<c>IsDefault</c> &gt; model id &gt; row id) but provider-AGNOSTIC (no harness filter), so
    /// a no-pin task can DERIVE its harness from the model it will actually get instead of defaulting blind. Only the
    /// provider tag (no decrypt). <c>null</c> when the team has no enabled pool model under an active credential (the
    /// caller keeps its harness floor). Agrees with <c>ModelCredentialResolver</c>'s pick: the derived harness drives this
    /// provider, so the harness-filtered resolve picks the SAME row.
    /// </summary>
    Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken);
}

/// <summary>One pooled model the brain may dispatch — its canonical id, the provider tag whose harness can drive it, and its cached advisory capability <see cref="ModelCapabilityTier"/> (default <see cref="ModelCapabilityTier.Unknown"/> when un-tiered). Catalog-only (no credential, no secret).</summary>
public sealed record PoolModelInfo(string ModelId, string Provider, ModelCapabilityTier Tier = ModelCapabilityTier.Unknown);

/// <summary>The agent-dispatch resolution: the canonical model id + the BACKING credential's id. Distinct from <see cref="ModelPoolPick"/> (the in-process plane, which carries the decrypted key) — the agent plane wants the credential REFERENCE (a Guid it resolves to env at execution), never the secret in-process.</summary>
public sealed record ModelDispatchRef
{
    public required string ModelId { get; init; }

    public required Guid ModelCredentialId { get; init; }

    /// <summary>The resolved credential's provider tag — lets the spawn path author a harness that can actually drive this model (the authoring-time compatibility clamp), instead of leaving a mismatch for the run-time reconciler.</summary>
    public required string Provider { get; init; }
}

/// <summary>The resolved pick: a model id from the pool + the decrypted credential of the row that backs it. Transient — carries a secret, never persisted.</summary>
public sealed record ModelPoolPick
{
    public required string ModelId { get; init; }

    public required ResolvedModelCredential Credential { get; init; }
}

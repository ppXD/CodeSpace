using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// Picks an in-process LLM call's model + its backing credential ENTIRELY from the team's credentialed-model POOL (the
/// <c>ModelCredentialModel</c> rows S1–S3 build) — the one mechanism every in-process caller shares: the supervisor
/// decider, the workflow planner, the <c>llm.complete</c> node, the supervisor synthesis. A qualifying row is an
/// ENABLED model under an ACTIVE credential of the requested provider, optionally narrowed to structured-output-capable
/// (the decider/planner need it; a free-text reduce does not), bounded by the operator's allowed-models pool
/// (null/empty = all the team's models), pinned to one model when the caller has one, preferring a supervisor-
/// recommended model. The chosen row's backing credential is decrypted just-in-time. <c>null</c> = nothing qualifies →
/// the caller fails closed. There is NO env "system credential" fallback and NO default model anywhere on this path.
/// </summary>
public interface IModelPoolSelector
{
    /// <summary>
    /// Select a model + credential for <paramref name="provider"/> (the client the caller will invoke), narrowed to
    /// structured-capable when <paramref name="requireStructured"/>, bounded by <paramref name="allowedModels"/>
    /// (null/empty = all), pinned to <paramref name="pinnedModel"/> if set, preferring a recommended one. Null when
    /// nothing qualifies. Provider + model-id matching is case-insensitive (parity with the agent-side resolver + the
    /// spawn clamp).
    /// </summary>
    Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, bool requireStructured, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve ONE credentialed-model row the operator picked by id (the supervisor's brain model) → its model id + the
    /// decrypted backing credential. Team-scoped, must be ENABLED under an ACTIVE credential, and (when
    /// <paramref name="requireStructured"/>) structured-output-capable. <c>null</c> when the row is missing / disabled /
    /// revoked / not the team's / not structured → the caller fails closed. Unambiguous by construction: a row id names
    /// exactly one (model, credential) pair, so the same model id under two credentials is never a guess.
    /// </summary>
    Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, bool requireStructured, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve a supervisor-dispatched agent's effective model NAME (the L4 model authors a name, not a row id) to a
    /// credentialed-model row → its canonical model id + the BACKING credential id (the agent plane resolves the key
    /// from that id at execution; this never decrypts). Bounded to the operator's allowed pool when
    /// <paramref name="allowedRowIds"/> is non-empty; null/empty = ALL the team's credentialed models. The row must be
    /// ENABLED under an ACTIVE credential. <c>null</c> = the name is not a credentialed model in the (bounded) pool →
    /// the spawn fails closed. So a dispatched agent can only run a model the team credentialed, on that model's own key.
    /// </summary>
    Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken);
}

/// <summary>The agent-dispatch resolution: the canonical model id + the BACKING credential's id. Distinct from <see cref="ModelPoolPick"/> (the in-process plane, which carries the decrypted key) — the agent plane wants the credential REFERENCE (a Guid it resolves to env at execution), never the secret in-process.</summary>
public sealed record ModelDispatchRef
{
    public required string ModelId { get; init; }

    public required Guid ModelCredentialId { get; init; }
}

/// <summary>The resolved pick: a model id from the pool + the decrypted credential of the row that backs it. Transient — carries a secret, never persisted.</summary>
public sealed record ModelPoolPick
{
    public required string ModelId { get; init; }

    public required ResolvedModelCredential Credential { get; init; }
}
